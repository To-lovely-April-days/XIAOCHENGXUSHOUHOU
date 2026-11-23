using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Tests;

/// <summary>
/// v1.4 订阅消息的微信调用（不联网：用假的 HttpMessageHandler 顶替 api.weixin.qq.com）：
/// stable_token 的请求形状、缓存与并发只取一次、过期重取、40001 / 42001 清缓存重试一次、
/// 43101 等正常拒收只记 Information、其它错误记 Warning、异常与超时都不抛、日志里没有 access_token 与 AppSecret。
/// </summary>
public sealed class V14SubscribeHttpTests
{
    private const string AppId = "wx-test-appid";
    private const string Secret = "S3CR3T-app-secret-value";
    private const string TokenA = "TOKEN-AAAA-1111";
    private const string TokenB = "TOKEN-BBBB-2222";

    private sealed class FakeWeChat : HttpMessageHandler
    {
        public ConcurrentQueue<(string Method, string Url, string Body)> Calls { get; } = new();
        public int TokenCalls;
        public int SendCalls;
        /// <summary>第 n 次取 token（从 1 起）→ 返回体</summary>
        public Func<int, CancellationToken, Task<string>> OnToken = (n, _) => Task.FromResult(TokenJson(TokenA));
        /// <summary>第 n 次发送（从 1 起）、所带 token → 返回体</summary>
        public Func<int, string, CancellationToken, Task<string>> OnSend = (_, _, _) => Task.FromResult("{\"errcode\":0,\"errmsg\":\"ok\"}");

        public static string TokenJson(string token, int expires = 7200) => $"{{\"access_token\":\"{token}\",\"expires_in\":{expires}}}";
        public static string Err(int code, string msg = "err") => $"{{\"errcode\":{code},\"errmsg\":\"{msg}\"}}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Enqueue((request.Method.Method, uri.ToString(), body));
            Assert.Equal("api.weixin.qq.com", uri.Host);
            string json;
            if (uri.AbsolutePath == "/cgi-bin/stable_token")
                json = await OnToken(Interlocked.Increment(ref TokenCalls), ct);
            else if (uri.AbsolutePath == "/cgi-bin/message/subscribe/send")
                json = await OnSend(Interlocked.Increment(ref SendCalls), System.Web.HttpUtility.ParseQueryString(uri.Query)["access_token"] ?? "", ct);
            else
                throw new InvalidOperationException("意外的请求地址 " + uri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public List<string> Names { get; } = new();
        public FakeFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name)
        {
            lock (Names) Names.Add(name);
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception) + (exception == null ? "" : " " + exception)));
        public string All => string.Join("\n", Entries.Select(e => e.Text));
        public int Count(LogLevel level) => Entries.Count(e => e.Level == level);
    }

    private readonly FakeWeChat _wx = new();
    private readonly ListLogger<SubscribeService> _log = new();
    private readonly WeChatOptions _opt = new()
    {
        AppId = AppId, AppSecret = Secret, MockMode = false,
        Subscribe = new SubscribeOptions
        {
            TemplateId = "TPL-1", MiniProgramState = "formal",
            Fields = { ["thing1"] = "Title", ["character_string2"] = "OrderNo", ["time3"] = "Time" }
        }
    };
    private readonly FakeFactory _factory;
    private readonly SubscribeService _svc;
    private DateTimeOffset _now = new(2026, 11, 6, 5, 0, 0, TimeSpan.Zero);

    public V14SubscribeHttpTests()
    {
        _factory = new FakeFactory(_wx);
        _svc = new SubscribeService(_factory, Options.Create(_opt), _log) { UtcNow = () => _now };
    }

    private static Employee E(int id, string name = "王工") => new() { Id = id, Name = name, Enabled = true, OpenId = "openid-" + id, Role = Role.Engineer };

    private static SubscribeNotice Notice(params Employee[] to) => new(42, to,
        new SubscribeSlots("新工单指派给你", "SH-20261106-001", "客户", "说明", new DateTime(2026, 11, 6, 13, 42, 0), "赵主管", "待启动", "设备"));

    private Task Send(params Employee[] to) => _svc.NotifyAsync(0, new[] { Notice(to) });

    private void AssertNoSecretsInLogs()
    {
        Assert.DoesNotContain(TokenA, _log.All);
        Assert.DoesNotContain(TokenB, _log.All);
        Assert.DoesNotContain(Secret, _log.All);
    }

    // ---------- 请求形状 ----------

    [Fact]
    public async Task 先取稳定版token再发送_请求形状正确()
    {
        await Send(E(1));
        var calls = _wx.Calls.ToArray();
        Assert.Equal(2, calls.Length);

        Assert.Equal(("POST", "https://api.weixin.qq.com/cgi-bin/stable_token"), (calls[0].Method, calls[0].Url));
        using (var token = JsonDocument.Parse(calls[0].Body))
        {
            Assert.Equal("client_credential", token.RootElement.GetProperty("grant_type").GetString());
            Assert.Equal(AppId, token.RootElement.GetProperty("appid").GetString());
            Assert.Equal(Secret, token.RootElement.GetProperty("secret").GetString());
            Assert.False(token.RootElement.TryGetProperty("force_refresh", out _));
        }

        Assert.Equal(("POST", $"https://api.weixin.qq.com/cgi-bin/message/subscribe/send?access_token={TokenA}"), (calls[1].Method, calls[1].Url));
        using var send = JsonDocument.Parse(calls[1].Body);
        var root = send.RootElement;
        Assert.Equal("openid-1", root.GetProperty("touser").GetString());
        Assert.Equal("TPL-1", root.GetProperty("template_id").GetString());
        Assert.Equal("pages/order-detail/index?id=42", root.GetProperty("page").GetString());
        Assert.Equal("formal", root.GetProperty("miniprogram_state").GetString());
        Assert.Equal("zh_CN", root.GetProperty("lang").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(3, data.EnumerateObject().Count());
        Assert.Equal("新工单指派给你", data.GetProperty("thing1").GetProperty("value").GetString());
        Assert.Equal("SH-20261106-001", data.GetProperty("character_string2").GetProperty("value").GetString());
        Assert.Equal("2026年11月06日 13:42", data.GetProperty("time3").GetProperty("value").GetString());

        Assert.All(_factory.Names, n => Assert.Equal("wechat", n));
        Assert.Equal(0, _log.Count(LogLevel.Warning));
        Assert.Contains("订阅消息已发送", _log.All);
        AssertNoSecretsInLogs();
    }

    [Theory]
    [InlineData("developer")]
    [InlineData("trial")]
    [InlineData("formal")]
    public async Task 小程序版本原样传给微信(string state)
    {
        _opt.Subscribe.MiniProgramState = state;
        await Send(E(1));
        using var send = JsonDocument.Parse(_wx.Calls.Last().Body);
        Assert.Equal(state, send.RootElement.GetProperty("miniprogram_state").GetString());
    }

    // ---------- token 缓存 ----------

    [Fact]
    public async Task Token只取一次_之后复用缓存()
    {
        await Send(E(1));
        await Send(E(2));
        await Send(E(3), E(4));
        Assert.Equal(1, _wx.TokenCalls);
        Assert.Equal(4, _wx.SendCalls);
    }

    [Fact]
    public async Task 并发发送_只取一次token()
    {
        _wx.OnToken = async (_, ct) => { await Task.Delay(80, ct); return FakeWeChat.TokenJson(TokenA); };
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Send(E(i * 3 + 1), E(i * 3 + 2), E(i * 3 + 3))));
        Assert.Equal(1, _wx.TokenCalls);
        Assert.Equal(30, _wx.SendCalls);
    }

    [Fact]
    public async Task Token缓存到过期前五分钟_过了才重新取()
    {
        _wx.OnToken = (n, _) => Task.FromResult(FakeWeChat.TokenJson(n == 1 ? TokenA : TokenB, 7200));
        var used = new ConcurrentQueue<string>();
        _wx.OnSend = (_, token, _) => { used.Enqueue(token); return Task.FromResult(FakeWeChat.Err(0, "ok")); };

        await Send(E(1));
        _now = _now.AddSeconds(7200 - 300 - 1);
        await Send(E(1));
        Assert.Equal(1, _wx.TokenCalls);

        _now = _now.AddSeconds(2);
        await Send(E(1));
        Assert.Equal(2, _wx.TokenCalls);
        Assert.Equal(new[] { TokenA, TokenA, TokenB }, used);
    }

    // ---------- token 失效重试 ----------

    [Theory]
    [InlineData(40001)]
    [InlineData(42001)]
    public async Task Token失效_清缓存重取并重试一次(int errcode)
    {
        _wx.OnToken = (n, _) => Task.FromResult(FakeWeChat.TokenJson(n == 1 ? TokenA : TokenB));
        _wx.OnSend = (_, token, _) => Task.FromResult(token == TokenA ? FakeWeChat.Err(errcode, "access_token expired") : FakeWeChat.Err(0, "ok"));

        await Send(E(1));
        Assert.Equal(2, _wx.TokenCalls);
        Assert.Equal(2, _wx.SendCalls);
        Assert.Equal(0, _log.Count(LogLevel.Warning));
        Assert.Contains("订阅消息已发送", _log.All);

        // 新 token 进了缓存：下一条直接用它
        await Send(E(2));
        Assert.Equal(2, _wx.TokenCalls);
        Assert.Equal(3, _wx.SendCalls);
        AssertNoSecretsInLogs();
    }

    [Fact]
    public async Task Token失效_重试后仍失效就放弃_只重试一次()
    {
        _wx.OnSend = (_, _, _) => Task.FromResult(FakeWeChat.Err(40001, "invalid credential"));
        await Send(E(1));
        Assert.Equal(2, _wx.TokenCalls);
        Assert.Equal(2, _wx.SendCalls);
        Assert.Equal(1, _log.Count(LogLevel.Warning));
        AssertNoSecretsInLogs();
    }

    [Fact]
    public async Task 多条消息同时遇到token失效_只重新取一次()
    {
        _wx.OnToken = async (n, ct) => { await Task.Delay(30, ct); return FakeWeChat.TokenJson(n == 1 ? TokenA : TokenB); };
        _wx.OnSend = async (_, token, ct) =>
        {
            await Task.Delay(30, ct);
            return token == TokenA ? FakeWeChat.Err(42001) : FakeWeChat.Err(0, "ok");
        };
        await Send(Enumerable.Range(1, 6).Select(i => E(i)).ToArray());
        Assert.Equal(2, _wx.TokenCalls);
        Assert.Equal(12, _wx.SendCalls);
        Assert.Equal(0, _log.Count(LogLevel.Warning));
    }

    // ---------- 错误分级 ----------

    [Theory]
    [InlineData(43101)]
    [InlineData(43107)]
    public async Task 用户未订阅或额度用完_只记Information_不重试(int errcode)
    {
        _wx.OnSend = (_, _, _) => Task.FromResult(FakeWeChat.Err(errcode, "user refuse to accept the msg"));
        await Send(E(1));
        Assert.Equal(1, _wx.TokenCalls);
        Assert.Equal(1, _wx.SendCalls);
        Assert.Equal(0, _log.Count(LogLevel.Warning));
        Assert.Equal(0, _log.Count(LogLevel.Error));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Text.Contains(errcode.ToString()));
    }

    [Fact]
    public async Task 其它错误记Warning_一个人失败不影响另一个人()
    {
        _wx.OnSend = (n, _, _) => Task.FromResult(n == 1 ? FakeWeChat.Err(47003, "argument invalid! data.thing1.value invalid") : FakeWeChat.Err(0, "ok"));
        await Send(E(1), E(2));
        Assert.Equal(2, _wx.SendCalls);
        var warn = Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("47003", warn.Text);
        Assert.Contains("data.thing1.value", warn.Text);
        Assert.Contains("订阅消息已发送", _log.All);
        AssertNoSecretsInLogs();
    }

    [Fact]
    public async Task 取token失败_不发送_记Warning_不抛()
    {
        _wx.OnToken = (_, _) => Task.FromResult(FakeWeChat.Err(40125, $"invalid appsecret {Secret} rid: 1"));
        await Send(E(1), E(2));
        Assert.Equal(0, _wx.SendCalls);
        Assert.True(_log.Count(LogLevel.Warning) >= 1);
        Assert.Contains("40125", _log.All);
        AssertNoSecretsInLogs();
    }

    [Fact]
    public async Task 取token失败_多个收件人也只取一次_只记一条Warning()
    {
        // 典型场景：服务器 IP 不在小程序后台的白名单里
        _wx.OnToken = (_, _) => Task.FromResult(FakeWeChat.Err(40164, "invalid ip 1.2.3.4, not in whitelist"));
        await Send(E(1), E(2), E(3));
        Assert.Equal(1, _wx.TokenCalls);
        Assert.Equal(0, _wx.SendCalls);
        Assert.Equal(1, _log.Count(LogLevel.Warning));
        Assert.Contains("40164", _log.All);
        AssertNoSecretsInLogs();
    }

    [Fact]
    public async Task 取token时网络异常_多个收件人也只试一次_只记一条Warning()
    {
        _wx.OnToken = (_, _) => throw new HttpRequestException("Name or service not known");
        await Send(E(1), E(2), E(3));
        Assert.Equal(1, _wx.TokenCalls);
        Assert.Equal(0, _wx.SendCalls);
        Assert.Equal(1, _log.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task 返回内容不是JSON_记Warning_不抛()
    {
        _wx.OnSend = (_, _, _) => Task.FromResult("<html>502 Bad Gateway</html>");
        await Send(E(1));
        Assert.Equal(1, _log.Count(LogLevel.Warning));
    }

    // ---------- 异常与超时 ----------

    [Fact]
    public async Task 网络异常_不抛_日志脱敏()
    {
        _wx.OnSend = (_, token, _) => throw new HttpRequestException(
            $"Connection refused (https://api.weixin.qq.com/cgi-bin/message/subscribe/send?access_token={token}&secret={Secret})");
        await Send(E(1));
        Assert.Equal(1, _log.Count(LogLevel.Warning));
        Assert.Contains("HttpRequestException", _log.All);
        Assert.Contains("access_token=***", _log.All);
        AssertNoSecretsInLogs();
    }

    [Fact]
    public async Task 取token时网络异常_不抛()
    {
        _wx.OnToken = (_, _) => throw new HttpRequestException("Name or service not known");
        await Send(E(1));
        Assert.Equal(0, _wx.SendCalls);
        Assert.Equal(1, _log.Count(LogLevel.Warning));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 微信迟迟不返回_到超时就放弃_不抛(bool handlerHonorsCancellation)
    {
        _svc.SendTimeout = TimeSpan.FromMilliseconds(200);
        var never = new TaskCompletionSource<string>();
        _wx.OnSend = handlerHonorsCancellation
            ? async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return ""; }
            : (_, _, _) => never.Task;
        // 在线程池里计时：xUnit 的同步上下文在整套测试并行跑时会让 await 之后的代码排队几秒，那不是被测代码的耗时
        var elapsed = await Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            await Send(E(1), E(2));
            return sw.ElapsedMilliseconds;
        });
        Assert.InRange(elapsed, 150, 3000);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("已放弃"));
        never.TrySetResult(FakeWeChat.Err(0, "ok"));
    }

    [Fact]
    public async Task 先取到token之后发送才卡住_超时同样记一条已放弃()
    {
        // token 不在缓存里：发送请求是在「开始等超时」之后才发出去的。超时那一刻发送任务会先于等待被取消，
        // 这种顺序下也必须留下一条超时日志（不能悄悄结束）。放进线程池跑：与线上一样没有同步上下文
        _svc.SendTimeout = TimeSpan.FromMilliseconds(300);
        _wx.OnToken = async (_, ct) => { await Task.Delay(50, ct); return FakeWeChat.TokenJson(TokenA); };
        _wx.OnSend = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return ""; };
        await Task.Run(() => Send(E(1), E(2)));
        Assert.Equal(1, _wx.TokenCalls);
        Assert.Equal(2, _wx.SendCalls);
        Assert.Equal(1, _log.Entries.Count(e => e.Level == LogLevel.Warning && e.Text.Contains("已放弃")));
        Assert.Equal(1, _log.Count(LogLevel.Warning));
        AssertNoSecretsInLogs();
    }

    [Fact]
    public async Task 取token时卡住_超时记一条已放弃_不抛()
    {
        _svc.SendTimeout = TimeSpan.FromMilliseconds(200);
        _wx.OnToken = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ""; };
        await Task.Run(() => Send(E(1), E(2), E(3)));
        Assert.Equal(1, _wx.TokenCalls);
        Assert.Equal(0, _wx.SendCalls);
        Assert.Equal(1, _log.Count(LogLevel.Warning));
        Assert.Contains("已放弃", _log.All);
    }

    [Fact]
    public void 默认超时五秒() => Assert.Equal(TimeSpan.FromSeconds(5), _svc.SendTimeout);

    // ---------- 不发网络请求的几种情况 ----------

    [Fact]
    public async Task MockMode_不发任何网络请求_只记录()
    {
        _opt.MockMode = true;
        await Send(E(1), E(2));
        Assert.Empty(_wx.Calls);
        Assert.Empty(_factory.Names);
        Assert.Equal(new[] { "openid-1", "openid-2" }, _svc.MockSent.Select(m => m.OpenId));
        Assert.Equal("pages/order-detail/index?id=42", _svc.MockSent[0].Page);
    }

    [Fact]
    public async Task 未启用_没有收件人_没配密钥_都不发网络请求()
    {
        // 没有可发的收件人（未绑定 / 停用 / 操作人本人）
        await _svc.NotifyAsync(5, new[] { Notice(new Employee { Id = 1, Enabled = true, OpenId = null }, new Employee { Id = 2, Enabled = false, OpenId = "x" }, E(5)) });
        Assert.Empty(_wx.Calls);

        // 没配 AppSecret
        _opt.AppSecret = "";
        await Send(E(1));
        Assert.Empty(_wx.Calls);
        _opt.AppSecret = Secret;

        // 未启用
        _opt.Subscribe.TemplateId = "";
        await Send(E(1));
        Assert.Empty(_wx.Calls);
        Assert.Empty(_log.Entries);
        Assert.Empty(_svc.MockSent);
    }

    // ---------- HttpClient 自带的请求日志 ----------

    private sealed class CaptureProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new L(categoryName, Lines);
        public void Dispose() { }

        private sealed class L : ILogger
        {
            private readonly string _category;
            private readonly ConcurrentQueue<string> _lines;
            public L(string category, ConcurrentQueue<string> lines) { _category = category; _lines = lines; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                _lines.Enqueue($"{logLevel} {_category}: {formatter(state, exception)} {exception}");
        }
    }

    [Fact]
    public async Task 按正式注册方式创建的HttpClient_最详细的日志级别下也不会把token和密钥写进日志()
    {
        var capture = new CaptureProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        services.AddOutboundHttpClients();
        services.AddHttpClient("wechat").ConfigurePrimaryHttpMessageHandler(() => _wx);
        services.AddSingleton(Options.Create(_opt));
        services.AddSingleton<SubscribeService>();
        await using var sp = services.BuildServiceProvider();

        // 一次成功，一次 token 失效后重试（两种 token 都出现在请求地址里）
        _wx.OnToken = (n, _) => Task.FromResult(FakeWeChat.TokenJson(n == 1 ? TokenA : TokenB));
        _wx.OnSend = (n, token, _) => Task.FromResult(n == 2 ? FakeWeChat.Err(40001) : FakeWeChat.Err(0, "ok"));
        var svc = sp.GetRequiredService<SubscribeService>();
        await svc.NotifyAsync(0, new[] { Notice(E(1)) });
        await svc.NotifyAsync(0, new[] { Notice(E(2)) });

        Assert.Equal(2, _wx.TokenCalls);
        Assert.Equal(3, _wx.SendCalls);
        var all = string.Join("\n", capture.Lines);
        Assert.Contains("订阅消息已发送", all);       // 确认日志确实被收集到了
        Assert.DoesNotContain(TokenA, all);
        Assert.DoesNotContain(TokenB, all);
        Assert.DoesNotContain(Secret, all);
        Assert.DoesNotContain("access_token=", all);
    }
}
