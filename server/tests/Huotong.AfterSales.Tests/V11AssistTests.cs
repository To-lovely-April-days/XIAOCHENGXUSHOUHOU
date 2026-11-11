using System.Reflection;
using ClosedXML.Excel;
using Huotong.AfterSales.Api.Controllers;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.1 (4) 多人协作：负责人 + 协助人、协助人打卡、按人工时、申请与审批</summary>
public sealed class V11AssistTests : V11TestBase
{
    private AssistantsRequest Set(params string[] names) => new() { EngineerIds = names.Select(n => U(n).Id).ToList() };

    private int NewEngineer(string name, bool enabled = true)
    {
        var e = new Employee { Name = name, Phone = "137" + Random.Shared.NextInt64(10000000, 99999999), Role = Role.Engineer, Department = "售后部", Enabled = enabled, CreatedAt = Clock.Now };
        Db.Employees.Add(e);
        Db.SaveChanges();
        return e.Id;
    }

    [Fact]
    public async Task AssignWithAssistantsAndReplace()
    {
        var wang = U("王工");
        var id = await NewPending("多人指派客户");
        var before = Notifier.Sent.Count;
        var d = await Orders.Assign(id, new AssignRequest { EngineerId = wang.Id, AssistantIds = new() { U("刘工").Id, U("陈工").Id, U("刘工").Id } }, Sup);
        Assert.Equal(OrderStatus.Accepted, d.Status);
        Assert.Equal("王工", d.Assignee!.Name);
        Assert.Equal(new[] { "刘工", "陈工" }, d.Assistants.Select(a => a.Name));
        Assert.Equal(new[] { "赵主管 指派给 王工 → 待启动", "赵主管 添加协助人 刘工、陈工" }, d.Logs.TakeLast(2).Select(l => l.Text));
        Assert.Equal(new[] { LogKind.Status, LogKind.Assist }, d.Logs.TakeLast(2).Select(l => l.Kind));
        Assert.Equal(new[] { (ParticipantRole.Lead, "王工"), (ParticipantRole.Assistant, "刘工"), (ParticipantRole.Assistant, "陈工") },
            d.Participants.Select(p => (p.Role, p.User.Name)));
        Assert.Equal(before + 2, Notifier.Sent.Count);
        Assert.Equal("工单指派提醒", Notifier.Sent[^2].Title);
        Assert.Equal("工单协助提醒", Notifier.Last.Title);
        Assert.Equal($"赵主管 安排你协助 **多人指派客户**（{d.OrderNo}），负责人 王工。\n\nHTR-500 · 不加热", Notifier.Last.Text);
        Assert.Equal(new[] { Emp("刘工").Phone, Emp("陈工").Phone }, Notifier.Last.Mobiles);

        // assistants 接口 = 最终全集
        d = await Orders.SetAssistants(id, Set("陈工"), Sup);
        Assert.Equal(new[] { "陈工" }, d.Assistants.Select(a => a.Name));
        Assert.Equal("赵主管 移除协助人 刘工", d.Logs.Last().Text);
        var logs = d.Logs.Count;
        Assert.Equal(logs, (await Orders.SetAssistants(id, Set("陈工"), Sup)).Logs.Count); // 无变化不写记录
        d = await Orders.SetAssistants(id, Set("刘工"), Sup);
        Assert.Equal(new[] { "赵主管 添加协助人 刘工", "赵主管 移除协助人 陈工" }, d.Logs.TakeLast(2).Select(l => l.Text)); // 先添加后移除

        // 校验
        await Fails(40000, "请选择协助人", () => Orders.SetAssistants(id, new AssistantsRequest(), Sup));
        await Fails(40000, "协助人不能是负责人", () => Orders.SetAssistants(id, Set("王工"), Sup));
        await Fails(40000, "所选协助人不存在或已停用", () => Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { 99999 } }, Sup));
        await Fails(40000, "所选协助人不存在或已停用", () => Orders.SetAssistants(id, Set("王敏"), Sup)); // 非工程师
        var disabled = NewEngineer("停用工", enabled: false);
        await Fails(40000, "所选协助人不存在或已停用", () => Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { disabled } }, Sup));
        var six = Enumerable.Range(1, 6).Select(i => NewEngineer("新工" + i)).ToList();
        await Fails(40000, "协助人最多 5 人", () => Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = six }, Sup));
        Assert.Equal(5, (await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = six.Take(5).ToList() }, Sup)).Assistants.Count);
        Assert.Equal(40300, (await Biz(() => Orders.SetAssistants(id, Set("刘工"), wang))).Code);
        await Fails(40900, "工单当前为「已闭环」，不能调整协助人", () => Orders.SetAssistants(OrderId("宁波某化工企业"), Set("刘工"), Sup));
        await Fails(40900, "工单当前为「待认领」，不能调整协助人", async () => await Orders.SetAssistants(await NewPending("未指派"), Set("刘工"), Sup));

        // assign 的 assistantIds：省略 = 不调整；[] = 清空；校验失败时什么都不改
        d = await Orders.Assign(id, new AssignRequest { EngineerId = U("陈工").Id }, Sup);
        Assert.Equal(5, d.Assistants.Count);
        await Fails(40000, "协助人不能是负责人",
            () => Orders.Assign(id, new AssignRequest { EngineerId = wang.Id, AssistantIds = new() { wang.Id } }, Sup));
        Db.ChangeTracker.Clear();
        Assert.Equal("陈工", (await Orders.Detail(id, Sup)).Assignee!.Name);
        d = await Orders.Assign(id, new AssignRequest { EngineerId = wang.Id, AssistantIds = new() }, Sup);
        Assert.Empty(d.Assistants);
        Assert.StartsWith("赵主管 移除协助人 新工1、", d.Logs.Last().Text);
    }

    [Fact]
    public async Task ReassignToAssistantPromotes()
    {
        var id = await NewAccepted("转正客户");
        await Orders.SetAssistants(id, Set("刘工", "陈工"), Sup);
        var d = await Orders.Assign(id, new AssignRequest { EngineerId = U("刘工").Id }, Sup);
        Assert.Equal("刘工", d.Assignee!.Name);
        Assert.Equal(new[] { "陈工" }, d.Assistants.Select(a => a.Name));
        Assert.Equal("赵主管 改派给 刘工（原 王工）", d.Logs.Last().Text); // 转正不算移除，不写「移除协助人」
        Assert.DoesNotContain(d.Logs, l => l.Text.Contains("移除协助人"));
        // 原负责人不会自动变成协助人；v1.3：他有退出记录（已改派），即使没打过卡也出现在参与人列表里，并且可以只读查看
        Assert.Equal(new[] { (ParticipantRole.Lead, "刘工"), (ParticipantRole.Assistant, "陈工"), (ParticipantRole.Former, "王工") },
            d.Participants.Select(p => (p.Role, p.User.Name)));
        Assert.Equal("已改派", d.Participants.Last().ExitText);
        var former = await Orders.Detail(id, U("王工"));
        Assert.Equal(ParticipantRole.Former, former.MyRole);
        Assert.Equal("已改派", former.MyExitText);
        Assert.Empty(former.Actions);
        Assert.Equal("你已退出本单（已改派），只能查看；你的打卡和工时记录已保留。", former.ReadOnlyText);
        Assert.Equal(ParticipantRole.Lead, (await Orders.Detail(id, U("刘工"))).MyRole);

        // 带 assistantIds 改派：新负责人从协助人转正，同时调整其余协助人
        d = await Orders.Assign(id, new AssignRequest { EngineerId = U("陈工").Id, AssistantIds = new() { U("王工").Id } }, Sup);
        Assert.Equal("陈工", d.Assignee!.Name);
        Assert.Equal(new[] { "王工" }, d.Assistants.Select(a => a.Name));
        Assert.Equal(new[] { "赵主管 改派给 陈工（原 刘工）", "赵主管 添加协助人 王工" }, d.Logs.TakeLast(2).Select(l => l.Text));
        // v1.3：王工被加为协助人 → 退出记录删除；刘工被换掉 → 成为已退出
        var asst = await Orders.Detail(id, U("王工"));
        Assert.Equal(ParticipantRole.Assistant, asst.MyRole);
        Assert.Null(asst.MyExitText);
        Assert.Null(asst.ReadOnlyText);
        Assert.Equal(new[] { (ParticipantRole.Lead, "陈工"), (ParticipantRole.Assistant, "王工"), (ParticipantRole.Former, "刘工") },
            d.Participants.Select(p => (p.Role, p.User.Name)));
        Assert.Equal(ParticipantRole.Former, (await Orders.Detail(id, U("刘工"))).MyRole);
        Assert.Equal(new[] { U("刘工").Id }, Db.OrderExits.AsNoTracking().Where(x => x.OrderId == id).Select(x => x.EngineerId).ToArray());
    }

    [Fact]
    public async Task AssistantSeesAndPunches()
    {
        var wang = U("王工");
        var liu = U("刘工");
        var chen = U("陈工");
        var id = await NewAccepted("协助打卡客户", "刘工");
        var active = (await Home.Me(wang)).ActiveOrderCount;
        await Orders.SetAssistants(id, Set("王工"), Sup);

        var d = await Orders.Detail(id, wang);
        Assert.Equal(ParticipantRole.Assistant, d.MyRole);
        Assert.Equal(0, d.MyMinutes);
        Assert.Equal(new[] { "checkin" }, d.Actions);
        var mine = (await Orders.Mine(wang, "all")).Items.Single(i => i.Id == id);
        Assert.Equal(ParticipantRole.Assistant, mine.MyRole);
        Assert.Equal("刘工", mine.Assignee!.Name);
        var home = await Home.Engineer(wang);
        Assert.Contains(home.Upcoming.Append(home.Next).Append(home.Current), i => i?.Id == id);
        Assert.Equal(active + 1, (await Home.Me(wang)).ActiveOrderCount);
        Assert.Equal(ParticipantRole.Lead, (await Orders.Detail(id, liu)).MyRole);
        Assert.Null((await Orders.Detail(id, Sup)).MyRole);
        Assert.Null((await Orders.Detail(id, Sup)).MyMinutes);

        // 非参与工程师：不能看、不能打卡
        Assert.Equal(40300, (await Biz(() => Orders.Detail(id, chen))).Code);
        await Fails(40300, "只有本单的负责人或协助人可以打卡", () => Punches.Context(chen, id));
        await Fails(40300, "只有本单的负责人或协助人可以打卡", () => Arrive(chen, id));
        // 协助人只能看和打卡
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Appointment(id, new AppointmentRequest { AppointmentTime = Clock.Now }, wang));
        await Fails(40300, "只有负责工程师可以申请暂停", () => Orders.RequestHold(id, HoldBody("WaitParts"), wang));

        var ctx = await Punches.Context(wang, id);
        Assert.Equal(ParticipantRole.Assistant, ctx!.MyRole);
        Assert.Equal(PunchKind.Arrive, ctx.NextKind);
        Assert.Null(ctx.BlockedReason);
        Assert.Equal(ParticipantRole.Lead, (await Punches.Context(liu, id))!.MyRole);

        // 协助人首次到场也会使 待启动 → 处理中
        var r = await Arrive(wang, id);
        Assert.Equal(OrderStatus.InProgress, r.Order.Status);
        Assert.Equal(ParticipantRole.Assistant, r.Order.MyRole);
        Assert.Equal(Clock.Now, r.Order.MyOnSiteSince);
        d = await Orders.Detail(id, wang);
        Assert.Equal("王工 首次到场 → 处理中", d.Logs.Last().Text);
        Assert.Equal(new[] { "checkin" }, d.Actions);
        // 不带 orderId 的自动选单：本人参与的现场单（此时为有未离场到场的这一单）
        var auto = await Punches.Context(wang, null);
        Assert.Equal(id, auto!.OrderId);
        Assert.Equal(PunchKind.Leave, auto.NextKind);
        Assert.Equal("working", (await Home.Engineer(wang)).State);
        Assert.Equal(id, (await Home.Engineer(wang)).Current!.Id);

        // 同一工程师同一时间只能有一个未离场到场：按本人计算
        var other = await NewAccepted("协助第二单", "刘工");
        await Orders.SetAssistants(other, Set("王工"), Sup);
        Assert.Equal("请先在〈协助打卡客户〉离场打卡", (await Punches.Context(wang, other))!.BlockedReason);
        await Fails(40900, "请先在〈协助打卡客户〉离场打卡", () => Arrive(wang, other));
        // 负责人不受协助人的到场影响
        Assert.Null((await Punches.Context(liu, other))!.BlockedReason);

        Tick(45);
        r = await Leave(wang, id);
        Assert.Equal(45, r.Punch.PairMinutes);
        Assert.Equal(45, r.Order.MyMinutes);
        Assert.Null(r.Order.MyOnSiteSince);
    }

    [Fact]
    public async Task CompleteBlockedWhileAssistantOnSite()
    {
        var wang = U("王工");
        var liu = U("刘工");
        var id = await NewAccepted("完工拦截客户", "刘工");
        await Orders.SetAssistants(id, Set("王工"), Sup);
        await Arrive(liu, id);
        Tick(5);
        await Arrive(wang, id);

        // 先判本人
        await Fails(40900, "请先离场打卡", () => Orders.Complete(id, Done(liu), liu));
        Tick(30);
        await Leave(liu, id);
        // 再判他人
        await Fails(40900, "王工还在现场计时，请其先离场打卡后再提交完工", () => Orders.Complete(id, Done(liu), liu));
        // 协助人不能提交完工
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Complete(id, Done(wang), wang));
        // 改派 / 移除协助人 的现场计时拦截
        await Fails(40900, "王工正在现场计时，请先离场打卡后再移除", () => Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() }, Sup));

        Tick(10);
        await Leave(wang, id);
        var d = await Orders.Complete(id, Done(liu), liu);
        Assert.Equal(OrderStatus.Submitted, d.Status);
        Assert.Equal(35 + 40, d.TotalMinutes);
        Assert.Empty((await Orders.Detail(id, wang)).Actions);
    }

    [Fact]
    public async Task PerPersonHours()
    {
        var wang = U("王工");
        var liu = U("刘工");
        var id = await NewAccepted("按人工时客户", "刘工");
        await Orders.SetAssistants(id, Set("王工"), Sup);
        await Arrive(liu, id);      // 13:42
        Tick(10);
        await Arrive(wang, id);     // 13:52
        Tick(60);
        await Leave(wang, id);      // 14:52 → 60
        Tick(50);
        await Leave(liu, id);       // 15:42 → 120

        var d = await Orders.Detail(id, Sup);
        Assert.Equal(180, d.TotalMinutes);
        Assert.Equal(new[] { ("刘工", ParticipantRole.Lead, 120), ("王工", ParticipantRole.Assistant, 60) },
            d.Participants.Select(p => (p.User.Name, p.Role, p.TotalMinutes)));
        Assert.All(d.Participants, p => { Assert.Null(p.OnSiteSince); Assert.False(p.MissingPunch); });
        Assert.Null(d.MyMinutes);
        Assert.Equal(60, (await Orders.Detail(id, wang)).MyMinutes);
        Assert.Equal(120, (await Orders.Detail(id, liu)).MyMinutes);
        Assert.Equal(60, (await Orders.Mine(wang, "all")).Items.Single(i => i.Id == id).MyMinutes);
        Assert.Equal(180, (await Orders.Mine(wang, "all")).Items.Single(i => i.Id == id).TotalMinutes);

        // 工时统计：协助人的工时计入协助人本人
        var h = await Reports.Hours(2026, 11);
        Assert.Equal(60, h.Engineers.Single(e => e.Engineer.Name == "王工").TotalMinutes);
        Assert.Equal(495 + 120, h.Engineers.Single(e => e.Engineer.Name == "刘工").TotalMinutes);
        var month = await Hours.Month(wang.Id, 2026, 11);
        Assert.Equal(60, month.TotalMinutes);
        Assert.Equal(ParticipantRole.Assistant, month.Days.SelectMany(x => x.Entries).Single(e => e.OrderId == id).Role);
        Assert.Equal(ParticipantRole.Assistant, (await Hours.Today(wang.Id)).Entries.Single().Role);
        Assert.Equal(ParticipantRole.Lead, (await Hours.Today(liu.Id)).Entries.Single().Role);
        Assert.Equal(60, (await Hours.Today(wang.Id)).ClosedMinutes);

        // 工时导出第 10 列：本单角色
        using (var wb = new XLWorkbook(new MemoryStream(await Admin.ExportHours(2026, 11))))
        {
            var ws = wb.Worksheet(1);
            Assert.Equal("本单角色", ws.Cell(1, 10).GetString());
            Assert.Equal("补录原因", ws.Cell(1, 9).GetString());
            var rows = ws.RowsUsed().Where(r => r.Cell(4).GetString() == "按人工时客户").ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal("协助人", rows.Single(r => r.Cell(1).GetString() == "王工").Cell(10).GetString());
            Assert.Equal(1.0, rows.Single(r => r.Cell(1).GetString() == "王工").Cell(7).GetDouble());
            Assert.Equal("负责人", rows.Single(r => r.Cell(1).GetString() == "刘工").Cell(10).GetString());
            Assert.All(ws.RowsUsed().Skip(1).Where(r => r.Cell(4).GetString() == "合肥某研究所"), r => Assert.Equal("负责人", r.Cell(10).GetString()));
        }
        // 台账：协助人、人员工时
        using (var wb = new XLWorkbook(new MemoryStream(await Admin.ExportOrders(null, null, null))))
        {
            var row = wb.Worksheet(1).RowsUsed().Single(r => r.Cell(3).GetString() == "按人工时客户");
            Assert.Equal("刘工", row.Cell(12).GetString());
            Assert.Equal(3.0, row.Cell(18).GetDouble());
            Assert.Equal("王工", row.Cell(23).GetString());
            Assert.Equal("刘工 2.0h；王工 1.0h", row.Cell(24).GetString());
        }

        // 移除后：有打卡的人显示为「已退出」，工时仍归本人
        d = await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() }, Sup);
        Assert.Equal(new[] { ("刘工", ParticipantRole.Lead, 120), ("王工", ParticipantRole.Former, 60) },
            d.Participants.Select(p => (p.User.Name, p.Role, p.TotalMinutes)));
        Assert.Equal(180, d.TotalMinutes);
        Assert.Equal(ParticipantRole.Former, (await Hours.Today(wang.Id)).Entries.Single().Role);
        using (var wb = new XLWorkbook(new MemoryStream(await Admin.ExportHours(2026, 11))))
            Assert.Equal("已退出", wb.Worksheet(1).RowsUsed().Single(r => r.Cell(4).GetString() == "按人工时客户" && r.Cell(1).GetString() == "王工").Cell(10).GetString());
    }

    [Fact]
    public async Task AssistantMissingPunchBlocksClose()
    {
        var wang = U("王工");
        var liu = U("刘工");
        var id = await NewAccepted("协助缺卡客户", "刘工");
        await Orders.SetAssistants(id, Set("王工"), Sup);
        await Arrive(liu, id);
        await Arrive(wang, id);
        Tick(60);
        await Leave(liu, id);       // 王工忘记离场

        Clock.Now = new DateTime(2026, 11, 7, 10, 0, 0);
        // 昨天的未离场不算「现场计时中」，可以提交完工
        await Orders.Complete(id, Done(liu), liu);
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);

        var d = await Orders.Detail(id, Sup);
        Assert.Contains(IssueType.MissingPunch, d.Issues);
        var issue = d.IssueDetails.Single(i => i.Type == IssueType.MissingPunch);
        Assert.Equal("王工", issue.Engineer!.Name);
        Assert.Equal("王工 11月6日 13:42 到场后未打离场卡，当天未计工时。补录后系统重新计算。", issue.Text);
        Assert.True(d.Participants.Single(p => p.User.Name == "王工").MissingPunch);
        Assert.False(d.Participants.Single(p => p.User.Name == "刘工").MissingPunch);
        Assert.Equal("请先补录缺卡", d.CloseBlockedReason);
        Assert.Equal(new[] { "reject", "supplement", "setPrice" }, d.Actions);
        await Fails(40900, "请先补录缺卡", () => Orders.Close(id, Sup));
        Assert.Contains((await Home.Supervisor(Sup)).Todos, t => t.Type == "MissingPunch" && t.OrderId == id && t.Title == "王工 · 缺离场卡");
        Assert.Contains((await Orders.Query(new OrderQuery { Issue = "MissingPunch" }, Sup)).Items, i => i.Id == id);

        d = await Admin.Supplement(new SupplementRequest
        {
            OrderId = id, ArrivePunchId = issue.PunchId, Kind = PunchKind.Leave, Time = new DateTime(2026, 11, 6, 18, 20, 0), Reason = "忘记打卡"
        }, Sup);
        Assert.Equal("赵主管 补录离场 18:20（王工）", d.Logs.Last().Text);
        Assert.DoesNotContain(IssueType.MissingPunch, d.Issues);
        Assert.Null(d.CloseBlockedReason);
        Assert.Equal(277, d.Participants.Single(p => p.User.Name == "王工").TotalMinutes);
        Assert.Equal(OrderStatus.Closed, (await Orders.Close(id, Sup)).Status);
    }

    [Fact]
    public async Task AssistantRequestFlow()
    {
        var wang = U("王工");
        var liu = U("刘工");
        var id = await NewAccepted("申请协助客户", "刘工");
        var before = Notifier.Sent.Count;

        var d = await Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = wang.Id, Note = "需要两人抬设备" }, liu);
        var req = d.Requests.Single();
        Assert.Equal(OrderRequestType.AddAssistant, req.Type);
        Assert.Equal("添加协助人 王工：需要两人抬设备", req.Text);
        Assert.Equal("王工", req.Engineer!.Name);
        Assert.Null(req.ReasonType);
        Assert.True(req.CanCancel);
        Assert.Empty(d.Assistants);
        Assert.Equal("刘工 申请添加协助人 王工（需要两人抬设备）", d.Logs.Last().Text);
        Assert.Equal(LogKind.Assist, d.Logs.Last().Kind);
        Assert.Contains("requestAssistant", d.Actions);
        Assert.Equal(before + 1, Notifier.Sent.Count);
        Assert.Equal("协助人申请", Notifier.Last.Title);
        Assert.Equal($"刘工 申请为 **申请协助客户**（{d.OrderNo}）添加协助人 王工：需要两人抬设备，请审批。", Notifier.Last.Text);
        var todo = (await Home.Supervisor(Sup)).Todos.Single(t => t.Type == "AssistantRequest" && t.OrderId == id);
        Assert.Equal(req.Id, todo.RequestId);
        Assert.Equal("刘工 申请添加协助人 王工：需要两人抬设备", todo.Subtitle);
        // 目标工程师在审批前不是参与人
        Assert.Equal(40300, (await Biz(() => Orders.Detail(id, wang))).Code);

        await Fails(40900, "已有针对王工的待审批申请", () => Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = wang.Id }, liu));
        await Fails(40000, "协助人不能是负责人", () => Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = liu.Id }, liu));
        await Fails(40000, "请选择协助工程师", () => Orders.RequestAssistant(id, new AssistantRequestRequest(), liu));
        await Fails(40000, "所选协助人不存在或已停用", () => Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = U("王敏").Id }, liu));
        await Fails(40000, "说明不能超过 200 个字",
            () => Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = U("陈工").Id, Note = new string('长', 201) }, liu));
        await Fails(40300, "只有负责工程师可以申请协助人", () => Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = liu.Id }, wang));

        // 驳回：不变
        d = await Orders.RejectRequest(id, req.Id, new ReplyRequest(), Sup);
        Assert.Empty(d.Assistants);
        Assert.Equal("赵主管 驳回添加协助人 王工", d.Logs.Last().Text);
        Assert.Equal($"**申请协助客户**（{d.OrderNo}）的协助人申请被驳回", Notifier.Last.Text);

        // 再申请 → 撤回
        d = await Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = wang.Id }, liu);
        Assert.Equal("添加协助人 王工", d.Requests.Last().Text);
        d = await Orders.CancelRequest(id, d.Requests.Last().Id, liu);
        Assert.Equal("刘工 撤回协助人申请（王工）", d.Logs.Last().Text);

        // 再申请 → 同意
        d = await Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = wang.Id }, liu);
        Tick();
        d = await Orders.ApproveRequest(id, d.Requests.Last().Id, Sup);
        Assert.Equal(new[] { "王工" }, d.Assistants.Select(a => a.Name));
        Assert.Equal(RequestStatus.Approved, d.Requests.Last().Status);
        Assert.Equal("赵主管 同意添加协助人 王工", d.Logs.Last().Text);
        Assert.Equal("工单协助提醒", Notifier.Last.Title);
        Assert.Equal($"赵主管 同意 王工 协助 **申请协助客户**（{d.OrderNo}），负责人 刘工。", Notifier.Last.Text);
        Assert.Equal(new[] { Emp("刘工").Phone, Emp("王工").Phone }, Notifier.Last.Mobiles);
        Assert.Equal(ParticipantRole.Assistant, (await Orders.Detail(id, wang)).MyRole);
        Assert.Equal(Emp("赵主管").Id, Db.OrderAssistants.AsNoTracking().Single(a => a.OrderId == id).AddedById);
        await Fails(40900, "王工已是本单协助人", () => Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = wang.Id }, liu));

        // 目标停用后同意 → 40000
        var temp = new Employee { Name = "临时工", Phone = "13700001111", Role = Role.Engineer, Department = "售后部", Enabled = true, CreatedAt = Clock.Now };
        Db.Employees.Add(temp);
        Db.SaveChanges();
        d = await Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = temp.Id }, liu);
        temp.Enabled = false;
        Db.SaveChanges();
        await Fails(40000, "所选协助人不存在或已停用", () => Orders.ApproveRequest(id, d.Requests.Last().Id, Sup));

        // 移除在现场的协助人 → 40900
        await Arrive(wang, id);
        await Fails(40900, "王工正在现场计时，请先离场打卡后再移除", () => Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() }, Sup));
        Tick(5);
        await Leave(wang, id);
        Assert.Empty((await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() }, Sup)).Assistants);
    }

    [Fact]
    public async Task EngineersListOpenToEngineerAndCountsAssist()
    {
        // 种子：王工负责 常熟（返厂，处理中），协助 常州（暂停搁置）
        var list = await Home.Engineers();
        var wang = list.Single(e => e.User.Name == "王工");
        Assert.Equal(1, wang.AssistCount);
        Assert.Equal(1, wang.DoingCount);
        Assert.Equal("处理中 1 单、协助 1 单", wang.LoadText);
        Assert.Equal(0, list.Single(e => e.User.Name == "李工").AssistCount);
        Assert.DoesNotContain("协助", list.Single(e => e.User.Name == "李工").LoadText);

        Assert.Equal("协助 1 单 · 今日未出差", HomeService.LoadText(0, 0, 0, null, 1));
        Assert.Equal("处理中 1 单、待验收 1 单、协助 2 单 · 南京", HomeService.LoadText(1, 0, 1, "南京", 2));
        Assert.Equal("今日空闲 · 今日未出差", HomeService.LoadText(0, 0, 0, null)); // 原 4 参数调用结果不变

        // 已闭环 / 待验收 的协助不计入
        var id = await NewAccepted("协助计数客户", "刘工", ServiceType.Remote);
        await Orders.SetAssistants(id, Set("李工"), Sup);
        Assert.Equal(1, (await Home.Engineers()).Single(e => e.User.Name == "李工").AssistCount);
        await Orders.Start(id, U("刘工"));
        await Orders.Complete(id, Done(U("刘工"), signature: false), U("刘工"));
        Assert.Equal(0, (await Home.Engineers()).Single(e => e.User.Name == "李工").AssistCount);

        // GET /api/engineers 对工程师开放
        var roles = typeof(HomeController).GetMethod(nameof(HomeController.Engineers))!.GetCustomAttribute<AuthorizeAttribute>()!.Roles!;
        Assert.Equal(new[] { "Engineer", "Supervisor" }, roles.Split(',').Select(r => r.Trim()).OrderBy(r => r));
    }
}
