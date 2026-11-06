using Huotong.AfterSales.Api.Domain;

namespace Huotong.AfterSales.Api.Services;

/// <summary>工单状态机（DESIGN §3）</summary>
public static class OrderStateMachine
{
    private static readonly Dictionary<(OrderStatus, OrderAction), OrderStatus> Transitions = new()
    {
        [(OrderStatus.Pending, OrderAction.Claim)] = OrderStatus.Accepted,
        [(OrderStatus.Pending, OrderAction.Assign)] = OrderStatus.Accepted,
        [(OrderStatus.Pending, OrderAction.Void)] = OrderStatus.Voided,
        [(OrderStatus.Accepted, OrderAction.FirstArrive)] = OrderStatus.InProgress,
        [(OrderStatus.Accepted, OrderAction.Start)] = OrderStatus.InProgress,
        [(OrderStatus.Accepted, OrderAction.Void)] = OrderStatus.Voided,
        [(OrderStatus.Accepted, OrderAction.Reassign)] = OrderStatus.Accepted,
        [(OrderStatus.InProgress, OrderAction.Complete)] = OrderStatus.Submitted,
        [(OrderStatus.InProgress, OrderAction.Hold)] = OrderStatus.OnHold,
        [(OrderStatus.InProgress, OrderAction.Reassign)] = OrderStatus.InProgress,
        [(OrderStatus.OnHold, OrderAction.Resume)] = OrderStatus.InProgress,
        [(OrderStatus.OnHold, OrderAction.Reassign)] = OrderStatus.OnHold,
        [(OrderStatus.Submitted, OrderAction.Reject)] = OrderStatus.InProgress,
        [(OrderStatus.Submitted, OrderAction.Close)] = OrderStatus.Closed,
    };

    public static bool CanApply(OrderStatus from, OrderAction action) => Transitions.ContainsKey((from, action));

    /// <summary>返回目标状态；非法流转抛 40900</summary>
    public static OrderStatus Apply(OrderStatus from, OrderAction action)
    {
        if (Transitions.TryGetValue((from, action), out var to)) return to;
        throw BizException.Conflict($"工单当前为「{EnumText.Status(from)}」，不能执行此操作");
    }

    /// <summary>
    /// 计算当前用户可执行动作（API.md actions 规则）
    /// </summary>
    public static List<string> Actions(Role role, int userId, OrderStatus status, int? assigneeId,
        ServiceType serviceType, bool hasMissingPunch)
    {
        var a = new List<string>();
        switch (role)
        {
            case Role.Engineer:
                if (status == OrderStatus.Pending) a.Add("claim");
                if (assigneeId == userId)
                {
                    if (status is OrderStatus.Accepted or OrderStatus.InProgress) a.Add("appoint");
                    if (serviceType == ServiceType.Onsite && status is OrderStatus.Accepted or OrderStatus.InProgress) a.Add("checkin");
                    if (serviceType != ServiceType.Onsite && status == OrderStatus.Accepted) a.Add("start");
                    if (status == OrderStatus.InProgress) a.Add("complete");
                }
                break;
            case Role.Supervisor:
                if (status == OrderStatus.Pending) a.Add("assign");
                if (status is OrderStatus.Accepted or OrderStatus.InProgress or OrderStatus.OnHold) a.Add("reassign");
                if (status is OrderStatus.Pending or OrderStatus.Accepted) a.Add("void");
                if (status == OrderStatus.InProgress) a.Add("hold");
                if (status == OrderStatus.OnHold) a.Add("resume");
                if (status == OrderStatus.Submitted) a.Add("reject");
                if (status == OrderStatus.Submitted && !hasMissingPunch) a.Add("close");
                if (hasMissingPunch) a.Add("supplement");
                break;
        }
        return a;
    }

    /// <summary>销售首页 5 阶段进度</summary>
    public static int ProgressStep(OrderStatus s) => s switch
    {
        OrderStatus.Pending => 1,
        OrderStatus.Accepted => 2,
        OrderStatus.InProgress or OrderStatus.OnHold => 3,
        OrderStatus.Submitted => 4,
        OrderStatus.Closed => 5,
        _ => 0
    };
}
