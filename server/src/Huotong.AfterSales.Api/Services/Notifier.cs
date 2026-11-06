using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Api.Services;

public interface INotifier
{
    /// <summary>发送群提醒；实现必须吞掉异常，不能影响主流程</summary>
    Task NotifyAsync(string title, string text, IEnumerable<string>? atMobiles = null);
}

/// <summary>钉钉群机器人（Webhook 为空只记日志）</summary>
public class DingTalkRobotNotifier : INotifier
{
    private readonly IHttpClientFactory _http;
    private readonly DingTalkOptions _opt;
    private readonly ILogger<DingTalkRobotNotifier> _logger;

    public DingTalkRobotNotifier(IHttpClientFactory http, IOptions<DingTalkOptions> opt, ILogger<DingTalkRobotNotifier> logger)
    {
        _http = http;
        _opt = opt.Value;
        _logger = logger;
    }

    public async Task NotifyAsync(string title, string text, IEnumerable<string>? atMobiles = null)
    {
        var mobiles = atMobiles?.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList() ?? new();
        if (string.IsNullOrWhiteSpace(_opt.Webhook))
        {
            _logger.LogInformation("[通知] {Title} | {Text} | @{Mobiles}", title, text, string.Join(",", mobiles));
            return;
        }
        try
        {
            var url = _opt.Webhook;
            if (!string.IsNullOrWhiteSpace(_opt.Secret))
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_opt.Secret));
                var sign = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}\n{_opt.Secret}")));
                url += (url.Contains('?') ? "&" : "?") + $"timestamp={ts}&sign={Uri.EscapeDataString(sign)}";
            }
            var at = mobiles.Count > 0 ? "\n\n" + string.Join(" ", mobiles.Select(m => "@" + m)) : "";
            var body = new
            {
                msgtype = "markdown",
                markdown = new { title, text = $"#### {title}\n\n{text}{at}" },
                at = new { atMobiles = mobiles, isAtAll = false }
            };
            var client = _http.CreateClient("dingtalk");
            client.Timeout = TimeSpan.FromSeconds(5);
            var resp = await client.PostAsJsonAsync(url, body);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("钉钉通知失败 HTTP {Status}", (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "钉钉通知发送异常（已忽略）");
        }
    }
}
