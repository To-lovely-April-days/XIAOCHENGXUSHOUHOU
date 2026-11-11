using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Services;

namespace Huotong.AfterSales.Tests;

public class OrderStateMachineTests
{
    [Theory]
    [InlineData(OrderStatus.Pending, OrderAction.Claim, OrderStatus.Accepted)]
    [InlineData(OrderStatus.Pending, OrderAction.Assign, OrderStatus.Accepted)]
    [InlineData(OrderStatus.Pending, OrderAction.Void, OrderStatus.Voided)]
    [InlineData(OrderStatus.Accepted, OrderAction.FirstArrive, OrderStatus.InProgress)]
    [InlineData(OrderStatus.Accepted, OrderAction.Start, OrderStatus.InProgress)]
    [InlineData(OrderStatus.Accepted, OrderAction.Void, OrderStatus.Voided)]
    [InlineData(OrderStatus.Accepted, OrderAction.Reassign, OrderStatus.Accepted)]
    [InlineData(OrderStatus.InProgress, OrderAction.Complete, OrderStatus.Submitted)]
    [InlineData(OrderStatus.InProgress, OrderAction.Hold, OrderStatus.OnHold)]
    [InlineData(OrderStatus.InProgress, OrderAction.Reassign, OrderStatus.InProgress)]
    [InlineData(OrderStatus.Accepted, OrderAction.Hold, OrderStatus.OnHold)]
    [InlineData(OrderStatus.OnHold, OrderAction.Reassign, OrderStatus.OnHold)]
    [InlineData(OrderStatus.Submitted, OrderAction.Reject, OrderStatus.InProgress)]
    [InlineData(OrderStatus.Submitted, OrderAction.Close, OrderStatus.Closed)]
    public void LegalTransitions(OrderStatus from, OrderAction action, OrderStatus to)
    {
        Assert.True(OrderStateMachine.CanApply(from, action));
        Assert.Equal(to, OrderStateMachine.Apply(from, action));
    }

    [Theory]
    [InlineData(OrderStatus.Accepted, OrderAction.Claim)]
    [InlineData(OrderStatus.Pending, OrderAction.Complete)]
    [InlineData(OrderStatus.Pending, OrderAction.Reassign)]
    [InlineData(OrderStatus.InProgress, OrderAction.Void)]
    [InlineData(OrderStatus.InProgress, OrderAction.Close)]
    [InlineData(OrderStatus.OnHold, OrderAction.Complete)]
    [InlineData(OrderStatus.Submitted, OrderAction.Hold)]
    [InlineData(OrderStatus.Closed, OrderAction.Reject)]
    [InlineData(OrderStatus.Closed, OrderAction.Void)]
    [InlineData(OrderStatus.Voided, OrderAction.Claim)]
    [InlineData(OrderStatus.Voided, OrderAction.Resume)]
    public void IllegalTransitionsThrowConflict(OrderStatus from, OrderAction action)
    {
        Assert.False(OrderStateMachine.CanApply(from, action));
        var ex = Assert.Throws<BizException>(() => OrderStateMachine.Apply(from, action));
        Assert.Equal(40900, ex.Code);
        Assert.Equal(409, ex.HttpStatus);
    }

    [Theory]
    [InlineData(OrderStatus.InProgress, OrderStatus.InProgress)]
    [InlineData(OrderStatus.Accepted, OrderStatus.Accepted)]
    [InlineData(null, OrderStatus.InProgress)] // 旧数据无来源状态
    public void ResumeReturnsToHoldSource(OrderStatus? holdFrom, OrderStatus to)
    {
        Assert.True(OrderStateMachine.CanApply(OrderStatus.OnHold, OrderAction.Resume));
        Assert.Equal(to, OrderStateMachine.Apply(OrderStatus.OnHold, OrderAction.Resume, holdFrom));
        Assert.Equal(to, OrderStateMachine.ResumeTarget(holdFrom));
        // 其余动作不受 holdFrom 影响；非法流转仍抛 40900
        Assert.Equal(OrderStatus.OnHold, OrderStateMachine.Apply(OrderStatus.InProgress, OrderAction.Hold, holdFrom));
        Assert.Equal(40900, Assert.Throws<BizException>(() => OrderStateMachine.Apply(OrderStatus.Closed, OrderAction.Resume, holdFrom)).Code);
    }

    private static List<string> Act(Role role, int userId, OrderStatus status, int? assigneeId, ServiceType service = ServiceType.Onsite,
        bool missing = false, bool assistant = false, bool priceSet = true, bool pendingHold = false, bool canRecord = false, bool shippedBack = false) =>
        OrderStateMachine.Actions(new OrderStateMachine.ActionContext(role, userId, status, assigneeId, service, missing, assistant, priceSet,
            pendingHold, canRecord, shippedBack));

    [Fact]
    public void EngineerActions()
    {
        const Role E = Role.Engineer;
        Assert.Equal(new[] { "claim" }, Act(E, 2, OrderStatus.Pending, null));
        // 负责人
        Assert.Equal(new[] { "appoint", "checkin", "requestHold", "requestAssistant" }, Act(E, 2, OrderStatus.Accepted, 2));
        Assert.Equal(new[] { "appoint", "start", "requestHold", "requestAssistant" }, Act(E, 2, OrderStatus.Accepted, 2, ServiceType.Remote));
        Assert.Equal(new[] { "appoint", "checkin", "complete", "requestHold", "requestAssistant" }, Act(E, 2, OrderStatus.InProgress, 2));
        // 已有待审批的暂停申请 → 不再出现 requestHold
        Assert.Equal(new[] { "appoint", "checkin", "complete", "requestAssistant" }, Act(E, 2, OrderStatus.InProgress, 2, pendingHold: true));
        // 返厂：没有 start；未寄回不能 complete
        Assert.Equal(new[] { "appoint", "factory", "requestHold", "requestAssistant" },
            Act(E, 2, OrderStatus.Accepted, 2, ServiceType.Factory, canRecord: true));
        Assert.Equal(new[] { "appoint", "factory", "requestHold", "requestAssistant" },
            Act(E, 2, OrderStatus.InProgress, 2, ServiceType.Factory, canRecord: true));
        Assert.Equal(new[] { "appoint", "factory", "complete", "requestHold", "requestAssistant" },
            Act(E, 2, OrderStatus.InProgress, 2, ServiceType.Factory, canRecord: true, shippedBack: true));
        Assert.Equal(new[] { "factory" }, Act(E, 2, OrderStatus.Submitted, 2, ServiceType.Factory, canRecord: true, shippedBack: true));
        Assert.Empty(Act(E, 2, OrderStatus.OnHold, 2));
        Assert.Empty(Act(E, 2, OrderStatus.Submitted, 2));
        // 协助人：现场单只能打卡
        Assert.Equal(new[] { "checkin" }, Act(E, 2, OrderStatus.Accepted, 3, assistant: true));
        Assert.Equal(new[] { "checkin" }, Act(E, 2, OrderStatus.InProgress, 3, assistant: true));
        Assert.Empty(Act(E, 2, OrderStatus.OnHold, 3, assistant: true));
        Assert.Empty(Act(E, 2, OrderStatus.InProgress, 3, ServiceType.Remote, assistant: true));
        // 无关工程师
        Assert.Empty(Act(E, 2, OrderStatus.InProgress, 3));
        // 工程师永远没有价格动作
        foreach (var st in Enum.GetValues<OrderStatus>())
        {
            Assert.DoesNotContain("setPrice", Act(E, 2, st, 2));
            Assert.DoesNotContain("suggestPrice", Act(E, 2, st, 2));
        }
    }

    [Fact]
    public void SupervisorActions()
    {
        const Role S = Role.Supervisor;
        Assert.Equal(new[] { "assign", "void", "setPrice" }, Act(S, 1, OrderStatus.Pending, null));
        Assert.Equal(new[] { "reassign", "assistants", "void", "hold", "setPrice" }, Act(S, 1, OrderStatus.Accepted, 2));
        Assert.Equal(new[] { "reassign", "assistants", "hold", "setPrice" }, Act(S, 1, OrderStatus.InProgress, 2));
        Assert.Equal(new[] { "reassign", "assistants", "resume", "setPrice" }, Act(S, 1, OrderStatus.OnHold, 2));
        Assert.Equal(new[] { "reject", "close", "setPrice" }, Act(S, 1, OrderStatus.Submitted, 2));
        // 缺卡时禁止闭环，可补录
        Assert.Equal(new[] { "reject", "supplement", "setPrice" }, Act(S, 1, OrderStatus.Submitted, 2, missing: true));
        // 未定价时禁止闭环
        Assert.Equal(new[] { "reject", "setPrice" }, Act(S, 1, OrderStatus.Submitted, 2, priceSet: false));
        Assert.Equal(new[] { "setPrice" }, Act(S, 1, OrderStatus.Closed, 2));
        Assert.Empty(Act(S, 1, OrderStatus.Voided, 2));
        Assert.Equal(new[] { "reassign", "assistants", "hold", "setPrice", "factory" },
            Act(S, 1, OrderStatus.InProgress, 2, ServiceType.Factory, canRecord: true));
    }

    [Fact]
    public void SalesActions()
    {
        Assert.Equal(new[] { "suggestPrice" }, Act(Role.Sales, 6, OrderStatus.Pending, null, missing: true));
        Assert.Equal(new[] { "suggestPrice", "factory" }, Act(Role.Sales, 6, OrderStatus.Pending, null, ServiceType.Factory, canRecord: true));
        Assert.Empty(Act(Role.Sales, 6, OrderStatus.Closed, 2));
        Assert.Empty(Act(Role.Sales, 6, OrderStatus.Voided, 2));
    }

    [Theory]
    [InlineData(OrderStatus.Pending, null, 1)]
    [InlineData(OrderStatus.Accepted, null, 2)]
    [InlineData(OrderStatus.InProgress, null, 3)]
    [InlineData(OrderStatus.OnHold, OrderStatus.Accepted, 2)]
    [InlineData(OrderStatus.OnHold, OrderStatus.InProgress, 3)]
    [InlineData(OrderStatus.OnHold, null, 3)]
    [InlineData(OrderStatus.Submitted, null, 4)]
    [InlineData(OrderStatus.Closed, null, 5)]
    [InlineData(OrderStatus.Voided, null, 0)]
    public void ProgressSteps(OrderStatus s, OrderStatus? holdFrom, int step) => Assert.Equal(step, OrderStateMachine.ProgressStep(s, holdFrom));
}
