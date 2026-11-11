using ClosedXML.Excel;
using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Tests;

/// <summary>
/// v1.3 转交：发起 / 确认接手 / 拒绝 / 撤回（撤销）、待确认期间的裁决、「已退出」的只读权限与持久化、
/// 主管改派的一致处理、列表与首页口径、并发（CHANGES-v1.3 §5–§7、§11.2 第 16–38 项）。
/// </summary>
public sealed class V13TransferTests : V13TestBase
{
    private const string Reason = "电气部分我搞不定，需要更熟的同事";
    private const string SupPhone = "13900000001";
    private const string WangPhone = "13900000004";
    private const string LiuPhone = "13900000003";

    private readonly FileCenterService _center;
    private int _phoneSeq;

    public V13TransferTests() => _center = new FileCenterService(Db, View);

    private CurrentUser Wang => U("王工");
    private CurrentUser Liu => U("刘工");
    private CurrentUser Chen => U("陈工");
    private CurrentUser Wm => U("王敏");

    /// <summary>没有任何历史工单 / 打卡的新工程师（数量、首页断言用）</summary>
    private CurrentUser NewEngineer(string name)
    {
        var e = new Employee
        {
            Name = name, Phone = $"1370000{++_phoneSeq:0000}", Role = Role.Engineer, Department = "售后部", Enabled = true, CreatedAt = Clock.Now
        };
        Db.Employees.Add(e);
        Db.SaveChanges();
        return new CurrentUser(e.Id, e.Role);
    }

    private Task<EmployeeDto> UpdateEmp(string name, Action<EmployeeUpdateRequest> f)
    {
        var e = Emp(name);
        var r = new EmployeeUpdateRequest { Name = e.Name, Phone = e.Phone, Role = e.Role, Department = e.Department, Enabled = e.Enabled };
        f(r);
        return Admin.UpdateEmployee(e.Id, r, Sup);
    }

    private static AssistantsRequest Set(params CurrentUser[] users) => new() { EngineerIds = users.Select(u => u.Id).ToList() };

    private async Task<string> No(int orderId) => (await Orders.Detail(orderId, Sup)).OrderNo;

    // ---------- 16：发起 ----------

    [Fact]
    public async Task StartCreatesPendingTransfer()
    {
        var id = await NewAccepted("转交客户", "王工");
        var d = await Orders.Transfer(id, To(Liu, "  " + Reason + " "), Wang);

        var t = d.PendingTransfer!;
        Assert.Equal(id, t.OrderId);
        Assert.Equal(TransferStatus.Pending, t.Status);
        Assert.Equal("待确认", t.StatusText);
        Assert.Equal(("王工", "刘工"), (t.From.Name, t.To.Name));
        Assert.Equal(Reason, t.Reason);
        Assert.Equal(Clock.Now, t.CreatedAt);
        Assert.Null(t.DecidedAt);
        Assert.Null(t.DecidedBy);
        Assert.Null(t.Reply);
        Assert.Equal("王工 转交给 刘工，待 刘工 确认", t.Text);
        Assert.Equal(t.Id, Assert.Single(d.Transfers).Id);

        var row = TransferRow(t.Id);
        Assert.Equal((TransferStatus.Pending, Wang.Id, Liu.Id), (row.Status, row.FromEngineerId, row.ToEngineerId));
        Assert.Equal(Liu.Id, OrderRow(id).PendingTransferToId);
        // 工单仍在原负责人手上，状态不变
        Assert.Equal(OrderStatus.Accepted, d.Status);
        Assert.Equal("王工", d.Assignee!.Name);
        Assert.Equal(ParticipantRole.Lead, d.MyRole);
        Assert.Null(d.ReadOnlyText);

        var log = d.Logs[^1];
        Assert.Equal($"王工 发起转交给 刘工：{Reason}", log.Text);
        Assert.Equal(LogKind.Assist, log.Kind);
        Assert.Equal(OrderStatus.Accepted, log.ToStatus);

        var n = Notifier.Last;
        Assert.Equal("工单转交", n.Title);
        Assert.Equal($"王工 想把 **转交客户**（{d.OrderNo}）转交给 刘工，原因：{Reason}。\n\n请 刘工 在小程序里确认接手或拒绝；确认前工单仍由 王工 负责。", n.Text);
        Assert.Equal(new[] { LiuPhone, SupPhone }, n.Mobiles); // 接手人在前

        // 发起人：有 cancelTransfer，没有 transfer；主管：多一个 cancelTransfer
        Assert.Equal(new[] { "appoint", "checkin", "requestHold", "requestAssistant", "cancelTransfer" }, d.Actions);
        Assert.Equal(new[] { "reassign", "assistants", "void", "hold", "setPrice", "cancelTransfer" }, (await Orders.Detail(id, Sup)).Actions);
        // 销售（建单人）也能看到待确认的转交，但没有任何转交动作
        var sales = await Orders.Detail(id, Wm);
        Assert.Equal(t.Id, sales.PendingTransfer!.Id);
        Assert.Equal(new[] { "suggestPrice" }, sales.Actions);
        AssertInvariants();
    }

    // ---------- 17：发起的校验 ----------

    [Fact]
    public async Task StartValidation()
    {
        var id = await NewAccepted("转交校验客户", "王工");
        await Orders.SetAssistants(id, Set(Chen), Sup);
        await Fails(40000, "请选择接手工程师", () => Orders.Transfer(id, new TransferRequest { Reason = "x" }, Wang));
        await Fails(40000, "请选择接手工程师", () => Orders.Transfer(999999, new TransferRequest(), Wang));
        await Fails(40000, "请填写转交原因", () => Orders.Transfer(id, To(Liu, null), Wang));
        await Fails(40000, "请填写转交原因", () => Orders.Transfer(999999, To(Liu, "   "), Wang));
        await Fails(40000, "转交原因不能超过 200 个字", () => Orders.Transfer(id, To(Liu, new string('x', 201)), Wang));
        await Fails(40400, "工单不存在", () => Orders.Transfer(999999, To(Liu), Wang));
        // 只有当前负责人：协助人、无关工程师都不行（固定文案，不泄露工单内容）
        await Fails(40300, "只有负责工程师可以转交工单", () => Orders.Transfer(id, To(Liu), Chen));
        await Fails(40300, "只有负责工程师可以转交工单", () => Orders.Transfer(id, To(Chen), Liu));
        await Fails(40000, "不能转交给自己", () => Orders.Transfer(id, To(Wang), Wang));
        // 接手人只能是启用中的工程师
        const string badTarget = "所选工程师不存在或已停用";
        await Fails(40000, badTarget, () => Orders.Transfer(id, new TransferRequest { EngineerId = 999999, Reason = "x" }, Wang));
        await Fails(40000, badTarget, () => Orders.Transfer(id, To(Wm), Wang));
        await Fails(40000, badTarget, () => Orders.Transfer(id, To(Sup), Wang));
        var disabled = NewEngineer("停用工");
        await UpdateEmp("停用工", r => r.Enabled = false);
        await Fails(40000, badTarget, () => Orders.Transfer(id, To(disabled), Wang));
        Assert.Empty(Db.OrderTransfers.Where(t => t.OrderId == id));
        Assert.Null(OrderRow(id).PendingTransferToId);

        // 原因刚好 200 字可以；可以转给本单协助人
        var d = await Orders.Transfer(id, To(Chen, new string('x', 200)), Wang);
        Assert.Equal("陈工", d.PendingTransfer!.To.Name);
        // 同一时间只能有一个待确认的转交
        await Fails(40900, "已有待确认的转交，请先撤回", () => Orders.Transfer(id, To(Liu), Wang));
        await Fails(40900, "已有待确认的转交，请先撤回", () => Orders.Transfer(id, To(Chen), Wang));
        AssertInvariants();
    }

    [Fact]
    public async Task StartAllowedOnlyInAcceptedInProgressOnHold()
    {
        // 待启动
        var accepted = await NewAccepted("状态-待启动", "王工", ServiceType.Remote);
        Assert.Equal(OrderStatus.Accepted, (await Orders.Transfer(accepted, To(Liu), Wang)).Status);
        // 处理中
        var doing = await NewAccepted("状态-处理中", "王工", ServiceType.Remote);
        await Orders.Start(doing, Wang);
        var d = await Orders.Transfer(doing, To(Liu), Wang);
        Assert.Equal(OrderStatus.InProgress, d.Status);
        Assert.Equal(OrderStatus.InProgress, d.Logs[^1].ToStatus);
        // 暂停搁置
        var held = await NewAccepted("状态-暂停", "王工", ServiceType.Remote);
        await Orders.Hold(held, HoldBody("WaitParts"), Sup);
        d = await Orders.Transfer(held, To(Liu), Wang);
        Assert.Equal(OrderStatus.OnHold, d.Status);
        Assert.Equal(new[] { "cancelTransfer" }, d.Actions);

        // 待认领：没有负责人
        var pending = await NewPending("状态-待认领");
        await Fails(40300, "只有负责工程师可以转交工单", () => Orders.Transfer(pending, To(Liu), Wang));
        // 待验收 / 已闭环 / 已作废（状态校验在「不能转交给自己」之前）
        var submitted = await NewAccepted("状态-待验收", "王工", ServiceType.Remote);
        await Orders.Start(submitted, Wang);
        await Orders.Complete(submitted, Done(Wang), Wang);
        await Fails(40900, "工单当前为「待验收」，不能转交", () => Orders.Transfer(submitted, To(Liu), Wang));
        await Fails(40900, "工单当前为「待验收」，不能转交", () => Orders.Transfer(submitted, To(Wang), Wang));
        Assert.DoesNotContain("transfer", (await Orders.Detail(submitted, Wang)).Actions);
        var closed = await NewClosed("状态-已闭环");
        await Fails(40900, "工单当前为「已闭环」，不能转交", () => Orders.Transfer(closed, To(Liu), Wang));
        var voided = await NewAccepted("状态-已作废", "王工");
        await Orders.Void(voided, new ReasonRequest(), Sup);
        await Fails(40900, "工单当前为「已作废」，不能转交", () => Orders.Transfer(voided, To(Liu), Wang));
        AssertInvariants();
    }

    [Fact]
    public async Task StartBlockedWhileOnSiteToday()
    {
        var id = await NewAccepted("现场计时客户", "王工");
        await Arrive(Wang, id);
        await Fails(40900, "你正在现场计时，请先离场打卡后再转交", () => Orders.Transfer(id, To(Liu), Wang));
        Tick(30);
        await Leave(Wang, id);
        Assert.NotNull((await Orders.Transfer(id, To(Liu), Wang)).PendingTransfer);
        AssertInvariants();
    }

    // ---------- 18、31、33：接手人确认之前 ----------

    [Fact]
    public async Task RecipientIsReadOnlyBeforeAccepting()
    {
        var a = NewEngineer("甲工");
        var id = await NewAccepted("接手人视角客户", "王工");
        await Orders.SetPrice(id, new PriceRequest { Price = 7654 }, Sup);
        await Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 7654, Note = "按合同" }, Wm);
        var pub = await UpPdf(Wm, "现场说明.pdf");
        var secret = await UpPdf(Wm, "底价清单.pdf");
        await Orders.AddAttachments(id, Att(pub), Wm);
        await Orders.AddAttachments(id, AttR(secret), Wm);
        var mineBefore = await Orders.Mine(a, "all");
        var activeBefore = (await Home.Me(a)).ActiveOrderCount;
        await Fails(40300, "无权查看该工单", () => Orders.Detail(id, a));

        var tid = await StartTransfer(id, Wang, a);

        // 能看详情：只有「确认接手 / 拒绝」，没有价格、没有受限附件及其记录
        var d = await Orders.Detail(id, a);
        Assert.Equal(new[] { "acceptTransfer", "rejectTransfer" }, d.Actions);
        Assert.Equal(tid, d.PendingTransfer!.Id);
        Assert.Null(d.MyRole);
        Assert.Null(d.MyExitText);
        Assert.Null(d.ReadOnlyText); // 只读提示只给已退出的人
        Assert.Null(d.Price);
        Assert.Null(d.PriceSet);
        Assert.Null(d.PriceSetAt);
        Assert.Null(d.PriceSuggestion);
        Assert.Equal(new[] { "现场说明.pdf" }, d.Attachments.Select(x => x.Name));
        Assert.DoesNotContain(d.Logs, l => l.Kind is LogKind.Price or LogKind.AttachmentPrivate);
        Assert.False(d.CanAddAttachment);
        Assert.False(d.CanEditSat);
        Assert.All(d.Attachments, x => Assert.False(x.CanDelete || x.CanChangeType || x.CanToggleRestricted));
        var json = Json(d);
        Assert.DoesNotContain("7654", json);
        Assert.DoesNotContain("7,654", json);
        Assert.DoesNotContain("底价清单", json);
        Assert.DoesNotContain(FileRow(secret).Path, json);
        // 联系人等与协助人相同的可见范围
        Assert.Equal("张工", d.ContactName);

        // 不能做任何写操作
        await Fails(40300, "只有本单的负责人或协助人可以打卡", () => Arrive(a, id));
        await Fails(40300, "只有本单的负责人或协助人可以打卡", () => Punches.Context(a, id));
        await Fails(40300, "只有建单人、负责工程师、协助人或主管可以添加附件", async () => await Orders.AddAttachments(id, Att(await UpPdf(a, "x.pdf")), a));
        await Fails(40300, "只能删除自己上传的附件", () => Orders.DeleteAttachment(id, pub, a));
        await Fails(40300, "只能修改自己上传的附件", () => Orders.SetAttachmentType(id, pub, new AttachmentTypeRequest { Type = "Sat" }, a));
        await Fails(40400, "附件不存在", () => Orders.DeleteAttachment(id, secret, a));
        await Fails(40300, "只有建单人、负责工程师或主管可以修改 SAT 信息", () => Orders.SetSat(id, new SatRequest { SatSigned = false }, a));
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Appointment(id, new AppointmentRequest { AppointmentTime = Clock.Now.AddDays(1) }, a));
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Start(id, a));
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Complete(id, Done(a), a));
        await Fails(40300, "只有负责工程师可以申请暂停", () => Orders.RequestHold(id, HoldBody("WaitParts"), a));
        await Fails(40300, "只有负责工程师可以申请协助人", () => Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = Chen.Id }, a));
        await Fails(40300, "只有负责工程师可以转交工单", () => Orders.Transfer(id, To(Chen), a));
        await Fails(40300, "只有发起人或主管可以撤回转交", () => Orders.CancelTransfer(id, tid, a));
        await Fails(40900, "工单已被他人认领", () => Orders.Claim(id, a));
        Db.ChangeTracker.Clear();

        // 首页「待我确认的转交」里有；「我的工单」任何分组里都没有；进行中数量不变
        var home = await Home.Engineer(a);
        var item = Assert.Single(home.TransfersToMe);
        Assert.Equal((id, tid), (item.Id, item.PendingTransfer!.Id));
        Assert.Null(item.Price);
        Assert.Null(item.MyRole);
        Assert.Null(home.Current);
        Assert.Null(home.Next);
        Assert.Empty(home.Upcoming);
        Assert.Empty(home.OnHold);
        Assert.Empty(home.RecentDone);
        Assert.Equal("idle", home.State);
        foreach (var g in new[] { "doing", "check", "closed", "former", "all" })
            Assert.Empty((await Orders.Mine(a, g)).Items);
        Assert.Equal(mineBefore.Counts, (await Orders.Mine(a, "all")).Counts);
        Assert.Equal(activeBefore, (await Home.Me(a)).ActiveOrderCount);

        // 文件下载 / 文件中心：非受限的可以，受限的 40400
        Assert.Equal("现场说明.pdf", (await Files.ResolveDownload(pub, a)).Name);
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(secret, a));
        Assert.Equal(new[] { pub }, (await _center.Query(new FileCenterQuery { OrderId = id }, a)).Items.Select(i => i.Id));
        // 转交相关通知正文不出现金额、附件名
        Assert.All(Notifier.Sent.Where(n => n.Title.Contains("转交")), n => Assert.DoesNotContain("7654", n.Text));
        Assert.All(Notifier.Sent.Where(n => n.Title.Contains("转交")), n => Assert.DoesNotContain("底价", n.Text));
        AssertInvariants();
    }

    // ---------- 19：确认接手 ----------

    [Fact]
    public async Task AcceptMakesRecipientLeadAndExitsOldLead()
    {
        var id = await NewAccepted("确认接手客户", "王工");
        var appointment = Clock.Now.AddDays(2);
        await Orders.Appointment(id, new AppointmentRequest { AppointmentTime = appointment }, Wang);
        await Orders.RequestHold(id, HoldBody("WaitParts"), Wang);
        await Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = Chen.Id }, Wang);
        var tid = await StartTransfer(id, Wang, Liu);
        var oldClaimed = OrderRow(id).ClaimedAt;
        Tick(5);

        var d = await Orders.AcceptTransfer(id, tid, Liu);
        Assert.Equal("刘工", d.Assignee!.Name);
        Assert.Equal(ParticipantRole.Lead, d.MyRole);
        Assert.Equal(Clock.Now, d.ClaimedAt);
        Assert.NotEqual(oldClaimed, d.ClaimedAt);
        Assert.Equal(OrderStatus.Accepted, d.Status); // 状态不变
        Assert.Equal(new DateTime(appointment.Year, appointment.Month, appointment.Day, appointment.Hour, appointment.Minute, 0), d.AppointmentTime); // 沿用预约
        Assert.Null(d.PendingTransfer);
        Assert.Null(OrderRow(id).PendingTransferToId);

        var x = Assert.Single(ExitsOf(id));
        Assert.Equal((Wang.Id, ExitVia.Transfer, Clock.Now), (x.EngineerId, x.Via, x.ExitedAt));
        var row = TransferRow(tid);
        Assert.Equal((TransferStatus.Accepted, (DateTime?)Clock.Now, (int?)Liu.Id, (string?)null), (row.Status, row.DecidedAt, row.DecidedById, row.Reply));
        var t = Assert.Single(d.Transfers);
        Assert.Equal(("已接手", "王工 转交给 刘工，刘工 已接手", "刘工"), (t.StatusText, t.Text, t.DecidedBy!.Name));

        Assert.Equal("刘工 确认接手（原负责人 王工）", d.Logs[^1].Text);
        Assert.Equal(LogKind.Assist, d.Logs[^1].Kind);
        var n = Notifier.Last;
        Assert.Equal("工单已转交", n.Title);
        Assert.Equal($"刘工 已接手 **确认接手客户**（{d.OrderNo}），原负责人 王工 退出本单。\n\nHTR-500 · 不加热", n.Text);
        Assert.Equal(new[] { WangPhone, SupPhone }, n.Mobiles); // 发起人在前

        // 原负责人待审批的暂停 / 协助人申请自动撤销
        Assert.All(RequestsOf(id), r =>
        {
            Assert.Equal(RequestStatus.Cancelled, r.Status);
            Assert.Equal("负责人已变更", r.Reply);
            Assert.Null(r.DeciderId);
        });
        Assert.Equal(2, RequestsOf(id).Count);
        // 新负责人拥有负责人的全部动作
        Assert.Equal(new[] { "appoint", "checkin", "requestHold", "requestAssistant", "transfer" }, d.Actions);
        Assert.Equal(new[] { (ParticipantRole.Lead, "刘工", (string?)null), (ParticipantRole.Former, "王工", "已转交") },
            d.Participants.Select(p => (p.Role, p.User.Name, p.ExitText)));
        Assert.Equal(Clock.Now, d.Participants[1].ExitedAt);
        Assert.Null(d.Participants[0].ExitedAt);
        AssertInvariants();
    }

    // ---------- 20：接手人原本是协助人 ----------

    [Fact]
    public async Task AcceptingAssistantIsRemovedFromAssistants()
    {
        var a = NewEngineer("甲工");
        var id = await NewAccepted("协助人接手客户", "王工");
        await Orders.SetAssistants(id, Set(a, Chen), Sup);
        var tid = await StartTransfer(id, Wang, a);

        // 协助人照常有自己的动作，再加确认 / 拒绝
        var before = await Orders.Detail(id, a);
        Assert.Equal(new[] { "checkin", "acceptTransfer", "rejectTransfer" }, before.Actions);
        Assert.Equal(ParticipantRole.Assistant, before.MyRole);
        // 既在「待我确认的转交」里，也照常出现在本人的工单里（不去重）
        var home = await Home.Engineer(a);
        Assert.Equal(id, Assert.Single(home.TransfersToMe).Id);
        Assert.Equal(id, home.Next!.Id);
        Assert.Contains((await Orders.Mine(a, "doing")).Items, i => i.Id == id);

        var d = await Orders.AcceptTransfer(id, tid, a);
        Assert.Equal("甲工", d.Assignee!.Name);
        Assert.Equal(new[] { "陈工" }, d.Assistants.Select(x => x.Name));
        Assert.DoesNotContain(d.Logs, l => l.Text.Contains("移除协助人"));
        Assert.Equal(new[] { (ParticipantRole.Lead, "甲工"), (ParticipantRole.Assistant, "陈工"), (ParticipantRole.Former, "王工") },
            d.Participants.Select(p => (p.Role, p.User.Name)));
        Assert.Empty((await Home.Engineer(a)).TransfersToMe);
        AssertInvariants();
    }

    // ---------- 21：确认接手被拦 ----------

    [Fact]
    public async Task AcceptBlockedAndRepeated()
    {
        var id = await NewAccepted("接手被拦客户", "王工");
        var other = await NewAccepted("另一张单", "王工");
        var tid = await StartTransfer(id, Wang, Liu);
        // 确认前工单仍在原负责人手上，一切照常：发起之后又到场了
        var p = await Arrive(Wang, id);
        Assert.Equal(OrderStatus.InProgress, p.Order.Status);
        Assert.Equal(tid, p.Order.PendingTransfer!.Id); // 打卡返回里的工单项也带 pendingTransfer
        await Fails(40900, "王工正在现场计时，请其先离场打卡后再确认接手", () => Orders.AcceptTransfer(id, tid, Liu));

        await Fails(40300, "只有接手人可以确认接手", () => Orders.AcceptTransfer(id, tid, Chen));
        await Fails(40300, "只有接手人可以确认接手", () => Orders.AcceptTransfer(id, tid, Wang));
        await Fails(40400, "转交不存在", () => Orders.AcceptTransfer(id, 999999, Liu));
        await Fails(40400, "转交不存在", () => Orders.AcceptTransfer(other, tid, Liu)); // 不属于该工单
        await Fails(40400, "工单不存在", () => Orders.AcceptTransfer(999999, tid, Liu));
        Db.ChangeTracker.Clear();
        Assert.Equal(TransferStatus.Pending, TransferRow(tid).Status);

        Tick(20);
        await Leave(Wang, id);
        var d = await Orders.AcceptTransfer(id, tid, Liu);
        Assert.Equal("刘工", d.Assignee!.Name);
        Assert.Equal(OrderStatus.InProgress, d.Status);
        // 重复确认
        await Fails(40900, "该转交已处理或已失效", () => Orders.AcceptTransfer(id, tid, Liu));
        await Fails(40900, "该转交已处理或已失效", () => Orders.RejectTransfer(id, tid, new ReplyRequest(), Liu));
        // 已退出的原负责人撤回自己那条（已结束）→ 40900
        await Fails(40900, "该转交已处理或已失效", () => Orders.CancelTransfer(id, tid, Wang));
        await Fails(40900, "该转交已处理或已失效", () => Orders.CancelTransfer(id, tid, Sup));
        AssertInvariants();
    }

    [Fact]
    public async Task PastMissingPunchDoesNotBlockTransfer()
    {
        // 往日缺卡（到场后没打离场、已过当天）不拦转交，仍由主管补录
        var id = await NewAccepted("往日缺卡客户", "王工");
        var arrive = (await Arrive(Wang, id)).Punch;
        Clock.Now = Clock.Now.AddDays(1);
        var tid = await StartTransfer(id, Wang, Liu);
        var d = await Orders.AcceptTransfer(id, tid, Liu);
        Assert.Contains(IssueType.MissingPunch, d.Issues);
        var former = d.Participants.Single(x => x.Role == ParticipantRole.Former);
        Assert.True(former.MissingPunch);
        Assert.Equal("已转交", former.ExitText);
        // 已退出的人自己不能补卡；主管补录不要求被补录人还是参与人
        await Fails(40300, "只有本单的负责人或协助人可以打卡", () => Leave(Wang, id));
        d = await Admin.Supplement(new SupplementRequest
        {
            OrderId = id, ArrivePunchId = arrive.Id, Kind = PunchKind.Leave, Time = arrive.Time.AddHours(2), Reason = "忘记打卡"
        }, Sup);
        Assert.DoesNotContain(IssueType.MissingPunch, d.Issues);
        Assert.Equal(120, d.Participants.Single(x => x.Role == ParticipantRole.Former).TotalMinutes);
        AssertInvariants();
    }

    // ---------- 22、38：拒绝 ----------

    [Fact]
    public async Task RejectWithAndWithoutReason()
    {
        var id = await NewAccepted("拒绝客户", "王工", ServiceType.Remote);
        await Orders.Start(id, Wang);
        var no = await No(id);
        var tid = await StartTransfer(id, Wang, Liu);
        await Fails(40000, "拒绝理由不能超过 200 个字", () => Orders.RejectTransfer(id, tid, new ReplyRequest { Reply = new string('x', 201) }, Liu));
        await Fails(40300, "只有接手人可以拒绝", () => Orders.RejectTransfer(id, tid, new ReplyRequest(), Wang));
        await Fails(40300, "只有接手人可以拒绝", () => Orders.RejectTransfer(id, tid, new ReplyRequest(), Chen));
        await Fails(40400, "转交不存在", () => Orders.RejectTransfer(id, 999999, new ReplyRequest(), Liu));
        await Fails(40400, "工单不存在", () => Orders.RejectTransfer(999999, tid, new ReplyRequest(), Liu));

        Tick();
        // 带理由；接手人不是本单其他身份 → 返回 null（已无查看权）
        var r = await Orders.RejectTransfer(id, tid, new ReplyRequest { Reply = " 这周都在外地，排不开 " }, Liu);
        Assert.Null(r);
        await Fails(40300, "无权查看该工单", () => Orders.Detail(id, Liu));
        Assert.Empty((await Home.Engineer(Liu)).TransfersToMe);
        var row = TransferRow(tid);
        Assert.Equal((TransferStatus.Rejected, (DateTime?)Clock.Now, (int?)Liu.Id, "这周都在外地，排不开"), (row.Status, row.DecidedAt, row.DecidedById, row.Reply));
        Assert.Null(OrderRow(id).PendingTransferToId);
        var n = Notifier.Last;
        Assert.Equal("转交被拒绝", n.Title);
        Assert.Equal($"刘工 拒绝接手 **拒绝客户**（{no}）：这周都在外地，排不开。工单仍由 王工 负责。", n.Text);
        Assert.Equal(new[] { WangPhone, SupPhone }, n.Mobiles);

        // 发起人：工单其他内容不变，transfer / complete 重新出现
        var d = await Orders.Detail(id, Wang);
        Assert.Equal("刘工 拒绝接手：这周都在外地，排不开", d.Logs[^1].Text);
        Assert.Equal(LogKind.Assist, d.Logs[^1].Kind);
        Assert.Equal("王工", d.Assignee!.Name);
        Assert.Null(d.PendingTransfer);
        Assert.Equal(new[] { "appoint", "complete", "requestHold", "requestAssistant", "transfer" }, d.Actions);
        var t = Assert.Single(d.Transfers);
        Assert.Equal(("已拒绝", "王工 转交给 刘工，刘工 已拒绝：这周都在外地，排不开", "这周都在外地，排不开"), (t.StatusText, t.Text, t.Reply));
        Assert.Empty(ExitsOf(id));

        // 可以立刻再次转交给同一个人；不带理由
        Tick();
        var tid2 = await StartTransfer(id, Wang, Liu);
        Assert.NotEqual(tid, tid2);
        Assert.Null(await Orders.RejectTransfer(id, tid2, new ReplyRequest { Reply = "  " }, Liu));
        Assert.Null(TransferRow(tid2).Reply);
        Assert.Equal($"刘工 拒绝接手 **拒绝客户**（{no}）。工单仍由 王工 负责。", Notifier.Last.Text);
        d = await Orders.Detail(id, Wang);
        Assert.Equal("刘工 拒绝接手", d.Logs[^1].Text);
        Assert.Equal(new[] { "王工 转交给 刘工，刘工 已拒绝：这周都在外地，排不开", "王工 转交给 刘工，刘工 已拒绝" }, d.Transfers.Select(x => x.Text));
        await Fails(40900, "该转交已处理或已失效", () => Orders.RejectTransfer(id, tid2, new ReplyRequest(), Liu));

        // 接手人同时是协助人 → 拒绝后仍能查看，返回最新详情
        await Orders.SetAssistants(id, Set(Chen), Sup);
        var tid3 = await StartTransfer(id, Wang, Chen);
        var back = await Orders.RejectTransfer(id, tid3, new ReplyRequest(), Chen);
        Assert.NotNull(back);
        Assert.Equal(ParticipantRole.Assistant, back!.MyRole);
        Assert.Null(back.PendingTransfer);
        Assert.Equal(3, back.Transfers.Count);
        Assert.Empty(back.Actions); // 远程单的协助人没有动作
        AssertInvariants();
    }

    // ---------- 23、24：撤回 / 撤销 ----------

    [Fact]
    public async Task CancelByRequesterAndSupervisor()
    {
        var id = await NewAccepted("撤回客户", "王工");
        var no = await No(id);
        var tid = await StartTransfer(id, Wang, Liu);
        await Fails(40300, "只有发起人或主管可以撤回转交", () => Orders.CancelTransfer(id, tid, Chen));
        await Fails(40300, "只有发起人或主管可以撤回转交", () => Orders.CancelTransfer(id, tid, Liu));
        await Fails(40300, "只有发起人或主管可以撤回转交", () => Orders.CancelTransfer(id, tid, Wm));
        await Fails(40400, "转交不存在", () => Orders.CancelTransfer(id, 999999, Wang));
        await Fails(40400, "工单不存在", () => Orders.CancelTransfer(999999, tid, Wang));

        // 发起人撤回
        Tick();
        var d = await Orders.CancelTransfer(id, tid, Wang);
        Assert.Null(d.PendingTransfer);
        Assert.Equal("王工 撤回转交（刘工）", d.Logs[^1].Text);
        Assert.Equal(LogKind.Assist, d.Logs[^1].Kind);
        var t = Assert.Single(d.Transfers);
        Assert.Equal((TransferStatus.Cancelled, "已撤回", "王工 转交给 刘工，已撤回", "王工"), (t.Status, t.StatusText, t.Text, t.DecidedBy!.Name));
        Assert.Equal(Clock.Now, t.DecidedAt);
        Assert.Equal("转交已撤回", Notifier.Last.Title);
        Assert.Equal($"王工 撤回了 **撤回客户**（{no}）转交给 刘工 的请求。", Notifier.Last.Text);
        Assert.Equal(new[] { LiuPhone, SupPhone }, Notifier.Last.Mobiles); // 接手人在前
        Assert.Contains("transfer", d.Actions);
        Assert.DoesNotContain("cancelTransfer", d.Actions);
        await Fails(40300, "无权查看该工单", () => Orders.Detail(id, Liu));
        await Fails(40900, "该转交已处理或已失效", () => Orders.CancelTransfer(id, tid, Wang));
        await Fails(40900, "该转交已处理或已失效", () => Orders.AcceptTransfer(id, tid, Liu));

        // 撤回后可以再次发起；主管撤销
        Tick();
        var tid2 = await StartTransfer(id, Wang, Liu);
        Tick();
        d = await Orders.CancelTransfer(id, tid2, Sup);
        Assert.Null(d.PendingTransfer);
        Assert.Equal("赵主管 撤销转交（王工 → 刘工）", d.Logs[^1].Text);
        Assert.Equal(LogKind.Assist, d.Logs[^1].Kind);
        Assert.Equal(2, d.Transfers.Count); // 历史两条都在
        t = d.Transfers[1];
        // 主管撤销的那一条 status 也是 Cancelled、statusText 也是「已撤回」，区别只在 text 与 decidedBy
        Assert.Equal((TransferStatus.Cancelled, "已撤回", "王工 转交给 刘工，已由 赵主管 撤销", "赵主管"), (t.Status, t.StatusText, t.Text, t.DecidedBy!.Name));
        Assert.Equal("转交已撤销", Notifier.Last.Title);
        Assert.Equal($"赵主管 撤销了 **撤回客户**（{no}）由 王工 转交给 刘工 的请求，工单仍由 王工 负责。", Notifier.Last.Text);
        Assert.Equal(new[] { WangPhone, LiuPhone }, Notifier.Last.Mobiles); // 发起人、接手人
        Assert.DoesNotContain("cancelTransfer", d.Actions);
        await Fails(40300, "无权查看该工单", () => Orders.Detail(id, Liu));
        Assert.Empty(ExitsOf(id));
        AssertInvariants();
    }

    // ---------- 25：待确认期间发生其他事情 ----------

    [Fact]
    public async Task CompleteIsBlockedWhilePending()
    {
        var id = await NewAccepted("完工拦截客户", "王工", ServiceType.Remote);
        await Orders.Start(id, Wang);
        var tid = await StartTransfer(id, Wang, Liu);
        var d = await Orders.Detail(id, Wang);
        Assert.Equal(new[] { "appoint", "requestHold", "requestAssistant", "cancelTransfer" }, d.Actions); // 没有 complete、没有 transfer
        await Fails(40900, "本单有待确认的转交，请先撤回转交后再提交完工", () => Orders.Complete(id, Done(Wang), Wang));
        Db.ChangeTracker.Clear();
        Assert.Equal(OrderStatus.InProgress, OrderRow(id).Status);
        // 其余照常：预约、申请暂停 / 协助人都可以
        await Orders.Appointment(id, new AppointmentRequest { AppointmentTime = Clock.Now.AddDays(1) }, Wang);
        await Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = Chen.Id }, Wang);
        // 撤回后可以提交完工
        await Orders.CancelTransfer(id, tid, Wang);
        Assert.Equal(OrderStatus.Submitted, (await Orders.Complete(id, Done(Wang), Wang)).Status);
        AssertInvariants();
    }

    [Fact]
    public async Task ReassignExpiresPendingTransfer()
    {
        var id = await NewAccepted("改派失效客户", "王工");
        var tid = await StartTransfer(id, Wang, Liu);
        Tick();
        var d = await Orders.Assign(id, new AssignRequest { EngineerId = Chen.Id }, Sup);
        var row = TransferRow(tid);
        Assert.Equal((TransferStatus.Expired, (DateTime?)Clock.Now, (int?)null, "主管已改派"), (row.Status, row.DecidedAt, row.DecidedById, row.Reply));
        Assert.Null(OrderRow(id).PendingTransferToId);
        Assert.Null(d.PendingTransfer);
        // 失效记录在改派记录之前，时间相同
        Assert.Equal(new[] { "转交已失效（王工 → 刘工）：主管已改派", "赵主管 改派给 陈工（原 王工）" }, d.Logs.TakeLast(2).Select(l => l.Text));
        Assert.Equal(LogKind.Assist, d.Logs[^2].Kind);
        Assert.Equal("赵主管", d.Logs[^2].Actor.Name);
        Assert.Equal(d.Logs[^1].Time, d.Logs[^2].Time);
        var t = Assert.Single(d.Transfers);
        Assert.Equal(("已失效", "王工 转交给 刘工，已失效：主管已改派"), (t.StatusText, t.Text));
        Assert.Null(t.DecidedBy);
        Assert.DoesNotContain("cancelTransfer", d.Actions);
        // 系统失效不单独发通知（只有改派自己的通知）
        Assert.Equal("工单指派提醒", Notifier.Last.Title);
        Assert.DoesNotContain(Notifier.Sent, n => n.Title.Contains("失效"));
        await Fails(40300, "无权查看该工单", () => Orders.Detail(id, Liu));
        await Fails(40900, "该转交已处理或已失效", () => Orders.AcceptTransfer(id, tid, Liu));
        Assert.Equal("已改派", (await Orders.Detail(id, Wang)).MyExitText);

        // 改派给转交的接手人：转交同样失效，他通过改派成为负责人
        var id2 = await NewAccepted("改派给接手人客户", "王工");
        var tid2 = await StartTransfer(id2, Wang, Liu);
        d = await Orders.Assign(id2, new AssignRequest { EngineerId = Liu.Id }, Sup);
        Assert.Equal("刘工", d.Assignee!.Name);
        Assert.Equal(TransferStatus.Expired, TransferRow(tid2).Status);
        Assert.Equal(new[] { "转交已失效（王工 → 刘工）：主管已改派", "赵主管 改派给 刘工（原 王工）" }, d.Logs.TakeLast(2).Select(l => l.Text));
        Assert.Equal(ExitVia.Reassign, Assert.Single(ExitsOf(id2)).Via);

        // 没有待确认转交时改派：不多出失效记录
        var id3 = await NewAccepted("普通改派客户", "王工");
        d = await Orders.Assign(id3, new AssignRequest { EngineerId = Liu.Id }, Sup);
        Assert.DoesNotContain(d.Logs, l => l.Text.Contains("转交已失效"));
        AssertInvariants();
    }

    [Fact]
    public async Task VoidExpiresPendingTransfer()
    {
        var id = await NewAccepted("作废失效客户", "王工");
        var tid = await StartTransfer(id, Wang, Liu);
        Tick();
        var d = await Orders.Void(id, new ReasonRequest { Reason = "客户取消" }, Sup);
        Assert.Equal(OrderStatus.Voided, d.Status);
        var row = TransferRow(tid);
        Assert.Equal((TransferStatus.Expired, (int?)null, "工单已作废"), (row.Status, row.DecidedById, row.Reply));
        Assert.Null(OrderRow(id).PendingTransferToId);
        Assert.Equal(new[] { "转交已失效（王工 → 刘工）：工单已作废", "赵主管 作废 → 已作废（客户取消）" }, d.Logs.TakeLast(2).Select(l => l.Text));
        // 失效记录的 ToStatus = 作废前的状态
        Assert.Equal(OrderStatus.Accepted, d.Logs[^2].ToStatus);
        Assert.Equal(OrderStatus.Voided, d.Logs[^1].ToStatus);
        Assert.Empty(d.Actions);
        Assert.Empty((await Home.Engineer(Liu)).TransfersToMe);
        await Fails(40300, "无权查看该工单", () => Orders.Detail(id, Liu));
        AssertInvariants();
    }

    [Fact]
    public async Task HoldAndResumeKeepPendingTransfer()
    {
        var id = await NewAccepted("暂停不影响客户", "王工", ServiceType.Remote);
        var tid = await StartTransfer(id, Wang, Liu);
        var d = await Orders.Hold(id, HoldBody("WaitParts"), Sup);
        Assert.Equal(tid, d.PendingTransfer!.Id);
        Assert.Equal(new[] { "reassign", "assistants", "resume", "setPrice", "cancelTransfer" }, d.Actions);
        d = await Orders.Resume(id, Sup);
        Assert.Equal(tid, d.PendingTransfer!.Id);
        Assert.Equal(TransferStatus.Pending, TransferRow(tid).Status);
        // 主管调整协助人：不影响；接手人被加为协助人后仍可确认接手
        await Orders.SetAssistants(id, Set(Liu, Chen), Sup);
        await Orders.Hold(id, HoldBody("WaitCustomer"), Sup);
        AssertInvariants();
        // 暂停搁置时也可以确认接手，状态不变
        d = await Orders.AcceptTransfer(id, tid, Liu);
        Assert.Equal(OrderStatus.OnHold, d.Status);
        Assert.Equal("刘工", d.Assignee!.Name);
        Assert.Equal(new[] { "陈工" }, d.Assistants.Select(a => a.Name));
        Assert.Equal(new[] { "transfer" }, d.Actions);
        AssertInvariants();
    }

    // ---------- 25、36：接手人 / 发起人被停用、改角色 ----------

    [Fact]
    public async Task DisablingRecipientExpiresAllHisPendingTransfers()
    {
        var a = NewEngineer("甲工");
        var id1 = await NewAccepted("停用失效客户一", "王工");
        var id2 = await NewAccepted("停用失效客户二", "陈工", ServiceType.Remote);
        var t1 = await StartTransfer(id1, Wang, a);
        var t2 = await StartTransfer(id2, Chen, a);

        // 只改姓名 / 手机号 / 部门：不触发
        await UpdateEmp("甲工", r => { r.Name = "甲工改"; r.Phone = "13700009999"; r.Department = "技术部"; });
        Assert.Equal(TransferStatus.Pending, TransferRow(t1).Status);
        Assert.Equal(TransferStatus.Pending, TransferRow(t2).Status);
        Assert.Equal("王工 转交给 甲工改，待 甲工改 确认", (await Orders.Detail(id1, Sup)).PendingTransfer!.Text);
        AssertInvariants();

        // 停用（同时改了姓名）：两张单都失效，各写一条记录；接手人姓名取修改后的
        Tick();
        var notices = Notifier.Sent.Count;
        await UpdateEmp("甲工改", r => { r.Enabled = false; r.Name = "甲工停"; });
        foreach (var (oid, tid, from) in new[] { (id1, t1, "王工"), (id2, t2, "陈工") })
        {
            var row = TransferRow(tid);
            Assert.Equal((TransferStatus.Expired, (DateTime?)Clock.Now, (int?)null, "接手人账号已停用"), (row.Status, row.DecidedAt, row.DecidedById, row.Reply));
            Assert.Null(OrderRow(oid).PendingTransferToId);
            var d = await Orders.Detail(oid, Sup);
            Assert.Null(d.PendingTransfer);
            Assert.Equal($"转交已失效（{from} → 甲工停）：接手人账号已停用", d.Logs[^1].Text);
            Assert.Equal((LogKind.Assist, "赵主管", d.Status), (d.Logs[^1].Kind, d.Logs[^1].Actor.Name, d.Logs[^1].ToStatus));
            Assert.Equal($"{from} 转交给 甲工停，已失效：接手人账号已停用", d.Transfers.Single().Text);
            Assert.Contains("transfer", (await Orders.Detail(oid, U(from))).Actions);
        }
        Assert.Equal(notices, Notifier.Sent.Count); // 不发通知
        AssertInvariants();
    }

    [Fact]
    public async Task ChangingRecipientRoleExpiresTransfer()
    {
        var a = NewEngineer("甲工");
        var b = NewEngineer("乙工");
        var id = await NewAccepted("改角色失效客户", "王工");
        var tid = await StartTransfer(id, Wang, a);
        await UpdateEmp("甲工", r => r.Role = Role.Sales);
        Assert.Equal((TransferStatus.Expired, "接手人已不是工程师"), (TransferRow(tid).Status, TransferRow(tid).Reply));
        Assert.Equal("转交已失效（王工 → 甲工）：接手人已不是工程师", (await Orders.Detail(id, Sup)).Logs[^1].Text);
        Assert.Null(OrderRow(id).PendingTransferToId);

        // 停用与改角色同时发生：取「已停用」
        var tid2 = await StartTransfer(id, Wang, b);
        await UpdateEmp("乙工", r => { r.Role = Role.Sales; r.Enabled = false; });
        Assert.Equal((TransferStatus.Expired, "接手人账号已停用"), (TransferRow(tid2).Status, TransferRow(tid2).Reply));
        AssertInvariants();
    }

    [Fact]
    public async Task DisablingRequesterKeepsTransferPending()
    {
        var a = NewEngineer("甲工");
        var id = await NewAccepted("停用发起人客户", "甲工", ServiceType.Remote);
        var tid = await StartTransfer(id, a, Liu);
        await UpdateEmp("甲工", r => r.Enabled = false);
        Assert.Equal(TransferStatus.Pending, TransferRow(tid).Status);
        Assert.Equal(Liu.Id, OrderRow(id).PendingTransferToId);
        AssertInvariants();
        // 接手人仍可确认接手（正好把单子接过去）
        var d = await Orders.AcceptTransfer(id, tid, Liu);
        Assert.Equal("刘工", d.Assignee!.Name);
        Assert.Equal(new[] { (ParticipantRole.Lead, "刘工"), (ParticipantRole.Former, "甲工") }, d.Participants.Select(p => (p.Role, p.User.Name)));
        AssertInvariants();
    }

    // ---------- 26、31：已退出的原负责人 ----------

    [Fact]
    public async Task FormerLeadIsReadOnlyEvenAsCreator()
    {
        var a = NewEngineer("甲工");
        // 工程师自己建单、自己认领：既是建单人又是负责人
        var id = (await Orders.Create(NewOrder("已退出只读客户"), a)).Id;
        await Orders.Claim(id, a);
        await Orders.SetPrice(id, new PriceRequest { Price = 7654 }, Sup);
        var mineFile = await UpPdf(a, "我上传的.pdf");
        await Orders.AddAttachments(id, Att(mineFile), a);
        await Orders.AddAttachments(id, AttR(await UpPdf(Sup, "底价清单.pdf")), Sup);
        var rid = (await Orders.RequestHold(id, HoldBody("WaitParts"), a)).Requests.Single().Id;
        var doingBefore = (await Orders.Mine(a, "doing")).Counts;
        Assert.Equal(1, (await Home.Me(a)).ActiveOrderCount);
        var tid = await StartTransfer(id, a, Liu);
        Tick();
        await Orders.AcceptTransfer(id, tid, Liu);

        // 能看（只读）
        var d = await Orders.Detail(id, a);
        Assert.Equal(ParticipantRole.Former, d.MyRole);
        Assert.Equal("已转交", d.MyExitText);
        Assert.Empty(d.Actions);
        Assert.Equal("你已退出本单（已转交），只能查看；你的打卡和工时记录已保留。", d.ReadOnlyText);
        Assert.False(d.CanAddAttachment);
        Assert.False(d.CanEditSat);
        var att = Assert.Single(d.Attachments);
        Assert.Equal(mineFile, att.Id);
        Assert.False(att.CanDelete);
        Assert.False(att.CanChangeType);
        Assert.Null(d.Price);
        Assert.Null(d.PriceSet);
        var json = Json(d);
        Assert.DoesNotContain("7654", json);
        Assert.DoesNotContain("底价清单", json);
        // 主管 / 销售不是「已退出」
        Assert.Null((await Orders.Detail(id, Sup)).ReadOnlyText);

        // 不能再做任何操作（各自文案）；他是建单人也一样
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Appointment(id, new AppointmentRequest { AppointmentTime = Clock.Now.AddDays(1) }, a));
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Start(id, a));
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Complete(id, Done(a), a));
        await Fails(40300, "只有负责工程师可以申请暂停", () => Orders.RequestHold(id, HoldBody("WaitParts"), a));
        await Fails(40300, "只有负责工程师可以申请协助人", () => Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = Chen.Id }, a));
        await Fails(40300, "只有负责工程师可以转交工单", () => Orders.Transfer(id, To(Chen), a));
        await Fails(40300, "只有本单的负责人或协助人可以打卡", () => Arrive(a, id));
        await Fails(40300, "只有本单的负责人或协助人可以打卡", () => Punches.Context(a, id));
        await Fails(40300, FormerDenied, async () => await Orders.AddAttachments(id, Att(await UpPdf(a, "再传.pdf")), a));
        await Fails(40300, FormerDenied, () => Orders.DeleteAttachment(id, mineFile, a));
        await Fails(40300, FormerDenied, () => Orders.SetAttachmentType(id, mineFile, new AttachmentTypeRequest { Type = "Sat" }, a));
        await Fails(40300, FormerDenied, () => Orders.SetSat(id, new SatRequest { SatSigned = false }, a));
        await Fails(40900, "该申请已处理", () => Orders.CancelRequest(id, rid, a)); // 他的申请在退出时已被自动撤销
        await Fails(40900, "该转交已处理或已失效", () => Orders.CancelTransfer(id, tid, a));
        await Fails(40900, "工单已被他人认领", () => Orders.Claim(id, a));
        Db.ChangeTracker.Clear();
        Assert.Equal(mineFile, (await Orders.Detail(id, Sup)).Attachments.First().Id); // 什么都没改

        // 别人发起的转交他也不能撤回
        var tid2 = await StartTransfer(id, Liu, Chen);
        await Fails(40300, "只有发起人或主管可以撤回转交", () => Orders.CancelTransfer(id, tid2, a));
        await Orders.CancelTransfer(id, tid2, Liu);

        // 「我的工单」：在 former 里，不在 doing 里；counts 正确；首页、进行中数量都不含
        var former = await Orders.Mine(a, "former");
        var item = Assert.Single(former.Items);
        Assert.Equal((id, ParticipantRole.Former, "已转交"), (item.Id, item.MyRole!.Value, item.MyExitText));
        Assert.Equal(0, item.MyMinutes);
        Assert.Empty((await Orders.Mine(a, "doing")).Items);
        Assert.Equal(new Dictionary<string, int> { ["doing"] = 0, ["check"] = 0, ["closed"] = 0, ["former"] = 1, ["all"] = 1 }, former.Counts);
        Assert.Equal(1, doingBefore["doing"]);
        Assert.Equal(id, Assert.Single((await Orders.Mine(a, "all")).Items).Id);
        Assert.Equal(id, Assert.Single((await Orders.Mine(a, null)).Items).Id);
        await Fails(40000, "分组参数不正确", () => Orders.Mine(a, "nope"));
        var home = await Home.Engineer(a);
        Assert.Null(home.Current);
        Assert.Null(home.Next);
        Assert.Empty(home.Upcoming);
        Assert.Empty(home.OnHold);
        Assert.Empty(home.RecentDone);
        Assert.Empty(home.TransfersToMe);
        Assert.Equal(0, (await Home.Me(a)).ActiveOrderCount);
        // 他建的单仍在「我提交的工单」里
        Assert.Contains((await Orders.Created(a, "all")).Items, i => i.Id == id && i.MyRole == ParticipantRole.Former);
        AssertInvariants();
    }

    [Fact]
    public async Task FormerCannotRecordFactoryNodes()
    {
        var a = NewEngineer("甲工");
        var id = (await Orders.Create(NewOrder("已退出返厂客户", ServiceType.Factory), a)).Id;
        await Orders.Claim(id, a);
        Assert.Contains("factory", (await Orders.Detail(id, a)).Actions);
        var tid = await StartTransfer(id, a, Liu);
        await Orders.AcceptTransfer(id, tid, Liu);

        var d = await Orders.Detail(id, a);
        Assert.Empty(d.Actions);
        Assert.All(d.Factory!.Nodes, n => Assert.False(n.CanRecord || n.CanEdit));
        // 建单人本来可以记节点 1、5；已退出优先
        await Fails(40300, FormerDenied, () => Orders.RecordFactoryNode(id, Node(FactoryNode.CustomerShipped, "顺丰", "SF1"), a));
        await Fails(40300, FormerDenied,
            () => Orders.RecordFactoryNode(id, Node(FactoryNode.FactoryReceived, null, null, NewFile(a, FileCategory.FactoryReceivePhoto)), a));
        // 新负责人照常
        d = await Orders.RecordFactoryNode(id, Node(FactoryNode.CustomerShipped, "顺丰", "SF1"), Liu);
        Assert.Contains("factory", d.Actions);
        AssertInvariants();
    }

    [Fact]
    public async Task FormerListSortedByExitTime()
    {
        var a = NewEngineer("甲工");
        var first = await NewAccepted("先转交的单", "甲工", ServiceType.Remote);
        var second = await NewAccepted("后转交的单", "甲工", ServiceType.Remote);
        var keep = await NewAccepted("还在手上的单", "甲工", ServiceType.Remote);
        await Orders.AcceptTransfer(first, await StartTransfer(first, a, Liu), Liu);
        Tick(10);
        await Orders.AcceptTransfer(second, await StartTransfer(second, a, Liu), Liu);
        // 已退出的工单被闭环 / 作废后：闭环的仍在 former，作废的不在
        var voided = await NewAccepted("转交后作废的单", "甲工");
        await Orders.AcceptTransfer(voided, await StartTransfer(voided, a, Liu), Liu);
        await Orders.Void(voided, new ReasonRequest(), Sup);
        await Orders.Start(first, Liu);
        await Orders.Complete(first, Done(Liu), Liu);
        await Orders.SetPrice(first, new PriceRequest { Price = 0 }, Sup);
        await Orders.Close(first, Sup);

        var former = await Orders.Mine(a, "former");
        Assert.Equal(new[] { second, first }, former.Items.Select(i => i.Id)); // 按退出时间倒序
        Assert.Equal(new Dictionary<string, int> { ["doing"] = 1, ["check"] = 0, ["closed"] = 0, ["former"] = 2, ["all"] = 3 }, former.Counts);
        Assert.Empty((await Orders.Mine(a, "closed")).Items); // 已退出的不混进 closed
        Assert.Equal(new[] { keep, second, first }, (await Orders.Mine(a, "all")).Items.Select(i => i.Id)); // former 排在最后
        Assert.Equal(1, (await Home.Me(a)).ActiveOrderCount);
        AssertInvariants();
    }

    // ---------- 27：参与人列表与工时 ----------

    [Fact]
    public async Task FormerWithoutPunchesAppearsInParticipants()
    {
        var id = await NewAccepted("远程转交客户", "王工", ServiceType.Remote);
        await Orders.SetAssistants(id, Set(Chen), Sup);
        Tick();
        await Orders.AcceptTransfer(id, await StartTransfer(id, Wang, Liu), Liu);
        var d = await Orders.Detail(id, Sup);
        Assert.Equal(new[] { (ParticipantRole.Lead, "刘工"), (ParticipantRole.Assistant, "陈工"), (ParticipantRole.Former, "王工") },
            d.Participants.Select(p => (p.Role, p.User.Name)));
        var f = d.Participants[2];
        Assert.Equal(("已转交", (DateTime?)Clock.Now, 0, (DateTime?)null, false), (f.ExitText, f.ExitedAt, f.TotalMinutes, f.OnSiteSince, f.MissingPunch));
        Assert.Empty(d.Punches);
    }

    [Fact]
    public async Task FormerHoursStayWithHimAndFormersAreOrderedByTime()
    {
        var id = await NewAccepted("工时保留客户", "王工");
        var a = NewEngineer("甲工");
        await Orders.SetAssistants(id, Set(a), Sup);
        // 协助人甲工先打卡，之后被移除（按打卡推导的「已退出」，没有退出记录、没有查看权）
        await Arrive(a, id);
        Tick(20);
        await Leave(a, id);
        await Orders.SetAssistants(id, Set(), Sup);
        // 负责人王工打卡 35 分钟后转交
        Tick(5);
        await Arrive(Wang, id);
        Tick(35);
        await Leave(Wang, id);
        Tick(5);
        await Orders.AcceptTransfer(id, await StartTransfer(id, Wang, Liu), Liu);

        var d = await Orders.Detail(id, Sup);
        // 已退出：按打卡推导的（首次打卡时间）与有退出记录的（退出时间）混在一起按时间正序
        Assert.Equal(new[] { (ParticipantRole.Lead, "刘工", 0, (string?)null), (ParticipantRole.Former, "甲工", 20, null), (ParticipantRole.Former, "王工", 35, "已转交") },
            d.Participants.Select(p => (p.Role, p.User.Name, p.TotalMinutes, p.ExitText)));
        Assert.Null(d.Participants[1].ExitedAt);
        Assert.Equal(55, d.TotalMinutes);
        Assert.Equal(4, d.Punches.Count); // 打卡记录原样保留
        await Fails(40300, "无权查看该工单", () => Orders.Detail(id, a)); // 被移除的协助人仍然看不了

        // 原负责人自己的视角：工时仍显示
        var mine = (await Orders.Mine(Wang, "former")).Items.Single(i => i.Id == id);
        Assert.Equal(35, mine.MyMinutes);
        Assert.Null(mine.MyOnSiteSince);
        // 月度工时与导出仍算在他名下，角色为「已退出」
        var month = await Hours.Month(Wang.Id, Clock.Now.Year, Clock.Now.Month);
        var entry = month.Days.SelectMany(x => x.Entries).Single(e => e.OrderId == id);
        Assert.Equal((35, ParticipantRole.Former), (entry.Minutes, entry.Role));
        using var wb = new XLWorkbook(new MemoryStream(await Admin.ExportHours(Clock.Now.Year, Clock.Now.Month)));
        var row = wb.Worksheet(1).RowsUsed().Single(r => r.Cell(1).GetString() == "王工" && r.Cell(4).GetString() == "工时保留客户");
        Assert.Equal("已退出", row.Cell(10).GetString());
        Assert.Equal(0.6, row.Cell(7).GetDouble(), 1);
        // 工单导出的人员工时
        using var wb2 = new XLWorkbook(new MemoryStream(await Admin.ExportOrders(null, null, null)));
        var orow = wb2.Worksheet(1).RowsUsed().Single(r => r.Cell(3).GetString() == "工时保留客户");
        Assert.Equal("刘工", orow.Cell(12).GetString());
        Assert.Contains("王工 0.6h", orow.Cell(24).GetString());
        AssertInvariants();
    }

    // ---------- 28、37：主管改派与退出记录 ----------

    [Fact]
    public async Task ReassignWritesAndRemovesExitRecords()
    {
        var id = await NewAccepted("改派退出客户", "王工");
        var up = await UpPdf(Wang, "王工传的.pdf");
        await Orders.AddAttachments(id, Att(up), Wang);
        Tick();
        // 改派：原负责人得到退出记录（已改派），只读
        await Orders.Assign(id, new AssignRequest { EngineerId = Liu.Id }, Sup);
        var x = Assert.Single(ExitsOf(id));
        Assert.Equal((Wang.Id, ExitVia.Reassign, Clock.Now), (x.EngineerId, x.Via, x.ExitedAt));
        var d = await Orders.Detail(id, Wang);
        Assert.Equal((ParticipantRole.Former, "已改派"), (d.MyRole!.Value, d.MyExitText));
        Assert.Equal("你已退出本单（已改派），只能查看；你的打卡和工时记录已保留。", d.ReadOnlyText);
        Assert.Empty(d.Actions);
        await Fails(40300, FormerDenied, () => Orders.DeleteAttachment(id, up, Wang));
        Assert.Contains((await Orders.Mine(Wang, "former")).Items, i => i.Id == id && i.MyExitText == "已改派");
        AssertInvariants();

        // 改派同时把原负责人设为协助人 → 他没有退出记录
        Tick();
        d = await Orders.Assign(id, new AssignRequest { EngineerId = Chen.Id, AssistantIds = new() { Liu.Id } }, Sup);
        Assert.Equal(new[] { Wang.Id }, ExitsOf(id).Select(e => e.EngineerId));
        Assert.Equal(ParticipantRole.Assistant, (await Orders.Detail(id, Liu)).MyRole);
        Assert.Equal(new[] { (ParticipantRole.Lead, "陈工"), (ParticipantRole.Assistant, "刘工"), (ParticipantRole.Former, "王工") },
            d.Participants.Select(p => (p.Role, p.User.Name)));
        AssertInvariants();

        // 把已退出的人加为协助人 → 退出记录删除，恢复协助人权限
        await Orders.SetAssistants(id, Set(Liu, Wang), Sup);
        Assert.Empty(ExitsOf(id));
        d = await Orders.Detail(id, Wang);
        Assert.Equal(ParticipantRole.Assistant, d.MyRole);
        Assert.Null(d.ReadOnlyText);
        Assert.True(d.CanAddAttachment);
        Assert.True(d.Attachments.Single().CanDelete);
        AssertInvariants();

        // 改派回来 → 他是负责人；被换掉的陈工成为已退出
        Tick();
        d = await Orders.Assign(id, new AssignRequest { EngineerId = Wang.Id }, Sup);
        Assert.Equal(new[] { Chen.Id }, ExitsOf(id).Select(e => e.EngineerId));
        Assert.Equal(new[] { "appoint", "checkin", "requestHold", "requestAssistant", "transfer" }, (await Orders.Detail(id, Wang)).Actions);
        AssertInvariants();

        // 同意协助人申请：目标是已退出的人 → 退出记录删除
        var rid = (await Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = Chen.Id }, Wang)).Requests.Last().Id;
        d = await Orders.ApproveRequest(id, rid, Sup);
        Assert.Empty(ExitsOf(id));
        Assert.Equal(ParticipantRole.Assistant, (await Orders.Detail(id, Chen)).MyRole);
        AssertInvariants();
    }

    [Fact]
    public async Task FormerAddedAsAssistantThenRemovedLosesAccess()
    {
        var id = await NewAccepted("退出后加为协助人又移除", "王工", ServiceType.Remote);
        await Orders.AcceptTransfer(id, await StartTransfer(id, Wang, Liu), Liu);
        Assert.Single(ExitsOf(id));
        await Orders.SetAssistants(id, Set(Wang), Sup);
        Assert.Empty(ExitsOf(id));
        await Orders.SetAssistants(id, Set(), Sup);
        // 与其他被移除的协助人一样：退出记录不恢复，不再有查看权
        Assert.Empty(ExitsOf(id));
        await Fails(40300, "无权查看该工单", () => Orders.Detail(id, Wang));
        Assert.DoesNotContain((await Orders.Mine(Wang, "all")).Items, i => i.Id == id);
        AssertInvariants();
    }

    // ---------- 29：来回转交 ----------

    [Fact]
    public async Task TransferBackAndForth()
    {
        var a = NewEngineer("甲工");
        var b = NewEngineer("乙工");
        var id = await NewAccepted("来回转交客户", "甲工", ServiceType.Remote);
        await Orders.AcceptTransfer(id, await StartTransfer(id, a, b), b);
        Assert.Equal(new[] { a.Id }, ExitsOf(id).Select(x => x.EngineerId));

        // 乙转回给甲（甲此时是已退出的人）：确认前仍只读，只多出确认 / 拒绝
        Tick();
        var tid = await StartTransfer(id, b, a, "还是你熟");
        var d = await Orders.Detail(id, a);
        Assert.Equal(new[] { "acceptTransfer", "rejectTransfer" }, d.Actions);
        Assert.Equal(ParticipantRole.Former, d.MyRole);
        Assert.NotNull(d.ReadOnlyText);
        await Fails(40300, FormerDenied, async () => await Orders.AddAttachments(id, Att(await UpPdf(a, "x.pdf")), a));
        Assert.Equal(id, Assert.Single((await Home.Engineer(a)).TransfersToMe).Id);
        Assert.Equal(id, Assert.Single((await Orders.Mine(a, "former")).Items).Id); // 同时按「已退出」身份出现在我的工单里
        // 拒绝：他是已退出的人，仍能查看 → 返回详情而不是 null
        var back = await Orders.RejectTransfer(id, tid, new ReplyRequest { Reply = "排不开" }, a);
        Assert.NotNull(back);
        Assert.Empty(back!.Actions);
        Assert.Equal(ParticipantRole.Former, back.MyRole);

        // 再次转回并确认：甲是负责人、没有退出记录；乙有退出记录
        Tick();
        tid = await StartTransfer(id, b, a, "还是你熟");
        d = await Orders.AcceptTransfer(id, tid, a);
        Assert.Equal("甲工", d.Assignee!.Name);
        Assert.Equal(ParticipantRole.Lead, d.MyRole);
        Assert.Null(d.ReadOnlyText);
        Assert.Equal(new[] { "appoint", "start", "requestHold", "requestAssistant", "transfer" }, d.Actions);
        var x = Assert.Single(ExitsOf(id));
        Assert.Equal((b.Id, ExitVia.Transfer), (x.EngineerId, x.Via));
        Assert.Equal(new[] { (ParticipantRole.Lead, "甲工"), (ParticipantRole.Former, "乙工") }, d.Participants.Select(p => (p.Role, p.User.Name)));
        Assert.Equal(new[] { "已接手", "已拒绝", "已接手" }, d.Transfers.Select(t => t.StatusText));
        Assert.Equal(ParticipantRole.Former, (await Orders.Detail(id, b)).MyRole);
        Assert.True((await Orders.Detail(id, a)).CanAddAttachment);
        AssertInvariants();
    }

    // ---------- 32：actions 示例（§2.9） ----------

    [Fact]
    public void ActionsExamples()
    {
        static List<string> Act(Role role, int userId, OrderStatus status, int? assigneeId, ServiceType service = ServiceType.Onsite,
            bool assistant = false, bool former = false, bool pending = false, bool toMe = false) =>
            OrderStateMachine.Actions(new OrderStateMachine.ActionContext(role, userId, status, assigneeId, service, false, assistant,
                IsFormer: former, HasPendingTransfer: pending, PendingTransferToMe: toMe));
        const Role E = Role.Engineer;
        // 负责人、无待确认转交
        Assert.Equal(new[] { "appoint", "checkin", "requestHold", "requestAssistant", "transfer" }, Act(E, 2, OrderStatus.Accepted, 2));
        Assert.Equal(new[] { "appoint", "start", "requestHold", "requestAssistant", "transfer" }, Act(E, 2, OrderStatus.Accepted, 2, ServiceType.Remote));
        Assert.Equal(new[] { "appoint", "checkin", "complete", "requestHold", "requestAssistant", "transfer" }, Act(E, 2, OrderStatus.InProgress, 2));
        Assert.Equal(new[] { "transfer" }, Act(E, 2, OrderStatus.OnHold, 2));
        Assert.Empty(Act(E, 2, OrderStatus.Submitted, 2));
        Assert.Empty(Act(E, 2, OrderStatus.Closed, 2));
        Assert.Empty(Act(E, 2, OrderStatus.Voided, 2));
        // 负责人、有待确认转交
        Assert.Equal(new[] { "appoint", "checkin", "requestHold", "requestAssistant", "cancelTransfer" }, Act(E, 2, OrderStatus.InProgress, 2, pending: true));
        Assert.Equal(new[] { "cancelTransfer" }, Act(E, 2, OrderStatus.OnHold, 2, pending: true));
        // 接手人
        Assert.Equal(new[] { "acceptTransfer", "rejectTransfer" }, Act(E, 3, OrderStatus.InProgress, 2, pending: true, toMe: true));
        Assert.Equal(new[] { "checkin", "acceptTransfer", "rejectTransfer" }, Act(E, 3, OrderStatus.InProgress, 2, assistant: true, pending: true, toMe: true));
        // 协助人 / 无关工程师看到别人的待确认转交：没有任何转交动作
        Assert.Equal(new[] { "checkin" }, Act(E, 4, OrderStatus.InProgress, 2, assistant: true, pending: true));
        Assert.Empty(Act(E, 4, OrderStatus.InProgress, 2, pending: true));
        // 已退出
        Assert.Empty(Act(E, 3, OrderStatus.InProgress, 2, former: true));
        Assert.Empty(Act(E, 3, OrderStatus.InProgress, 2, former: true, pending: true));
        Assert.Equal(new[] { "acceptTransfer", "rejectTransfer" }, Act(E, 3, OrderStatus.InProgress, 2, former: true, pending: true, toMe: true));
        // 主管
        Assert.Equal(new[] { "reassign", "assistants", "hold", "setPrice", "cancelTransfer" }, Act(Role.Supervisor, 1, OrderStatus.InProgress, 2, pending: true));
        Assert.Equal(new[] { "reassign", "assistants", "hold", "setPrice" }, Act(Role.Supervisor, 1, OrderStatus.InProgress, 2));
        // 销售不变
        Assert.Equal(new[] { "suggestPrice" }, Act(Role.Sales, 9, OrderStatus.InProgress, 2, pending: true));
        Assert.True(OrderStateMachine.CanTransfer(OrderStatus.Accepted) && OrderStateMachine.CanTransfer(OrderStatus.InProgress) && OrderStateMachine.CanTransfer(OrderStatus.OnHold));
        Assert.False(OrderStateMachine.CanTransfer(OrderStatus.Pending) || OrderStateMachine.CanTransfer(OrderStatus.Submitted)
                     || OrderStateMachine.CanTransfer(OrderStatus.Closed) || OrderStateMachine.CanTransfer(OrderStatus.Voided));
    }

    // ---------- 33：文件下载 ----------

    [Fact]
    public async Task DownloadFollowsViewPermission()
    {
        var a = NewEngineer("甲工");
        var id = await NewAccepted("下载权限客户", "王工");
        var pub = await UpPdf(Wm, "公开说明.pdf");
        var secret = await UpPdf(Wm, "受限报价.pdf");
        await Orders.AddAttachments(id, Att(pub), Wm);
        await Orders.AddAttachments(id, AttR(secret), Wm);
        // 真实落盘的打卡照片
        var photo = (await Up(Wang, Jpg, "arrive.jpg", "PunchPhoto")).Id;
        await Punches.Create(Wang, new PunchRequest { OrderId = id, Kind = PunchKind.Arrive, Latitude = 31.30, Longitude = 120.72, Address = "x", PhotoFileId = photo });
        Tick(10);
        await Leave(Wang, id);

        async Task CanGet(CurrentUser u)
        {
            Assert.Equal("公开说明.pdf", (await Files.ResolveDownload(pub, u)).Name);
            Assert.NotNull(await Files.ResolveDownload(photo, u));
            await Fails(40400, "文件不存在", () => Files.ResolveDownload(secret, u));
            // 文件中心（按用户处理可见性）：能查看该工单 → 含非受限文件
            var ids = (await _center.Query(new FileCenterQuery { OrderId = id, PageSize = 200 }, u)).Items.Select(i => i.Id).ToList();
            Assert.Contains(pub, ids);
            Assert.Contains(photo, ids);
            Assert.DoesNotContain(secret, ids);
        }
        async Task Denied(CurrentUser u)
        {
            await Fails(40300, "无权查看该工单", () => Files.ResolveDownload(pub, u));
            await Fails(40300, "无权查看该工单", () => Files.ResolveDownload(photo, u));
            await Fails(40300, "无权查看该工单", () => Files.ResolveDownload(secret, u));
            Assert.Empty((await _center.Query(new FileCenterQuery { OrderId = id, PageSize = 200 }, u)).Items);
        }

        await Denied(a);
        await Denied(Chen); // 无关工程师始终 40300
        // 待确认的接手人
        var tid = await StartTransfer(id, Wang, a);
        await CanGet(a);
        await Denied(Chen);
        // 拒绝之后
        Assert.Null(await Orders.RejectTransfer(id, tid, new ReplyRequest(), a));
        await Denied(a);
        // 被撤回之后
        tid = await StartTransfer(id, Wang, a);
        await CanGet(a);
        await Orders.CancelTransfer(id, tid, Wang);
        await Denied(a);
        // 接手之后：原负责人（已退出）仍能下载非受限文件
        tid = await StartTransfer(id, Wang, a);
        await Orders.AcceptTransfer(id, tid, a);
        await CanGet(a);
        await CanGet(Wang);
        await Denied(Chen);
        AssertInvariants();
    }

    // ---------- 34：列表里的 pendingTransfer ----------

    [Fact]
    public async Task PendingTransferAppearsInEveryList()
    {
        var a = NewEngineer("甲工");
        var id = await NewAccepted("列表转交客户", "王工");
        Assert.Null((await Orders.Query(new OrderQuery { Keyword = "列表转交客户" }, Sup)).Items.Single().PendingTransfer);
        var tid = await StartTransfer(id, Wang, a);

        async Task<int?> InQuery() => (await Orders.Query(new OrderQuery { Keyword = "列表转交客户" }, Sup)).Items.Single().PendingTransfer?.Id;
        async Task<int?> InMine() => (await Orders.Mine(Wang, "doing")).Items.Single(i => i.Id == id).PendingTransfer?.Id;
        async Task<int?> InCreated() => (await Orders.Created(Wm, "all")).Items.Single(i => i.Id == id).PendingTransfer?.Id;
        async Task<int?> InHome()
        {
            var h = await Home.Engineer(Wang);
            return h.Upcoming.Concat(new[] { (OrderListItem?)h.Next }).Single(i => i?.Id == id)!.PendingTransfer?.Id;
        }
        Assert.Equal(tid, await InQuery());
        Assert.Equal(tid, await InMine());
        Assert.Equal(tid, await InCreated());
        Assert.Equal(tid, await InHome());
        var toMe = Assert.Single((await Home.Engineer(a)).TransfersToMe);
        Assert.Equal(tid, toMe.PendingTransfer!.Id);
        Assert.Equal("王工 转交给 甲工，待 甲工 确认", toMe.PendingTransfer.Text);
        Assert.Equal("HTR-500", toMe.DeviceText);
        Assert.Equal(tid, (await Orders.Detail(id, a)).PendingTransfer!.Id);

        // 转交结束后三处都变回 null / 从 transfersToMe 消失
        await Orders.CancelTransfer(id, tid, Wang);
        Assert.Null(await InQuery());
        Assert.Null(await InMine());
        Assert.Null(await InCreated());
        Assert.Null(await InHome());
        Assert.Empty((await Home.Engineer(a)).TransfersToMe);

        // 多张单转给同一个人：按转交发起时间正序
        var id2 = await NewAccepted("列表转交客户二", "陈工", ServiceType.Remote);
        var t2 = await StartTransfer(id2, Chen, a);
        Tick(3);
        var t1 = await StartTransfer(id, Wang, a);
        Assert.Equal(new[] { t2, t1 }, (await Home.Engineer(a)).TransfersToMe.Select(i => i.PendingTransfer!.Id));
        AssertInvariants();
    }

    // ---------- 不变量的反向检查：种子与常规流程 ----------

    [Fact]
    public async Task InvariantsHoldAcrossRegularFlows()
    {
        AssertInvariants(); // 种子数据
        var id = await NewAccepted("常规流程客户", "王工", ServiceType.Remote);
        var tid = await StartTransfer(id, Wang, Liu);
        AssertInvariants();
        await Orders.AcceptTransfer(id, tid, Liu);
        AssertInvariants();
        await Orders.Start(id, Liu);
        await Orders.Complete(id, Done(Liu), Liu);
        await Orders.Reject(id, new ReasonRequest(), Sup);
        await Orders.Complete(id, Done(Liu), Liu);
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        await Orders.Close(id, Sup);
        AssertInvariants();
        // 闭环后：已退出的原负责人仍然只读，不能补传附件
        await Fails(40300, FormerDenied, async () => await Orders.AddAttachments(id, Att(await UpPdf(Wang, "x.pdf")), Wang));
        Assert.Single((await Orders.AddAttachments(id, Att(await UpPdf(Liu, "闭环后.pdf")), Liu)).Attachments);
    }

    // ---------- 并发：四个转交接口与改派 / 作废 / 提交完工 / 打卡排队 ----------

    private sealed class Stack : IDisposable
    {
        public readonly SqliteConnection Conn;
        public readonly AppDbContext Db;
        public readonly OrderService Orders;
        public readonly PunchService Punches;

        public Stack(string path, string root, FixedClock clock, INotifier notifier)
        {
            Conn = new SqliteConnection($"Data Source={path}");
            Conn.Open();
            Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Conn).Options);
            var files = new FileService(Db, clock, new Env { ContentRootPath = root }, new HttpContextAccessor(),
                Options.Create(new AppSettings { PublicBaseUrl = "http://test" }));
            var view = new OrderViewBuilder(Db, clock, files);
            Orders = new OrderService(Db, clock, view, files, notifier, NullLogger<OrderService>.Instance);
            Punches = new PunchService(Db, clock, view, files);
        }

        public CurrentUser User(string name)
        {
            var e = Db.Employees.AsNoTracking().Single(x => x.Name == name);
            return new CurrentUser(e.Id, e.Role);
        }

        public int NewFile(CurrentUser u, FileCategory cat, DateTime now)
        {
            var f = new StoredFile
            {
                Path = "t/" + Guid.NewGuid().ToString("N") + ".jpg", OriginalName = "t.jpg", Category = cat, MimeType = "image/jpeg",
                Size = 1, UploaderId = u.Id, CreatedAt = now
            };
            Db.Files.Add(f);
            Db.SaveChanges();
            return f.Id;
        }

        public void Dispose()
        {
            Db.Dispose();
            Conn.Dispose();
        }
    }

    private async Task WithTwoConnections(Func<Stack, Stack, Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aftersales-v13-race-{Guid.NewGuid():N}.db");
        try
        {
            using var a = new Stack(path, Root, Clock, Notifier);
            a.Db.Database.Migrate();
            await SeedData.RunAsync(a.Db, Path.Combine(Root, "uploads"), Clock.Now);
            a.Db.ChangeTracker.Clear();
            using var b = new Stack(path, Root, Clock, Notifier);
            await body(a, b);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>连接 a 持有写锁、把待确认的转交结束掉（未提交）；返回事务</summary>
    private static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> EndInOpenTransaction(Stack a, int orderId, int transferId, TransferStatus status)
    {
        var tx = await a.Db.Database.BeginTransactionAsync();
        var o = await a.Db.Orders.FirstAsync(x => x.Id == orderId);
        var t = await a.Db.OrderTransfers.FirstAsync(x => x.Id == transferId);
        TransferRules.End(o, t, status, null, t.CreatedAt, null);
        await a.Db.SaveChangesAsync();
        return tx;
    }

    [Fact]
    public async Task ConcurrentAcceptWaitsForCancel() => await WithTwoConnections(async (a, b) =>
    {
        var wang = a.User("王工");
        var liu = a.User("刘工");
        var id = (await a.Orders.Create(NewOrder("并发撤回与接手客户"), a.User("王敏"))).Id;
        await a.Orders.Claim(id, wang);
        var tid = (await a.Orders.Transfer(id, To(liu), wang)).PendingTransfer!.Id;
        await b.Orders.Detail(id, liu); // 预热连接 b
        b.Db.ChangeTracker.Clear();     // 真实请求每次都是新的 DbContext

        // a 正在撤回（已写入、未提交）；b 的确认接手必须排队，等 a 提交后得到「该转交已处理或已失效」
        var tx = await EndInOpenTransaction(a, id, tid, TransferStatus.Cancelled);
        var accept = Task.Run(() => b.Orders.AcceptTransfer(id, tid, liu));
        await Task.Delay(500);
        Assert.False(accept.IsCompleted);
        await tx.CommitAsync();
        var ex = await Assert.ThrowsAsync<BizException>(() => accept);
        Assert.Equal((40900, "该转交已处理或已失效"), (ex.Code, ex.Message));
        a.Db.ChangeTracker.Clear();
        var o = await a.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(wang.Id, o.AssigneeId);
        Assert.Null(o.PendingTransferToId);
        Assert.Empty(a.Db.OrderExits.Where(x => x.OrderId == id));
    });

    [Fact]
    public async Task ConcurrentCompleteAndReassignWaitForTransferWrites() => await WithTwoConnections(async (a, b) =>
    {
        var wang = a.User("王工");
        var liu = a.User("刘工");
        var sup = a.User("赵主管");
        var id = (await a.Orders.Create(NewOrder("并发完工客户", ServiceType.Remote), a.User("王敏"))).Id;
        await a.Orders.Claim(id, wang);
        await a.Orders.Start(id, wang);
        await b.Orders.Detail(id, wang);
        b.Db.ChangeTracker.Clear();
        var done = new CompleteRequest
        {
            FaultCause = "x", Solution = "y", RunStatus = RunStatus.Normal,
            ProcessPhotoIds = new() { b.NewFile(wang, FileCategory.ProcessPhoto, Clock.Now) },
            SignaturePhotoIds = new() { b.NewFile(wang, FileCategory.Signature, Clock.Now) }
        };

        // a 正在发起转交（已写入待确认的转交、未提交）；b 的提交完工进了写锁，要等 a 提交后被拦截
        var tx = await a.Db.Database.BeginTransactionAsync();
        var o = await a.Db.Orders.FirstAsync(x => x.Id == id);
        a.Db.OrderTransfers.Add(new OrderTransfer
        {
            OrderId = id, FromEngineerId = wang.Id, ToEngineerId = liu.Id, Reason = "并发", Status = TransferStatus.Pending, CreatedAt = Clock.Now
        });
        a.Db.Entry(o).Property(x => x.PendingTransferToId).CurrentValue = liu.Id;
        await a.Db.SaveChangesAsync();
        var complete = Task.Run(() => b.Orders.Complete(id, done, wang));
        await Task.Delay(500);
        Assert.False(complete.IsCompleted);
        await tx.CommitAsync();
        var ex = await Assert.ThrowsAsync<BizException>(() => complete);
        Assert.Equal((40900, "本单有待确认的转交，请先撤回转交后再提交完工"), (ex.Code, ex.Message));
        b.Db.ChangeTracker.Clear();
        a.Db.ChangeTracker.Clear();
        var tid = await a.Db.OrderTransfers.Where(t => t.OrderId == id).Select(t => t.Id).SingleAsync();

        // a 正在确认接手（已换负责人、未提交）；b 的改派进了写锁，等 a 提交后看到的是接手后的状态：
        // 待确认的转交已结束 → 不再写失效记录，被换掉的是新负责人刘工
        tx = await a.Db.Database.BeginTransactionAsync();
        o = await a.Db.Orders.FirstAsync(x => x.Id == id);
        var t0 = await a.Db.OrderTransfers.FirstAsync(x => x.Id == tid);
        TransferRules.End(o, t0, TransferStatus.Accepted, liu.Id, Clock.Now, null);
        o.AssigneeId = liu.Id;
        a.Db.OrderExits.Add(new OrderExit { OrderId = id, EngineerId = wang.Id, Via = ExitVia.Transfer, ExitedAt = Clock.Now });
        await a.Db.SaveChangesAsync();
        var chen = a.User("陈工");
        var assign = Task.Run(() => b.Orders.Assign(id, new AssignRequest { EngineerId = chen.Id }, sup));
        await Task.Delay(500);
        Assert.False(assign.IsCompleted);
        await tx.CommitAsync();
        var d = await assign;
        Assert.Equal("陈工", d.Assignee!.Name);
        Assert.Equal("赵主管 改派给 陈工（原 刘工）", d.Logs[^1].Text);
        Assert.DoesNotContain(d.Logs, l => l.Text.Contains("转交已失效"));
        a.Db.ChangeTracker.Clear();
        Assert.Equal(new[] { wang.Id, liu.Id }, a.Db.OrderExits.Where(x => x.OrderId == id).OrderBy(x => x.Id).Select(x => x.EngineerId).ToArray());
    });

    [Fact]
    public async Task ConcurrentPunchWaitsForAccept() => await WithTwoConnections(async (a, b) =>
    {
        var wang = a.User("王工");
        var liu = a.User("刘工");
        var id = (await a.Orders.Create(NewOrder("并发打卡客户"), a.User("王敏"))).Id;
        await a.Orders.Claim(id, wang);
        var tid = (await a.Orders.Transfer(id, To(liu), wang)).PendingTransfer!.Id;
        await b.Orders.Detail(id, wang);
        b.Db.ChangeTracker.Clear();
        var photo = b.NewFile(wang, FileCategory.PunchPhoto, Clock.Now);

        // a 正在确认接手（未提交）；原负责人的到场打卡必须排队，提交后他已不是参与人
        var tx = await a.Db.Database.BeginTransactionAsync();
        var o = await a.Db.Orders.FirstAsync(x => x.Id == id);
        var t = await a.Db.OrderTransfers.FirstAsync(x => x.Id == tid);
        TransferRules.End(o, t, TransferStatus.Accepted, liu.Id, Clock.Now, null);
        o.AssigneeId = liu.Id;
        a.Db.OrderExits.Add(new OrderExit { OrderId = id, EngineerId = wang.Id, Via = ExitVia.Transfer, ExitedAt = Clock.Now });
        await a.Db.SaveChangesAsync();
        var punch = Task.Run(() => b.Punches.Create(wang, new PunchRequest
        {
            OrderId = id, Kind = PunchKind.Arrive, Latitude = 31.30, Longitude = 120.72, Address = "x", PhotoFileId = photo
        }));
        await Task.Delay(500);
        Assert.False(punch.IsCompleted);
        await tx.CommitAsync();
        var ex = await Assert.ThrowsAsync<BizException>(() => punch);
        Assert.Equal((40300, "只有本单的负责人或协助人可以打卡"), (ex.Code, ex.Message));
        Assert.Empty(a.Db.Punches.Where(p => p.OrderId == id));
    });

    /// <summary>
    /// 审查补充：主管把接手人加为协助人，与对方「确认接手」同时到达。调整协助人不进写锁时，它按接手前的负责人通过校验、
    /// 等对方提交后再写入，结果同一个人既是负责人又是协助人。进写锁后必须排队，看到接手后的状态而被拦住。
    /// </summary>
    [Fact]
    public async Task ConcurrentSetAssistantsWaitsForAccept() => await WithTwoConnections(async (a, b) =>
    {
        var wang = a.User("王工");
        var liu = a.User("刘工");
        var sup = a.User("赵主管");
        var id = (await a.Orders.Create(NewOrder("并发协助人客户", ServiceType.Remote), a.User("王敏"))).Id;
        await a.Orders.Claim(id, wang);
        var tid = (await a.Orders.Transfer(id, To(liu), wang)).PendingTransfer!.Id;
        await b.Orders.Detail(id, sup);
        b.Db.ChangeTracker.Clear();

        // a 正在确认接手（刘工已成为负责人、未提交）；b 把刘工加为协助人必须排队
        var tx = await a.Db.Database.BeginTransactionAsync();
        var o = await a.Db.Orders.FirstAsync(x => x.Id == id);
        var t = await a.Db.OrderTransfers.FirstAsync(x => x.Id == tid);
        TransferRules.End(o, t, TransferStatus.Accepted, liu.Id, Clock.Now, null);
        o.AssigneeId = liu.Id;
        a.Db.OrderExits.Add(new OrderExit { OrderId = id, EngineerId = wang.Id, Via = ExitVia.Transfer, ExitedAt = Clock.Now });
        await a.Db.SaveChangesAsync();
        var set = Task.Run(() => b.Orders.SetAssistants(id, Set(liu), sup));
        await Task.Delay(500);
        Assert.False(set.IsCompleted);
        await tx.CommitAsync();
        var ex = await Assert.ThrowsAsync<BizException>(() => set);
        Assert.Equal((40000, "协助人不能是负责人"), (ex.Code, ex.Message));
        a.Db.ChangeTracker.Clear();
        Assert.Equal(liu.Id, (await a.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == id)).AssigneeId);
        Assert.Empty(a.Db.OrderAssistants.Where(x => x.OrderId == id));
    });

    /// <summary>
    /// 验收补充：原负责人的「开始处理」与对方「确认接手」同时到达。没有写锁时，Start 按旧负责人通过校验，
    /// 等接手提交后再写入，结果在他退出之后把工单改成「处理中」并落下一条他的记录。进写锁后必须排队并被拦住。
    /// </summary>
    [Fact]
    public async Task ConcurrentStartWaitsForAccept() => await WithTwoConnections(async (a, b) =>
    {
        var wang = a.User("王工");
        var liu = a.User("刘工");
        var id = (await a.Orders.Create(NewOrder("并发开始处理客户", ServiceType.Remote), a.User("王敏"))).Id;
        await a.Orders.Claim(id, wang);
        var tid = (await a.Orders.Transfer(id, To(liu), wang)).PendingTransfer!.Id;
        await b.Orders.Detail(id, wang);
        b.Db.ChangeTracker.Clear();

        // a 正在确认接手（刘工已成为负责人、未提交）；b 上王工点「开始处理」必须排队
        var tx = await a.Db.Database.BeginTransactionAsync();
        var o = await a.Db.Orders.FirstAsync(x => x.Id == id);
        var t = await a.Db.OrderTransfers.FirstAsync(x => x.Id == tid);
        TransferRules.End(o, t, TransferStatus.Accepted, liu.Id, Clock.Now, null);
        o.AssigneeId = liu.Id;
        a.Db.OrderExits.Add(new OrderExit { OrderId = id, EngineerId = wang.Id, Via = ExitVia.Transfer, ExitedAt = Clock.Now });
        await a.Db.SaveChangesAsync();
        var start = Task.Run(() => b.Orders.Start(id, wang));
        await Task.Delay(500);
        Assert.False(start.IsCompleted);
        await tx.CommitAsync();
        var ex = await Assert.ThrowsAsync<BizException>(() => start);
        Assert.Equal((40300, "只有负责工程师可以操作"), (ex.Code, ex.Message));
        a.Db.ChangeTracker.Clear();
        var row = await a.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal((liu.Id, OrderStatus.Accepted), (row.AssigneeId, row.Status));
    });
}
