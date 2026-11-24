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
    /// <summary>订阅消息（v1.4）</summary>
    public SubscribeOptions Subscribe { get; set; } = new();
}

/// <summary>
/// 微信小程序一次性订阅消息（v1.4）。模板 ID 与字段键全部由配置决定：在小程序后台选好模板后只改配置，不改代码。
/// 模板 ID 不是机密，可以写进 appsettings.json。
/// </summary>
public class SubscribeOptions
{
    /// <summary>订阅消息模板 ID；为空 = 未启用</summary>
    public string TemplateId { get; set; } = "";
    /// <summary>跳转的小程序版本：developer / trial / formal，原样传给微信的 miniprogram_state</summary>
    public string MiniProgramState { get; set; } = "trial";
    /// <summary>点击消息打开的页面；{id} 替换为工单 id</summary>
    public string Page { get; set; } = "pages/order-detail/index?id={id}";
    /// <summary>
    /// 模板字段映射：键 = 微信模板字段键（thing1、character_string2、time3、phrase4、name5…），
    /// 值 = 语义槽名（Title / OrderNo / Customer / Content / Time / Operator / Status / Device，见 SubscribeSlot）
    /// </summary>
    public Dictionary<string, string> Fields
    {
        get => _fields;
        // 配置里写成 null 时按「没有映射」处理：Enabled 在每个工单动作里都会被读到，不能因为配置写法抛异常
        set => _fields = value ?? new();
    }
    private Dictionary<string, string> _fields = new();

    /// <summary>已启用 = 模板 ID 非空 且 至少映射了一个字段</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(TemplateId) && Fields.Count > 0;

    /// <summary>v1.8 「有新的注册申请」提醒（发给有注册审批权限的人）；单独一个模板，没配就不发</summary>
    public RegisterSubscribeOptions Register
    {
        get => _register;
        set => _register = value ?? new();
    }
    private RegisterSubscribeOptions _register = new();
}

/// <summary>
/// v1.8 注册申请提醒的模板配置。Fields 的槽名沿用 SubscribeSlot：
/// Title = 「新的注册申请」，Customer / Operator = 申请人姓名，OrderNo = 申请人手机号，Content = 一句话说明，Status = 「待审批」，Time = 提交时间。
/// </summary>
public class RegisterSubscribeOptions
{
    public string TemplateId { get; set; } = "";
    public string Page { get; set; } = "pages/approvals/index";
    public Dictionary<string, string> Fields
    {
        get => _fields;
        set => _fields = value ?? new();
    }
    private Dictionary<string, string> _fields = new();
    public bool Enabled => !string.IsNullOrWhiteSpace(TemplateId) && Fields.Count > 0;
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
