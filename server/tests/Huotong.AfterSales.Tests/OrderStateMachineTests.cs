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
    [InlineData(OrderStatus.OnHold, OrderAction.Resume, OrderStatus.InProgress)]
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

    [Fact]
    public void EngineerActions()
    {
        Assert.Equal(new[] { "claim" }, OrderStateMachine.Actions(Role.Engineer, 2, OrderStatus.Pending, null, ServiceType.Onsite, false));
        Assert.Equal(new[] { "appoint", "checkin" }, OrderStateMachine.Actions(Role.Engineer, 2, OrderStatus.Accepted, 2, ServiceType.Onsite, false));
        Assert.Equal(new[] { "appoint", "start" }, OrderStateMachine.Actions(Role.Engineer, 2, OrderStatus.Accepted, 2, ServiceType.Remote, false));
        Assert.Equal(new[] { "appoint", "checkin", "complete" }, OrderStateMachine.Actions(Role.Engineer, 2, OrderStatus.InProgress, 2, ServiceType.Onsite, false));
        Assert.Equal(new[] { "appoint", "complete" }, OrderStateMachine.Actions(Role.Engineer, 2, OrderStatus.InProgress, 2, ServiceType.Factory, false));
        Assert.Empty(OrderStateMachine.Actions(Role.Engineer, 2, OrderStatus.InProgress, 3, ServiceType.Onsite, false));
        Assert.Empty(OrderStateMachine.Actions(Role.Engineer, 2, OrderStatus.Submitted, 2, ServiceType.Onsite, false));
    }

    [Fact]
    public void SupervisorActions()
    {
        Assert.Equal(new[] { "assign", "void" }, OrderStateMachine.Actions(Role.Supervisor, 1, OrderStatus.Pending, null, ServiceType.Onsite, false));
        Assert.Equal(new[] { "reassign", "void" }, OrderStateMachine.Actions(Role.Supervisor, 1, OrderStatus.Accepted, 2, ServiceType.Onsite, false));
        Assert.Equal(new[] { "reassign", "hold" }, OrderStateMachine.Actions(Role.Supervisor, 1, OrderStatus.InProgress, 2, ServiceType.Onsite, false));
        Assert.Equal(new[] { "reassign", "resume" }, OrderStateMachine.Actions(Role.Supervisor, 1, OrderStatus.OnHold, 2, ServiceType.Onsite, false));
        Assert.Equal(new[] { "reject", "close" }, OrderStateMachine.Actions(Role.Supervisor, 1, OrderStatus.Submitted, 2, ServiceType.Onsite, false));
        // 缺卡时禁止闭环，可补录
        Assert.Equal(new[] { "reject", "supplement" }, OrderStateMachine.Actions(Role.Supervisor, 1, OrderStatus.Submitted, 2, ServiceType.Onsite, true));
        Assert.Empty(OrderStateMachine.Actions(Role.Supervisor, 1, OrderStatus.Closed, 2, ServiceType.Onsite, false));
    }

    [Fact]
    public void SalesHasNoActions() =>
        Assert.Empty(OrderStateMachine.Actions(Role.Sales, 6, OrderStatus.Pending, null, ServiceType.Onsite, true));

    [Theory]
    [InlineData(OrderStatus.Pending, 1)]
    [InlineData(OrderStatus.Accepted, 2)]
    [InlineData(OrderStatus.InProgress, 3)]
    [InlineData(OrderStatus.OnHold, 3)]
    [InlineData(OrderStatus.Submitted, 4)]
    [InlineData(OrderStatus.Closed, 5)]
    public void ProgressSteps(OrderStatus s, int step) => Assert.Equal(step, OrderStateMachine.ProgressStep(s));
}
