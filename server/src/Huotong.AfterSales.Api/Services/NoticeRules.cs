using Huotong.AfterSales.Api.Domain;

namespace Huotong.AfterSales.Api.Services;

/// <summary>
/// v2.2 提醒设置：微信提醒分成几类，每个人可以在小程序「提醒设置」里按类关掉。
/// 关掉的类别记在 Employee.MutedNotices（逗号分隔的 key）；发送前在 SubscribeService 里统一过滤。
/// </summary>
public static class NoticeRules
{
    public const string Assign = "assign";
    public const string Request = "request";
    public const string Progress = "progress";
    public const string Charge = "charge";
    public const string Register = "register";

    public record Category(string Key, string Name, string Desc);

    public static readonly Category[] All =
    {
        new(Assign, "指派与转交", "工单指派 / 改派给你、转交给你、被加为协助人、新工单待指派"),
        new(Request, "申请与审批", "暂停 / 协助 / 升级服务方式的申请待审批，以及同意或驳回的结果"),
        new(Progress, "进度与验收", "工单待验收、完工被退回、暂停与恢复、已闭环"),
        new(Charge, "收费确认", "等你报价 / 审核 / 确认，收费确认的结果和退回"),
        new(Register, "注册申请", "有新同事申请注册账号，等你审批"),
    };

    /// <summary>工单提醒按标题归类（标题是代码里写死的）。先看收费，再看申请，再看指派 / 转交 / 协助人，其余都算进度</summary>
    public static string CategoryOf(string title)
    {
        if (title.Contains("收费") || title.Contains("报价")) return Charge;
        if (title.Contains("申请") || title == "服务方式已升级") return Request;
        if (title.Contains("指派") || title.Contains("转交") || title.Contains("协助人")) return Assign;
        return Progress;
    }

    public static HashSet<string> Muted(string? stored) =>
        (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    public static bool IsMuted(Employee e, string category) => Muted(e.MutedNotices).Contains(category);

    /// <summary>这个人用得上哪几类：注册申请只给有注册审批权限的人；收费确认工程师也有（确认结果会通知到他）</summary>
    public static IEnumerable<Category> For(Employee e) =>
        All.Where(c => c.Key != Register || e.CanApprove);

    public static string Toggle(string? stored, string key, bool on)
    {
        var set = Muted(stored);
        if (on) set.Remove(key); else set.Add(key);
        return string.Join(",", All.Select(c => c.Key).Where(set.Contains));
    }
}
