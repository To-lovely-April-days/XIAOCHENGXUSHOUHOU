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
        [(OrderStatus.Accepted, OrderAction.Hold)] = OrderStatus.OnHold,
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

    /// <summary>恢复处理的目标状态：回到暂停前的状态；旧数据（null）回处理中</summary>
    public static OrderStatus ResumeTarget(OrderStatus? holdFrom) =>
        holdFrom == OrderStatus.Accepted ? OrderStatus.Accepted : OrderStatus.InProgress;

    /// <summary>Resume 专用重载：目标状态由暂停前状态决定；其余动作同 Apply(from, action)</summary>
    public static OrderStatus Apply(OrderStatus from, OrderAction action, OrderStatus? holdFrom)
    {
        var to = Apply(from, action);
        return action == OrderAction.Resume ? ResumeTarget(holdFrom) : to;
    }

    /// <summary>
    /// 计算 actions 所需的全部事实（CHANGES-v1.1 §2.7；v1.3 §2.9 在末尾新增 IsFormer / HasPendingTransfer / PendingTransferToMe）
    /// </summary>
    public record ActionContext(Role Role, int UserId, OrderStatus Status, int? AssigneeId, ServiceType ServiceType,
        bool HasMissingPunch, bool IsAssistant = false, bool PriceSet = true, bool HasPendingHoldRequest = false,
        bool FactoryCanRecord = false, bool FactoryShippedBack = false,
        bool IsFormer = false, bool HasPendingTransfer = false, bool PendingTransferToMe = false);

    /// <summary>可以发起 / 存在待确认转交的状态：待启动、处理中、暂停搁置</summary>
    public static bool CanTransfer(OrderStatus s) => s is OrderStatus.Accepted or OrderStatus.InProgress or OrderStatus.OnHold;

    /// <summary>
    /// 计算当前用户可执行动作（CHANGES-v1.1 §2.7、CHANGES-v1.3 §2.9，固定顺序）
    /// </summary>
    public static List<string> Actions(ActionContext c)
    {
        var a = new List<string>();
        var status = c.Status;
        var active = status is OrderStatus.Accepted or OrderStatus.InProgress;
        var factory = c.ServiceType == ServiceType.Factory && c.FactoryCanRecord;
        switch (c.Role)
        {
            case Role.Engineer:
                // 已退出的人只读：只可能有「确认接手 / 拒绝」（又被转交回来待确认时）
                if (c.IsFormer)
                {
                    if (c.PendingTransferToMe) { a.Add("acceptTransfer"); a.Add("rejectTransfer"); }
                    break;
                }
                var lead = c.AssigneeId == c.UserId;
                if (status == OrderStatus.Pending) a.Add("claim");
                if (lead && active) a.Add("appoint");
                if ((lead || c.IsAssistant) && c.ServiceType == ServiceType.Onsite && active) a.Add("checkin");
                if (lead && c.ServiceType == ServiceType.Remote && status == OrderStatus.Accepted) a.Add("start");
                if (factory) a.Add("factory");
                if (lead && status == OrderStatus.InProgress && (c.ServiceType != ServiceType.Factory || c.FactoryShippedBack)
                    && !c.HasPendingTransfer) a.Add("complete");
                if (lead && active && !c.HasPendingHoldRequest) a.Add("requestHold");
                if (lead && active) a.Add("requestAssistant");
                if (lead && CanTransfer(status) && !c.HasPendingTransfer) a.Add("transfer");
                if (lead && c.HasPendingTransfer) a.Add("cancelTransfer");
                if (c.PendingTransferToMe) { a.Add("acceptTransfer"); a.Add("rejectTransfer"); }
                break;
            case Role.Supervisor:
                if (status == OrderStatus.Pending) a.Add("assign");
                if (active || status == OrderStatus.OnHold) a.Add("reassign");
                if (active || status == OrderStatus.OnHold) a.Add("assistants");
                if (status is OrderStatus.Pending or OrderStatus.Accepted) a.Add("void");
                if (active) a.Add("hold");
                if (status == OrderStatus.OnHold) a.Add("resume");
                if (status == OrderStatus.Submitted) a.Add("reject");
                if (status == OrderStatus.Submitted && !c.HasMissingPunch && c.PriceSet) a.Add("close");
                if (c.HasMissingPunch) a.Add("supplement");
                if (status != OrderStatus.Voided) a.Add("setPrice");
                if (factory) a.Add("factory");
                if (c.HasPendingTransfer) a.Add("cancelTransfer");
                break;
            case Role.Sales:
                if (status is not (OrderStatus.Closed or OrderStatus.Voided)) a.Add("suggestPrice");
                if (factory) a.Add("factory");
                break;
        }
        return a;
    }

    /// <summary>5 段进度：OnHold = 暂停前状态对应的段（旧数据无来源 → 3）</summary>
    public static int ProgressStep(OrderStatus s, OrderStatus? holdFrom = null) => s switch
    {
        OrderStatus.Pending => 1,
        OrderStatus.Accepted => 2,
        OrderStatus.InProgress => 3,
        OrderStatus.OnHold => holdFrom == OrderStatus.Accepted ? 2 : 3,
        OrderStatus.Submitted => 4,
        OrderStatus.Closed => 5,
        _ => 0
    };
}
