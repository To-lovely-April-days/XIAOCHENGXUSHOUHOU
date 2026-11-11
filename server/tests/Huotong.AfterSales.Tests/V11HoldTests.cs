using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.1 (2) 暂停搁置：原因、直接暂停、申请与审批、互斥、自动撤销</summary>
public sealed class V11HoldTests : V11TestBase
{
    [Fact]
    public async Task SupervisorHoldsFromAcceptedAndResumesBack()
    {
        var wang = U("王工");
        var id = await NewAccepted("暂停客户");
        await Fails(40900, "工单当前为「待认领」，不能执行此操作", async () =>
            await Orders.Hold(await NewPending("未认领客户"), HoldBody("WaitParts"), Sup));

        var before = Notifier.Sent.Count;
        var d = await Orders.Hold(id, HoldBody("WaitParts", " 轴承预计周五到 "), Sup);
        Assert.Equal(OrderStatus.OnHold, d.Status);
        Assert.Equal(2, d.ProgressStep);
        Assert.Equal(HoldReasonType.WaitParts, d.HoldReasonType);
        Assert.Equal("等备件：轴承预计周五到", d.HoldText);
        Assert.Equal("等备件：轴承预计周五到", d.HoldReason);
        Assert.Equal(OrderStatus.Accepted, d.HoldFromStatus);
        Assert.Equal("等备件", d.Hold!.ReasonText);
        Assert.Equal("轴承预计周五到", d.Hold.Note);
        Assert.Equal("等备件：轴承预计周五到", d.Hold.Text);
        Assert.Equal(OrderStatus.Accepted, d.Hold.FromStatus);
        Assert.Equal(Clock.Now, d.Hold.At);
        Assert.Equal("赵主管", d.Hold.By!.Name);
        Assert.Equal("赵主管 标为暂停搁置 → 暂停搁置（等备件：轴承预计周五到）", d.Logs.Last().Text);
        Assert.Equal(LogKind.Hold, d.Logs.Last().Kind);
        Assert.Equal(new[] { "reassign", "assistants", "resume", "setPrice" }, d.Actions);
        Assert.Equal(before + 1, Notifier.Sent.Count);
        Assert.Equal("工单已暂停搁置", Notifier.Last.Title);
        Assert.Equal($"**暂停客户**（{d.OrderNo}）已暂停搁置：等备件：轴承预计周五到", Notifier.Last.Text);
        Assert.Equal(new[] { Emp("王工").Phone }, Notifier.Last.Mobiles);
        // 列表、首页同样带暂停信息；工程师无动作
        var item = (await Orders.Mine(wang, "doing")).Items.Single(i => i.Id == id);
        Assert.Equal("等备件：轴承预计周五到", item.HoldText);
        Assert.Equal(2, item.ProgressStep);
        Assert.Contains((await Home.Engineer(wang)).OnHold, i => i.Id == id);
        Assert.Equal(new[] { "transfer" }, (await Orders.Detail(id, wang)).Actions); // v1.3：暂停搁置的负责人只能转交
        await Fails(40900, "工单当前为「暂停搁置」，不能执行此操作", () => Orders.Void(id, new ReasonRequest(), Sup));

        Tick();
        d = await Orders.Resume(id, Sup);
        Assert.Equal(OrderStatus.Accepted, d.Status);
        Assert.Null(d.Hold);
        Assert.Null(d.HoldText);
        Assert.Null(d.HoldReason);
        Assert.Null(d.HoldReasonType);
        Assert.Null(d.HoldFromStatus);
        Assert.Equal(2, d.ProgressStep);
        Assert.Equal("赵主管 恢复处理 → 待启动", d.Logs.Last().Text);
        Assert.Equal(LogKind.Hold, d.Logs.Last().Kind);
        Assert.Equal("工单已恢复处理", Notifier.Last.Title);
        Assert.Equal($"**暂停客户**（{d.OrderNo}）已恢复为「待启动」", Notifier.Last.Text);
        var row = Db.Orders.AsNoTracking().Single(o => o.Id == id);
        Assert.Null(row.HoldReason);
        Assert.Null(row.HoldReasonType);
        Assert.Null(row.HoldFromStatus);
        Assert.Null(row.HoldAt);
        Assert.Null(row.HoldById);

        // 处理中 → 暂停 → 恢复回处理中
        await Arrive(wang, id);
        Tick(30);
        await Leave(wang, id);
        d = await Orders.Hold(id, HoldBody("WaitCustomer"), Sup);
        Assert.Equal(3, d.ProgressStep);
        Assert.Equal("等客户", d.HoldText);
        Assert.Null(d.Hold!.Note);
        Assert.Equal(OrderStatus.InProgress, d.Hold.FromStatus);
        d = await Orders.Resume(id, Sup);
        Assert.Equal(OrderStatus.InProgress, d.Status);
        Assert.Equal("赵主管 恢复处理 → 处理中", d.Logs.Last().Text);
        Assert.Equal(40300, (await Biz(() => Orders.Hold(id, HoldBody("WaitParts"), wang))).Code);
    }

    [Fact]
    public async Task HoldValidation()
    {
        var id = await NewAccepted("暂停校验客户");
        await Fails(40000, "请选择暂停原因", () => Orders.Hold(id, new HoldRequest(), Sup));
        await Fails(40000, "请选择暂停原因", () => Orders.Hold(id, HoldBody("Nonsense"), Sup));
        await Fails(40000, "请选择暂停原因", () => Orders.Hold(id, HoldBody("1"), Sup));
        await Fails(40000, "请填写暂停说明", () => Orders.Hold(id, HoldBody("Other"), Sup));
        await Fails(40000, "请填写暂停说明", () => Orders.Hold(id, HoldBody("Other", "  "), Sup));
        await Fails(40000, "暂停说明不能超过 200 个字", () => Orders.Hold(id, HoldBody("WaitParts", new string('长', 201)), Sup));
        Assert.Equal(OrderStatus.Accepted, Db.Orders.AsNoTracking().Single(o => o.Id == id).Status);

        // 旧请求体 { reason } → Other + note
        var d = await Orders.Hold(id, new HoldRequest { Reason = "等待备件" }, Sup);
        Assert.Equal(HoldReasonType.Other, d.Hold!.ReasonType);
        Assert.Equal("等待备件", d.Hold.Note);
        Assert.Equal("等待备件", d.HoldReason);
        Assert.Equal("赵主管 标为暂停搁置 → 暂停搁置（等待备件）", d.Logs.Last().Text);
        await Orders.Resume(id, Sup);
        d = await Orders.Hold(id, HoldBody("Other", "客户厂区停电"), Sup);
        Assert.Equal("客户厂区停电", d.HoldText);
        Assert.Equal("其他", d.Hold!.ReasonText);
    }

    [Fact]
    public async Task LegacyHeldOrderWithoutReasonType()
    {
        // v1.1 之前的数据：OnHold 但没有原因类型 / 来源状态
        var id = await NewAccepted("旧暂停客户");
        var o = Db.Orders.Single(x => x.Id == id);
        o.Status = OrderStatus.OnHold;
        o.HoldReason = "等待备件";
        Db.SaveChanges();
        var d = await Orders.Detail(id, Sup);
        Assert.Equal(HoldReasonType.Other, d.HoldReasonType);
        Assert.Equal("等待备件", d.HoldText);
        Assert.Equal(OrderStatus.InProgress, d.HoldFromStatus);
        Assert.Equal(3, d.ProgressStep);
        Assert.Equal(OrderStatus.InProgress, d.Hold!.FromStatus);
        Assert.Null(d.Hold.At);
        Assert.Null(d.Hold.By);
        Assert.Equal(OrderStatus.InProgress, (await Orders.Resume(id, Sup)).Status);
    }

    [Fact]
    public async Task HoldRequestApproveRejectCancel()
    {
        var wang = U("王工");
        var liu = U("刘工");
        var id = await NewAccepted("申请暂停客户");
        var before = Notifier.Sent.Count;

        var d = await Orders.RequestHold(id, HoldBody("WaitParts", "轴承预计周五到"), wang);
        Assert.Equal(OrderStatus.Accepted, d.Status);
        Assert.Equal(1, d.PendingRequestCount);
        var req = Assert.Single(d.Requests);
        Assert.Equal(OrderRequestType.Hold, req.Type);
        Assert.Equal(RequestStatus.Pending, req.Status);
        Assert.Equal("王工", req.Requester.Name);
        Assert.Equal(HoldReasonType.WaitParts, req.ReasonType);
        Assert.Equal("等备件", req.ReasonText);
        Assert.Equal("等备件：轴承预计周五到", req.Text);
        Assert.True(req.CanCancel);
        Assert.False(req.CanDecide);
        Assert.Equal("王工 申请暂停搁置（等备件：轴承预计周五到）", d.Logs.Last().Text);
        Assert.Equal(LogKind.Hold, d.Logs.Last().Kind);
        Assert.DoesNotContain("requestHold", d.Actions);
        Assert.Equal(before + 1, Notifier.Sent.Count);
        Assert.Equal("暂停搁置申请", Notifier.Last.Title);
        Assert.Equal($"王工 申请暂停 **申请暂停客户**（{d.OrderNo}）：等备件：轴承预计周五到，请审批。", Notifier.Last.Text);
        Assert.Equal(new[] { Emp("赵主管").Phone }, Notifier.Last.Mobiles);

        var todo = (await Home.Supervisor(Sup)).Todos.Single(t => t.Type == "HoldRequest" && t.OrderId == id);
        Assert.Equal(req.Id, todo.RequestId);
        Assert.Equal("申请暂停客户", todo.Title);
        Assert.Equal("王工 申请暂停 · 等备件：轴承预计周五到", todo.Subtitle);
        Assert.Equal(1, (await Orders.Query(new OrderQuery { Keyword = "申请暂停客户" }, Sup)).Items.Single().PendingRequestCount);

        await Fails(40900, "已有待审批的暂停申请", () => Orders.RequestHold(id, HoldBody("WaitCustomer"), wang));
        await Fails(40300, "只有负责工程师可以申请暂停", () => Orders.RequestHold(id, HoldBody("WaitCustomer"), liu));
        await Fails(40000, "请填写暂停说明", () => Orders.RequestHold(id, HoldBody("Other"), wang));

        var sup = await Orders.Detail(id, Sup);
        Assert.True(sup.Requests[0].CanDecide);
        Assert.False(sup.Requests[0].CanCancel);
        Assert.Equal(40300, (await Biz(() => Orders.ApproveRequest(id, req.Id, wang))).Code);
        await Fails(40400, "申请不存在", () => Orders.ApproveRequest(id, 99999, Sup));
        await Fails(40400, "申请不存在", () => Orders.ApproveRequest(OrderId("南通某试剂厂"), req.Id, Sup));
        await Fails(40000, "回复不能超过 200 个字", () => Orders.RejectRequest(id, req.Id, new ReplyRequest { Reply = new string('长', 201) }, Sup));

        // 驳回：状态不变
        Tick();
        d = await Orders.RejectRequest(id, req.Id, new ReplyRequest { Reply = "备件明天到，先别停" }, Sup);
        Assert.Equal(OrderStatus.Accepted, d.Status);
        Assert.Equal(RequestStatus.Rejected, d.Requests[0].Status);
        Assert.Equal("备件明天到，先别停", d.Requests[0].Reply);
        Assert.Equal("赵主管", d.Requests[0].Decider!.Name);
        Assert.Equal(Clock.Now, d.Requests[0].DecidedAt);
        Assert.Equal(0, d.PendingRequestCount);
        Assert.Equal("赵主管 驳回暂停申请（备件明天到，先别停）", d.Logs.Last().Text);
        Assert.Equal("申请被驳回", Notifier.Last.Title);
        Assert.Equal($"**申请暂停客户**（{d.OrderNo}）的暂停申请被驳回：备件明天到，先别停", Notifier.Last.Text);
        Assert.Equal(new[] { Emp("王工").Phone }, Notifier.Last.Mobiles);
        await Fails(40900, "该申请已处理", () => Orders.ApproveRequest(id, req.Id, Sup));
        await Fails(40900, "该申请已处理", () => Orders.RejectRequest(id, req.Id, new ReplyRequest(), Sup));
        await Fails(40900, "该申请已处理", () => Orders.CancelRequest(id, req.Id, wang));

        // 再申请 → 只能本人撤回
        Tick();
        d = await Orders.RequestHold(id, HoldBody("WaitCustomer"), wang);
        var second = d.Requests.Last();
        await Fails(40300, "只能撤回本人的申请", () => Orders.CancelRequest(id, second.Id, liu));
        d = await Orders.CancelRequest(id, second.Id, wang);
        Assert.Equal(RequestStatus.Cancelled, d.Requests.Last().Status);
        Assert.Null(d.Requests.Last().Decider);
        Assert.Equal("王工 撤回暂停申请", d.Logs.Last().Text);
        Assert.Contains("requestHold", d.Actions);

        // 再申请 → 同意 → 暂停，原因取自申请
        Tick();
        d = await Orders.RequestHold(id, HoldBody("WaitThirdParty", "等第三方检测报告"), wang);
        var third = d.Requests.Last();
        Tick();
        d = await Orders.ApproveRequest(id, third.Id, Sup);
        Assert.Equal(OrderStatus.OnHold, d.Status);
        Assert.Equal(HoldReasonType.WaitThirdParty, d.Hold!.ReasonType);
        Assert.Equal("等第三方：等第三方检测报告", d.Hold.Text);
        Assert.Equal(OrderStatus.Accepted, d.Hold.FromStatus);
        Assert.Equal("赵主管", d.Hold.By!.Name);
        Assert.Equal(RequestStatus.Approved, d.Requests.Last().Status);
        Assert.Equal("赵主管 同意暂停申请 → 暂停搁置（等第三方：等第三方检测报告）", d.Logs.Last().Text);
        Assert.Equal("暂停申请已同意", Notifier.Last.Title);
        Assert.Equal($"**申请暂停客户**（{d.OrderNo}）已暂停搁置：等第三方：等第三方检测报告", Notifier.Last.Text);
        await Fails(40900, "工单当前为「暂停搁置」，不能申请暂停", () => Orders.RequestHold(id, HoldBody("WaitParts"), wang));
        Assert.Equal(3, d.Requests.Count); // 全部申请（含已处理），按创建时间正序
        Assert.Equal(d.Requests.OrderBy(r => r.CreatedAt).Select(r => r.Id), d.Requests.Select(r => r.Id));
    }

    [Fact]
    public async Task HoldBlockedWhileAssistantOnSite()
    {
        var liu = U("刘工");
        var wang = U("王工");
        var id = await NewAccepted("协助人在场客户", "刘工");
        await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { wang.Id } }, Sup);
        await Arrive(wang, id);

        await Fails(40900, "协助人王工正在现场计时，请先离场打卡后再搁置", () => Orders.Hold(id, HoldBody("WaitParts"), Sup));
        // 申请时不检查是否在现场，同意时检查
        var d = await Orders.RequestHold(id, HoldBody("WaitParts"), liu);
        var reqId = d.Requests.Single().Id;
        await Fails(40900, "协助人王工正在现场计时，请先离场打卡后再搁置", () => Orders.ApproveRequest(id, reqId, Sup));
        Assert.Equal(RequestStatus.Pending, RequestsOf(id).Single().Status);
        Assert.Equal(OrderStatus.InProgress, Db.Orders.AsNoTracking().Single(o => o.Id == id).Status);

        // 负责人在场 → 原文案
        await Arrive(liu, id);
        await Fails(40900, "工程师正在现场计时，请先离场打卡后再搁置", () => Orders.Hold(id, HoldBody("WaitParts"), Sup));

        Tick(10);
        await Leave(liu, id);
        await Leave(wang, id);
        d = await Orders.ApproveRequest(id, reqId, Sup);
        Assert.Equal(OrderStatus.OnHold, d.Status);
        // 通知 @申请人与协助人
        Assert.Equal(new[] { Emp("刘工").Phone, Emp("王工").Phone }, Notifier.Last.Mobiles);
    }

    [Fact]
    public async Task OnHoldBlocksPunchStartComplete()
    {
        var wang = U("王工");
        var onsite = await NewAccepted("暂停现场客户");
        await Orders.Hold(onsite, HoldBody("WaitCustomer"), Sup);

        var ctx = await Punches.Context(wang, onsite);
        Assert.Equal("工单已暂停搁置，恢复后才能打卡", ctx!.BlockedReason);
        await Fails(40900, "工单已暂停搁置，恢复后才能打卡", () => Arrive(wang, onsite));
        await Fails(40900, "工单已暂停搁置，恢复后才能提交完工", () => Orders.Complete(onsite, new CompleteRequest(), wang));
        await Fails(40900, "工单当前为「暂停搁置」，不能预约",
            () => Orders.Appointment(onsite, new AppointmentRequest { AppointmentTime = Clock.Now.AddDays(1) }, wang));

        var remote = await NewAccepted("暂停远程客户", service: ServiceType.Remote);
        await Orders.Hold(remote, HoldBody("WaitCustomer"), Sup);
        await Fails(40900, "工单已暂停搁置，恢复后才能开始处理", () => Orders.Start(remote, wang));
        await Orders.Resume(remote, Sup);
        Assert.Equal(OrderStatus.InProgress, (await Orders.Start(remote, wang)).Status);

        // 恢复后可以打卡
        await Orders.Resume(onsite, Sup);
        Assert.Equal(OrderStatus.InProgress, (await Arrive(wang, onsite)).Order.Status);
    }

    [Fact]
    public async Task PendingRequestsAutoCancelled()
    {
        var wang = U("王工");
        var liu = U("刘工");

        // a) 主管直接暂停：暂停申请撤销；协助人申请保留（OnHold 时主管仍可审批）
        var a = await NewAccepted("自动撤销A");
        await Orders.RequestHold(a, HoldBody("WaitParts"), wang);
        await Orders.RequestAssistant(a, new AssistantRequestRequest { EngineerId = liu.Id }, wang);
        var logs = (await Orders.Detail(a, Sup)).Logs.Count;
        Tick();
        var d = await Orders.Hold(a, HoldBody("WaitCustomer"), Sup);
        var ra = RequestsOf(a);
        Assert.Equal(RequestStatus.Cancelled, ra[0].Status);
        Assert.Equal("主管已直接暂停", ra[0].Reply);
        Assert.Null(ra[0].DeciderId);
        Assert.Equal(Clock.Now, ra[0].DecidedAt);
        Assert.Equal(RequestStatus.Pending, ra[1].Status);
        Assert.Equal(logs + 1, d.Logs.Count); // 自动撤销不写状态记录
        Assert.Equal(OrderStatus.OnHold, (await Orders.ApproveRequest(a, ra[1].Id, Sup)).Status);
        Assert.Equal("刘工", (await Orders.Detail(a, Sup)).Assistants.Single().Name);

        // b) 改派：原负责人发起的全部申请撤销
        var b = await NewAccepted("自动撤销B");
        await Orders.RequestHold(b, HoldBody("WaitParts"), wang);
        await Orders.RequestAssistant(b, new AssistantRequestRequest { EngineerId = liu.Id }, wang);
        await Orders.Assign(b, new AssignRequest { EngineerId = U("陈工").Id }, Sup);
        Assert.All(RequestsOf(b), r =>
        {
            Assert.Equal(RequestStatus.Cancelled, r.Status);
            Assert.Equal("负责人已变更", r.Reply);
        });

        // c) 完工：状态离开 待启动/处理中
        var c = await NewAccepted("自动撤销C", service: ServiceType.Remote);
        await Orders.Start(c, wang);
        await Orders.RequestHold(c, HoldBody("WaitParts"), wang);
        await Orders.RequestAssistant(c, new AssistantRequestRequest { EngineerId = liu.Id }, wang);
        await Orders.Complete(c, Done(wang, signature: false), wang);
        Assert.All(RequestsOf(c), r =>
        {
            Assert.Equal(RequestStatus.Cancelled, r.Status);
            Assert.Equal("工单状态已变化", r.Reply);
        });

        // d) 目标通过其他途径成为协助人
        var e = await NewAccepted("自动撤销D");
        await Orders.RequestAssistant(e, new AssistantRequestRequest { EngineerId = liu.Id }, wang);
        await Orders.SetAssistants(e, new AssistantsRequest { EngineerIds = new() { liu.Id } }, Sup);
        var re = RequestsOf(e).Single();
        Assert.Equal(RequestStatus.Cancelled, re.Status);
        Assert.Equal("已是本单参与人", re.Reply);

        // e) 作废
        var f = await NewAccepted("自动撤销E");
        await Orders.RequestHold(f, HoldBody("WaitParts"), wang);
        await Orders.Void(f, new ReasonRequest(), Sup);
        Assert.Equal("工单状态已变化", RequestsOf(f).Single().Reply);
        Assert.Equal(0, (await Orders.Detail(f, Sup)).PendingRequestCount);
    }
}
