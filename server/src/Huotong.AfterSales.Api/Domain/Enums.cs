namespace Huotong.AfterSales.Api.Domain;

public enum Role { Engineer, Sales, Supervisor }

public enum OrderStatus { Pending, Accepted, InProgress, OnHold, Submitted, Closed, Voided }

public enum OrderSource { SalesReferral, CustomerDirect, Inspection, ReturnLogistics }

public enum ProblemType { Fault, Complaint, Return, Consult }

public enum Urgency { Urgent, Normal, Consult }

public enum ServiceType { Onsite, Remote, Factory }

public enum PunchKind { Arrive, Leave }

public enum RunStatus { Normal, Observe, NotRecovered }

public enum IssueType { MissingPunch, DistanceAbnormal }

public enum FileCategory { OrderPhoto, PunchPhoto, ProcessPhoto, Signature, Avatar, FactoryReceivePhoto, FactoryShipPhoto, FactorySignPhoto, Attachment }

/// <summary>文件大类（v1.2，不落库）：由 MimeType 推导</summary>
public enum FileKind { Image, Video, Document }

/// <summary>暂停原因类型（v1.1）</summary>
public enum HoldReasonType { WaitCustomer, WaitParts, WaitThirdParty, Other }

/// <summary>工程师发起、需主管审批的申请类型</summary>
public enum OrderRequestType { Hold, AddAssistant }

public enum RequestStatus { Pending, Approved, Rejected, Cancelled }

/// <summary>返厂节点；Repairing 为隐含节点，不落库</summary>
public enum FactoryNode { CustomerShipped, FactoryReceived, Repairing, ShippedBack, CustomerSigned }

public enum ParticipantRole { Lead, Assistant, Former }

/// <summary>状态记录类别；库中 null = Status</summary>
/// <remarks>AttachmentPrivate（受限附件的记录）与 Price 一样不返回给 Engineer</remarks>
public enum LogKind { Status, Price, Hold, Assist, Factory, Attachment, AttachmentPrivate }

/// <summary>工单动作（状态机输入）</summary>
public enum OrderAction { Claim, Assign, Reassign, Start, FirstArrive, Complete, Void, Hold, Resume, Reject, Close }

public static class EnumText
{
    public static string RoleName(Role r) => r switch
    {
        Role.Engineer => "售后工程师",
        Role.Sales => "销售",
        Role.Supervisor => "售后主管",
        _ => r.ToString()
    };

    public static string Status(OrderStatus s) => s switch
    {
        OrderStatus.Pending => "待认领",
        OrderStatus.Accepted => "待启动",
        OrderStatus.InProgress => "处理中",
        OrderStatus.OnHold => "暂停搁置",
        OrderStatus.Submitted => "待验收",
        OrderStatus.Closed => "已闭环",
        OrderStatus.Voided => "已作废",
        _ => s.ToString()
    };

    public static string Service(ServiceType s) => s switch
    {
        ServiceType.Onsite => "现场",
        ServiceType.Remote => "远程",
        ServiceType.Factory => "返厂",
        _ => s.ToString()
    };

    public static string UrgencyName(Urgency u) => u switch
    {
        Urgency.Urgent => "紧急",
        Urgency.Normal => "一般",
        Urgency.Consult => "咨询",
        _ => u.ToString()
    };

    public static string Source(OrderSource s) => s switch
    {
        OrderSource.SalesReferral => "销售转报",
        OrderSource.CustomerDirect => "客户直联",
        OrderSource.Inspection => "现场巡检",
        OrderSource.ReturnLogistics => "退货物流",
        _ => s.ToString()
    };

    public static string Problem(ProblemType p) => p switch
    {
        ProblemType.Fault => "设备故障",
        ProblemType.Complaint => "客诉投诉",
        ProblemType.Return => "退回返修",
        ProblemType.Consult => "咨询指导",
        _ => p.ToString()
    };

    public static string Run(RunStatus r) => r switch
    {
        RunStatus.Normal => "正常",
        RunStatus.Observe => "待观察",
        RunStatus.NotRecovered => "未恢复",
        _ => r.ToString()
    };

    public static string Kind(PunchKind k) => k == PunchKind.Arrive ? "到场" : "离场";

    public static string HoldReason(HoldReasonType t) => t switch
    {
        HoldReasonType.WaitCustomer => "等客户",
        HoldReasonType.WaitParts => "等备件",
        HoldReasonType.WaitThirdParty => "等第三方",
        HoldReasonType.Other => "其他",
        _ => t.ToString()
    };

    public static string Factory(FactoryNode n) => n switch
    {
        FactoryNode.CustomerShipped => "客户寄出",
        FactoryNode.FactoryReceived => "工厂收货",
        FactoryNode.Repairing => "检测/维修",
        FactoryNode.ShippedBack => "寄回客户",
        FactoryNode.CustomerSigned => "客户签收",
        _ => n.ToString()
    };

    public static string Participant(ParticipantRole r) => r switch
    {
        ParticipantRole.Lead => "负责人",
        ParticipantRole.Assistant => "协助人",
        ParticipantRole.Former => "已退出",
        _ => r.ToString()
    };

    public static string RequestStatusName(RequestStatus s) => s switch
    {
        RequestStatus.Pending => "待审批",
        RequestStatus.Approved => "已同意",
        RequestStatus.Rejected => "已驳回",
        RequestStatus.Cancelled => "已撤销",
        _ => s.ToString()
    };

    /// <summary>暂停原因文字（全系统唯一规则）：Other / 无类型 → 只用说明（空则「其他」）；其余 → 类型中文[：说明]</summary>
    public static string HoldText(HoldReasonType? type, string? note)
    {
        var n = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (type == null || type == HoldReasonType.Other) return n ?? "其他";
        return n == null ? HoldReason(type.Value) : $"{HoldReason(type.Value)}：{n}";
    }

    /// <summary>金额文字：0 → 保内免费；整数 → ¥1,800；非整数 → ¥1,800.50</summary>
    public static string Money(decimal x)
    {
        if (x == 0) return "保内免费";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return x == decimal.Truncate(x) ? "¥" + x.ToString("#,##0", inv) : "¥" + x.ToString("#,##0.00", inv);
    }

    /// <summary>返厂进度文字：取最靠后的已记录节点</summary>
    public static string FactoryText(IEnumerable<FactoryNode> recorded)
    {
        var set = recorded.ToHashSet();
        if (set.Contains(FactoryNode.CustomerSigned)) return "客户已签收";
        if (set.Contains(FactoryNode.ShippedBack)) return "已寄回客户";
        if (set.Contains(FactoryNode.FactoryReceived)) return "检测/维修中";
        if (set.Contains(FactoryNode.CustomerShipped)) return "客户已寄出";
        return "待客户寄出";
    }
}
