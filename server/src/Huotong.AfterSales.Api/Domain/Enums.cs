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

public enum FileCategory { OrderPhoto, PunchPhoto, ProcessPhoto, Signature, Avatar }

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
}
