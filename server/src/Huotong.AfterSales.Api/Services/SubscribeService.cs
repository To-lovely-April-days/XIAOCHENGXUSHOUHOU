using System.Text;
using System.Text.Json;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Api.Services;

/// <summary>一条提醒：发给哪些人、各语义槽的内容。To 里可以有 null / 停用 / 未绑定微信 / 操作人本人，由实现统一过滤</summary>
public record SubscribeNotice(int OrderId, IReadOnlyList<Employee?> To, SubscribeSlots Slots);

/// <summary>实际交给微信的一条订阅消息（MockMode 下只记录不发送）</summary>
public record SubscribeMessage(string OpenId, string TemplateId, string Page, string MiniProgramState, string Lang,
    IReadOnlyDictionary<string, string> Data);

public interface ISubscribeNotifier
{
    /// <summary>是否已启用（配置了模板 ID 且至少映射了一个字段）</summary>
    bool Enabled { get; }

    /// <summary>小程序端 wx.requestSubscribeMessage 要申请的模板 ID；未启用时为空</summary>
    IReadOnlyList<string> TemplateIds { get; }

    /// <summary>
    /// 发送一次业务动作产生的提醒。实现必须吞掉异常并自带超时，不能影响主流程；未启用时什么都不做。
    /// 收件人过滤：启用状态、已绑定微信（OpenId 非空）、不是操作人本人；同一个人在一次调用里只收第一条。
    /// </summary>
    Task NotifyAsync(int actorId, IReadOnlyList<SubscribeNotice> notices);
}

/// <summary>不发送任何提醒（测试里不关心订阅消息时用）</summary>
public sealed class NullSubscribeNotifier : ISubscribeNotifier
{
    public static readonly NullSubscribeNotifier Instance = new();
    public bool Enabled => false;
    public IReadOnlyList<string> TemplateIds => Array.Empty<string>();
    public Task NotifyAsync(int actorId, IReadOnlyList<SubscribeNotice> notices) => Task.CompletedTask;
}

/// <summary>
/// 微信小程序一次性订阅消息（v1.4）。
/// access_token 走 stable_token 接口，内存缓存到过期前 5 分钟，并发时只取一次；与 WeChatService（手机号验证）用的 token 互相独立、互不影响。
/// MockMode 下不发任何网络请求，只把将要发送的消息记进 <see cref="MockSent"/>。
/// 日志里不出现 access_token 与 AppSecret。
/// </summary>
public class SubscribeService : ISubscribeNotifier
{
    private const string TokenUrl = "https://api.weixin.qq.com/cgi-bin/stable_token";
    private const string SendUrl = "https://api.weixin.qq.com/cgi-bin/message/subscribe/send";
    private const int MockKeep = 500;

    /// <summary>用户没订阅 / 额度用完（43101）、订阅消息能力被限制（43107）：属于正常情况，不算故障</summary>
    private static readonly HashSet<int> ExpectedRefusals = new() { 43101, 43107 };

    private readonly IHttpClientFactory _http;
    private readonly WeChatOptions _opt;
    private readonly ILogger<SubscribeService> _logger;

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private volatile CachedToken? _token;
    private readonly List<SubscribeMessage> _mockSent = new();

    private sealed record CachedToken(string Value, DateTimeOffset GoodUntil);

    private sealed record Outgoing(SubscribeMessage Message, string Name, string Title);

    public SubscribeService(IHttpClientFactory http, IOptions<WeChatOptions> opt, ILogger<SubscribeService> logger)
    {
        _http = http;
        _opt = opt.Value;
        _logger = logger;
    }

    /// <summary>一次业务动作的全部发送（含取 token）最多等这么久，超时放弃</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>当前时间（测试可替换，用来验证 token 缓存的过期）</summary>
    public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public bool Enabled => _opt.Subscribe.Enabled;

    public IReadOnlyList<string> TemplateIds => Enabled ? new[] { _opt.Subscribe.TemplateId.Trim() } : Array.Empty<string>();

    /// <summary>MockMode 下「将要发送」的消息（按时间顺序，最多保留最近 500 条）</summary>
    public IReadOnlyList<SubscribeMessage> MockSent
    {
        get { lock (_mockSent) return _mockSent.ToList(); }
    }

    public void ClearMockSent()
    {
        lock (_mockSent) _mockSent.Clear();
    }

    public async Task NotifyAsync(int actorId, IReadOnlyList<SubscribeNotice> notices)
    {
        if (!Enabled) return;
        try
        {
            var outgoing = Compose(actorId, notices);
            if (outgoing.Count == 0) return;

            if (_opt.MockMode)
            {
                lock (_mockSent)
                {
                    _mockSent.AddRange(outgoing.Select(x => x.Message));
                    if (_mockSent.Count > MockKeep) _mockSent.RemoveRange(0, _mockSent.Count - MockKeep);
                }
                foreach (var x in outgoing)
                    _logger.LogInformation("[订阅消息·Mock] {Title} → {Name} | {Page} | {Data}", x.Title, x.Name, x.Message.Page,
                        string.Join("；", x.Message.Data.Select(kv => $"{kv.Key}={kv.Value}")));
                return;
            }
            // 没配 AppId / AppSecret 时发不了（启动时已有警告），不逐条报错
            if (string.IsNullOrWhiteSpace(_opt.AppId) || string.IsNullOrWhiteSpace(_opt.AppSecret)) return;

            var cts = new CancellationTokenSource(SendTimeout);
            try
            {
                await SendAll(outgoing, cts.Token).WaitAsync(cts.Token);
                cts.Dispose();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // 不 Dispose：还没结束的发送任务仍持有这个取消令牌，它们会随取消自行结束。
                // 超时只在这里记一条：不管是 WaitAsync 先取消，还是发送任务自己先被取消，都会走到这里
                _logger.LogWarning("订阅消息发送超过 {Seconds} 秒，已放弃（不影响业务）", SendTimeout.TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("订阅消息发送异常（已忽略）：{Type} {Message}", ex.GetType().Name, Redact(ex.Message, null));
        }
    }

    /// <summary>过滤收件人并生成每个人的消息；同一个人（同一 OpenId）在一次调用里只收第一条</summary>
    private List<Outgoing> Compose(int actorId, IReadOnlyList<SubscribeNotice> notices)
    {
        var cfg = _opt.Subscribe;
        var templateId = cfg.TemplateId.Trim();
        var state = string.IsNullOrWhiteSpace(cfg.MiniProgramState) ? "trial" : cfg.MiniProgramState.Trim();
        var seen = new HashSet<string>();
        var list = new List<Outgoing>();
        foreach (var n in notices)
        {
            Dictionary<string, string>? data = null;
            foreach (var e in n.To)
            {
                if (e == null || !e.Enabled || e.Id == actorId || string.IsNullOrWhiteSpace(e.OpenId)) continue;
                if (!seen.Add(e.OpenId)) continue;
                data ??= SubscribeFields.Build(cfg.Fields, n.Slots);
                var page = (cfg.Page ?? "").Trim().Replace("{id}", n.OrderId.ToString());
                list.Add(new Outgoing(new SubscribeMessage(e.OpenId, templateId, page, state, "zh_CN", data), e.Name, n.Slots.Title));
            }
        }
        return list;
    }

    /// <summary>
    /// 一次动作的全部发送。先取一次 token：取不到（密钥不对、IP 不在白名单、网络不通）就整批不发，
    /// 只请求一次、只记一条日志，而不是每个收件人各去取一次、各记一条。
    /// 超时（取消）不在这里吞掉，交给 NotifyAsync 统一记一条。
    /// </summary>
    private async Task SendAll(List<Outgoing> outgoing, CancellationToken ct)
    {
        if (await AccessToken(ct) == null) return; // 已记日志
        await Task.WhenAll(outgoing.Select(x => SendOne(x, ct)));
    }

    private async Task SendOne(Outgoing x, CancellationToken ct)
    {
        string? token = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                token = await AccessToken(ct);
                if (token == null) return; // 取 token 失败，已记日志
                var (code, msg) = await PostSend(token, x.Message, ct);
                if (code == 0)
                {
                    _logger.LogInformation("订阅消息已发送：{Title} → {Name}", x.Title, x.Name);
                    return;
                }
                // access_token 失效：清缓存、重新取、重试一次
                if (code is 40001 or 42001 or 40014 && attempt == 0)
                {
                    Invalidate(token);
                    continue;
                }
                if (ExpectedRefusals.Contains(code))
                    _logger.LogInformation("订阅消息未送达：{Title} → {Name}（{Code}，对方没有订阅或额度已用完）", x.Title, x.Name, code);
                else
                    _logger.LogWarning("订阅消息发送失败：{Title} → {Name}，errcode={Code} {Message}", x.Title, x.Name, code, Redact(msg, token));
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 超时（ct 被取消）不在这里处理：往上抛，由 NotifyAsync 统一记一条
            _logger.LogWarning("订阅消息发送异常（已忽略）：{Title} → {Name}，{Type} {Message}", x.Title, x.Name, ex.GetType().Name, Redact(ex.Message, token));
        }
    }

    private async Task<(int Code, string Message)> PostSend(string token, SubscribeMessage m, CancellationToken ct)
    {
        var body = new
        {
            touser = m.OpenId,
            template_id = m.TemplateId,
            page = m.Page,
            miniprogram_state = m.MiniProgramState,
            lang = m.Lang,
            data = m.Data.ToDictionary(kv => kv.Key, kv => new { value = kv.Value })
        };
        var client = _http.CreateClient("wechat");
        using var resp = await client.PostAsync($"{SendUrl}?access_token={Uri.EscapeDataString(token)}", Json(body), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var code = root.TryGetProperty("errcode", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0;
            var msg = root.TryGetProperty("errmsg", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() ?? "" : "";
            if (code == 0 && !resp.IsSuccessStatusCode) return (-(int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}");
            return (code, msg);
        }
        catch (JsonException)
        {
            return (-(int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}，返回内容不是 JSON");
        }
    }

    // ---------- access_token ----------

    /// <summary>取（或复用缓存的）access_token；失败返回 null 并记日志。并发调用只会有一个真的去取</summary>
    private async Task<string?> AccessToken(CancellationToken ct)
    {
        var cached = _token;
        if (cached != null && UtcNow() < cached.GoodUntil) return cached.Value;
        await _tokenGate.WaitAsync(ct);
        try
        {
            cached = _token;
            if (cached != null && UtcNow() < cached.GoodUntil) return cached.Value;

            var client = _http.CreateClient("wechat");
            using var resp = await client.PostAsync(TokenUrl,
                Json(new { grant_type = "client_credential", appid = _opt.AppId, secret = _opt.AppSecret }), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            string? token = null;
            int expires = 7200, code = 0;
            var msg = "";
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String) token = t.GetString();
                if (root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number) expires = e.GetInt32();
                if (root.TryGetProperty("errcode", out var ec) && ec.ValueKind == JsonValueKind.Number) code = ec.GetInt32();
                if (root.TryGetProperty("errmsg", out var em) && em.ValueKind == JsonValueKind.String) msg = em.GetString() ?? "";
            }
            catch (JsonException)
            {
                msg = "返回内容不是 JSON";
            }
            if (string.IsNullOrEmpty(token))
            {
                // 不记原始返回体：只记错误码与说明
                _logger.LogWarning("获取订阅消息 access_token 失败：HTTP {Status} errcode={Code} {Message}", (int)resp.StatusCode, code, Redact(msg, null));
                return null;
            }
            // 缓存到过期前 5 分钟
            _token = new CachedToken(token, UtcNow().AddSeconds(Math.Max(60, expires - 300)));
            return token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>只在缓存的仍是这个失效 token 时才清：并发的多条消息同时遇到 40001，只会重新取一次</summary>
    private void Invalidate(string token)
    {
        var cached = _token;
        if (cached != null && cached.Value == token) _token = null;
    }

    // ---------- 配置检查 ----------

    private static readonly string[] States = { "developer", "trial", "formal" };

    /// <summary>启动时的配置检查（只用于提示）：返回每一处写错的说明；未启用或全部正确时为空</summary>
    public static List<string> ConfigProblems(SubscribeOptions cfg)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(cfg.TemplateId) && cfg.Fields.Count == 0) return problems;
        if (string.IsNullOrWhiteSpace(cfg.TemplateId)) problems.Add("已配置 Fields 但 TemplateId 为空，订阅消息未启用");
        if (cfg.Fields.Count == 0) problems.Add("已配置 TemplateId 但 Fields 为空，订阅消息未启用");
        foreach (var (key, slot) in cfg.Fields)
            if (SubscribeFields.ParseSlot(slot) == null)
                problems.Add($"Fields:{key} 的值「{slot}」不是有效的槽名（可用：{string.Join(" / ", Enum.GetNames<SubscribeSlot>())}）");
        if (!States.Contains((cfg.MiniProgramState ?? "").Trim()))
            problems.Add($"MiniProgramState「{cfg.MiniProgramState}」应为 developer / trial / formal");
        return problems;
    }

    // ---------- 工具 ----------

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    /// <summary>日志文字兜底脱敏：抹掉 AppSecret、access_token 以及 URL 里的 access_token / secret 参数</summary>
    private string Redact(string? text, string? token)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        if (!string.IsNullOrEmpty(_opt.AppSecret)) s = s.Replace(_opt.AppSecret, "***");
        if (!string.IsNullOrEmpty(token)) s = s.Replace(token, "***");
        var cached = _token?.Value;
        if (!string.IsNullOrEmpty(cached)) s = s.Replace(cached, "***");
        return System.Text.RegularExpressions.Regex.Replace(s, @"(access_token|secret)=[^&\s""']+", "$1=***");
    }
}
