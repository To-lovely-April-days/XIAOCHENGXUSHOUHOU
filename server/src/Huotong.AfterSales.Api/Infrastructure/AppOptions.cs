namespace Huotong.AfterSales.Api.Infrastructure;

public class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "huotong-aftersales";
    public int ExpireDays { get; set; } = 30;
}

public class WeChatOptions
{
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public bool MockMode { get; set; }
}

public class DingTalkOptions
{
    public string Webhook { get; set; } = "";
    /// <summary>加签密钥（可选）</summary>
    public string Secret { get; set; } = "";
}

public class AppSettings
{
    /// <summary>文件绝对地址前缀，如 https://api.example.com；为空用请求 Host</summary>
    public string PublicBaseUrl { get; set; } = "";
}
