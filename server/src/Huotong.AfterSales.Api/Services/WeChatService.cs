using System.Text.Json;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Api.Services;

public interface IWeChatService
{
    /// <summary>wx.login code → openid</summary>
    Task<string> Code2Session(string code);
    /// <summary>getPhoneNumber code → 手机号</summary>
    Task<string> GetPhoneNumber(string phoneCode);
}

public class WeChatService : IWeChatService
{
    private readonly IHttpClientFactory _http;
    private readonly WeChatOptions _opt;
    private readonly IMemoryCache _cache;
    private readonly ILogger<WeChatService> _logger;

    public WeChatService(IHttpClientFactory http, IOptions<WeChatOptions> opt, IMemoryCache cache, ILogger<WeChatService> logger)
    {
        _http = http;
        _opt = opt.Value;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string> Code2Session(string code)
    {
        if (_opt.MockMode) return code.StartsWith("mock-") ? code : "mock-" + code;
        var url = $"https://api.weixin.qq.com/sns/jscode2session?appid={Uri.EscapeDataString(_opt.AppId)}&secret={Uri.EscapeDataString(_opt.AppSecret)}&js_code={Uri.EscapeDataString(code)}&grant_type=authorization_code";
        using var doc = await GetJson(url);
        var root = doc.RootElement;
        if (root.TryGetProperty("openid", out var openid) && !string.IsNullOrEmpty(openid.GetString()))
            return openid.GetString()!;
        _logger.LogWarning("jscode2session 失败: {Body}", root.GetRawText());
        throw BizException.BadRequest("微信登录失败，请重试");
    }

    public async Task<string> GetPhoneNumber(string phoneCode)
    {
        if (_opt.MockMode) return phoneCode.Trim();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await AccessToken(force: attempt > 0);
            var client = _http.CreateClient("wechat");
            var resp = await client.PostAsync(
                $"https://api.weixin.qq.com/wxa/business/getuserphonenumber?access_token={Uri.EscapeDataString(token)}",
                JsonContent(new { code = phoneCode }));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var errcode = root.TryGetProperty("errcode", out var ec) ? ec.GetInt32() : 0;
            if (errcode == 0 && root.TryGetProperty("phone_info", out var info))
            {
                var phone = info.TryGetProperty("purePhoneNumber", out var p) ? p.GetString() : null;
                if (!string.IsNullOrEmpty(phone)) return phone!;
            }
            // access_token 失效时刷新重试一次
            if (errcode is 40001 or 42001 or 40014) { _cache.Remove(TokenKey); continue; }
            _logger.LogWarning("getuserphonenumber 失败: {Body}", root.GetRawText());
            break;
        }
        throw BizException.BadRequest("获取手机号失败，请重试");
    }

    private const string TokenKey = "wx_access_token";

    private async Task<string> AccessToken(bool force)
    {
        if (!force && _cache.TryGetValue(TokenKey, out string? cached) && !string.IsNullOrEmpty(cached)) return cached!;
        var url = $"https://api.weixin.qq.com/cgi-bin/token?grant_type=client_credential&appid={Uri.EscapeDataString(_opt.AppId)}&secret={Uri.EscapeDataString(_opt.AppSecret)}";
        using var doc = await GetJson(url);
        var root = doc.RootElement;
        if (root.TryGetProperty("access_token", out var t))
        {
            var expires = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 7200;
            var token = t.GetString()!;
            _cache.Set(TokenKey, token, TimeSpan.FromSeconds(Math.Max(60, expires - 300)));
            return token;
        }
        _logger.LogWarning("获取 access_token 失败: {Body}", root.GetRawText());
        throw new BizException(50000, "微信服务暂不可用，请稍后重试");
    }

    private async Task<JsonDocument> GetJson(string url)
    {
        var client = _http.CreateClient("wechat");
        var body = await client.GetStringAsync(url);
        return JsonDocument.Parse(body);
    }

    private static StringContent JsonContent(object o) =>
        new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");
}
