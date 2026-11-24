using Huotong.AfterSales.Api.Controllers;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Huotong.AfterSales.Tests;

/// <summary>
/// v1.4 订阅消息：每个业务事件的收件人与内容（MockMode，只记录不联网）、不发给自己、未绑定微信不发、未启用不发、
/// 消息里没有价格、发送失败不影响主流程、提交之后才发。
/// </summary>
public sealed class V14SubscribeEventTests : V13TestBase
{
    private const string Tpl = "TPL-ORDER-REMIND";
    private const string Now = "2026年11月06日 13:42";

    public V14SubscribeEventTests()
    {
        WxOptions.Subscribe.TemplateId = Tpl;
        WxOptions.Subscribe.Fields = new Dictionary<string, string>
        {
            ["thing1"] = "Title", ["character_string2"] = "OrderNo", ["thing3"] = "Customer", ["thing4"] = "Content",
            ["time5"] = "Time", ["name6"] = "Operator", ["phrase7"] = "Status", ["thing8"] = "Device"
        };
        // 种子里的 7 个人全部绑定微信
        foreach (var e in Db.Employees.AsNoTracking().ToList()) SetOpenId(e.Name, "oid-" + e.Id);
    }

    // ---------- 工具 ----------

    private void SetOpenId(string name, string? openId) =>
        Db.Employees.Where(e => e.Name == name).ExecuteUpdate(s => s.SetProperty(e => e.OpenId, openId));

    private CurrentUser AddEmployee(string name, Role role, string? openId, bool enabled = true)
    {
        var e = new Employee
        {
            Name = name, Phone = "1380000" + (1000 + Db.Employees.Count()), Role = role, Department = "售后部", Enabled = enabled,
            OpenId = openId, CreatedAt = Clock.Now
        };
        Db.Employees.Add(e);
        Db.SaveChanges();
        Db.Entry(e).State = EntityState.Detached;
        return new CurrentUser(e.Id, e.Role);
    }

    private CurrentUser Wang => U("王工");
    private CurrentUser Liu => U("刘工");
    private CurrentUser Chen => U("陈工");
    private CurrentUser Li => U("李工");
    private CurrentUser Wm => U("王敏");

    /// <summary>取走到目前为止记录的消息</summary>
    private List<SubscribeMessage> Take()
    {
        var list = Subscribe.MockSent.ToList();
        Subscribe.ClearMockSent();
        return list;
    }

    private string Who(SubscribeMessage m) => Db.Employees.AsNoTracking().Single(e => e.OpenId == m.OpenId).Name;
    private static string Title(SubscribeMessage m) => m.Data["thing1"];
    private static string Content(SubscribeMessage m) => m.Data["thing4"];

    /// <summary>断言这一步恰好发了这些「收件人：事项」（不看顺序），并返回消息</summary>
    private List<SubscribeMessage> Expect(params string[] whoAndTitle)
    {
        var sent = Take();
        Assert.Equal(whoAndTitle.OrderBy(x => x, StringComparer.Ordinal), sent.Select(m => $"{Who(m)}：{Title(m)}").OrderBy(x => x, StringComparer.Ordinal));
        return sent;
    }

    private void ExpectNone() => Assert.Empty(Take());

    private async Task<int> RemoteInProgress(string customer, string lead = "王工", string creator = "王敏")
    {
        var id = await NewAccepted(customer, lead, ServiceType.Remote, creator);
        await Orders.Start(id, U(lead));
        return id;
    }

    private async Task<int> RemoteSubmitted(string customer, string lead = "王工", string creator = "王敏")
    {
        var id = await RemoteInProgress(customer, lead, creator);
        await Orders.Complete(id, Done(U(lead)), U(lead));
        return id;
    }

    private static AssistantsRequest Set(params CurrentUser[] users) => new() { EngineerIds = users.Select(u => u.Id).ToList() };

    // ---------- 接口：GET /api/me/subscribe ----------

    [Fact]
    public void 配置接口_已启用返回模板_未启用返回空()
    {
        var me = new MeController(Home, null!, Subscribe);
        Assert.Equal("{\"enabled\":true,\"templateIds\":[\"TPL-ORDER-REMIND\"]}", Json(me.Subscribe().GetAwaiter().GetResult().Data!));
        Assert.Equal(0, me.Subscribe().GetAwaiter().GetResult().Code);

        WxOptions.Subscribe.TemplateId = "";
        Assert.Equal("{\"enabled\":false,\"templateIds\":[]}", Json(me.Subscribe().GetAwaiter().GetResult().Data!));

        WxOptions.Subscribe.TemplateId = Tpl;
        WxOptions.Subscribe.Fields = new();
        Assert.Equal("{\"enabled\":false,\"templateIds\":[]}", Json(me.Subscribe().GetAwaiter().GetResult().Data!));
    }

    // ---------- 给主管 ----------

    [Fact]
    public async Task 非主管建单_提醒全部启用且已绑定的主管_消息形状完整()
    {
        AddEmployee("钱主管", Role.Supervisor, "oid-qian");
        AddEmployee("孙主管", Role.Supervisor, null);                       // 未绑定微信
        AddEmployee("周主管", Role.Supervisor, "oid-zhou", enabled: false);  // 已停用

        var id = await NewPending("订阅建单客户");
        var sent = Expect("赵主管：新工单待指派", "钱主管：新工单待指派");

        var m = sent[0];
        var o = OrderRow(id);
        Assert.Equal(Tpl, m.TemplateId);
        Assert.Equal($"pages/order-detail/index?id={id}", m.Page);
        Assert.Equal("trial", m.MiniProgramState);
        Assert.Equal("zh_CN", m.Lang);
        Assert.Equal(8, m.Data.Count);
        Assert.Equal(o.OrderNo, m.Data["character_string2"]);
        Assert.Equal("订阅建单客户", m.Data["thing3"]);
        Assert.Equal("王敏新建现场工单，待指派", Content(m));
        Assert.Equal(Now, m.Data["time5"]);
        Assert.Equal("王敏", m.Data["name6"]);
        Assert.Equal("待认领", m.Data["phrase7"]);
        Assert.Equal(SubscribeFields.Clean("thing8", o.DeviceText), m.Data["thing8"]);
    }

    [Fact]
    public async Task 紧急工单的说明带紧急前缀_工程师建单同样提醒主管()
    {
        var r = NewOrder("紧急订阅客户", ServiceType.Remote);
        r.Urgency = Urgency.Urgent;
        await Orders.Create(r, Li);
        var m = Assert.Single(Expect("赵主管：新工单待指派"));
        Assert.Equal("【紧急】李工新建远程工单，待指派", Content(m));
        Assert.Equal("李工", m.Data["name6"]);
    }

    [Fact]
    public async Task 主管自己建单_不提醒任何主管()
    {
        AddEmployee("钱主管", Role.Supervisor, "oid-qian");
        await Orders.Create(NewOrder("主管建单客户"), Sup);
        ExpectNone();
    }

    [Fact]
    public async Task 认领不提醒任何人_提交完工提醒主管()
    {
        var id = await NewPending("完工订阅客户", ServiceType.Remote);
        Take();
        await Orders.Claim(id, Wang);
        ExpectNone();
        await Orders.Start(id, Wang);
        ExpectNone();
        await Orders.Complete(id, Done(Wang), Wang);
        var m = Assert.Single(Expect("赵主管：工单待验收"));
        Assert.Equal("王工提交了完工，请验收", Content(m));
        Assert.Equal("待验收", m.Data["phrase7"]);
    }

    [Fact]
    public async Task 暂停申请与协助申请_提醒主管审批()
    {
        var id = await NewAccepted("申请订阅客户");
        Take();
        await Orders.RequestHold(id, HoldBody("WaitParts", "等加热管到货"), Wang);
        var m = Assert.Single(Expect("赵主管：有申请待审批"));
        Assert.StartsWith("王工申请暂停：等备件", Content(m));

        await Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = Liu.Id, Note = "需要电气支持" }, Wang);
        m = Assert.Single(Expect("赵主管：有申请待审批"));
        Assert.Equal("王工申请添加协助人刘工", Content(m));
    }

    // ---------- 给工程师：指派 / 改派 / 协助人 ----------

    [Fact]
    public async Task 主管指派_提醒新负责人_带协助人时各提醒一条()
    {
        var id = await NewPending("指派订阅客户");
        Take();
        await Orders.Assign(id, new AssignRequest { EngineerId = Wang.Id, AssistantIds = new() { Liu.Id, Chen.Id } }, Sup);
        var sent = Expect("王工：新工单指派给你", "刘工：你被加为协助人", "陈工：你被加为协助人");
        var lead = sent.Single(m => Who(m) == "王工");
        Assert.Equal("赵主管把工单指派给你，请及时处理", Content(lead));
        Assert.Equal("赵主管", lead.Data["name6"]);
        Assert.Equal("待启动", lead.Data["phrase7"]);
        Assert.Equal($"pages/order-detail/index?id={id}", lead.Page);
        Assert.Equal("赵主管安排你协助，负责人王工", Content(sent.Single(m => Who(m) == "刘工")));
    }

    [Fact]
    public async Task 主管改派_提醒新负责人_原负责人没有消息()
    {
        var id = await NewAccepted("改派订阅客户");
        Take();
        await Orders.Assign(id, new AssignRequest { EngineerId = Liu.Id }, Sup);
        var m = Assert.Single(Expect("刘工：新工单指派给你"));
        Assert.Equal("赵主管把工单改派给你，请及时处理", Content(m));
    }

    [Fact]
    public async Task 改派让待确认的转交失效_提醒转交发起人()
    {
        var id = await NewAccepted("改派失效客户");
        await StartTransfer(id, Wang, Liu);
        Take();
        await Orders.Assign(id, new AssignRequest { EngineerId = Chen.Id }, Sup);
        var sent = Expect("陈工：新工单指派给你", "王工：转交已失效");
        Assert.Equal("主管已改派，转交自动失效", Content(sent.Single(m => Who(m) == "王工")));
    }

    [Fact]
    public async Task 改派时原负责人被加为协助人_只收一条()
    {
        var id = await NewAccepted("改派协助客户");
        await StartTransfer(id, Wang, Liu);
        Take();
        await Orders.Assign(id, new AssignRequest { EngineerId = Chen.Id, AssistantIds = new() { Wang.Id } }, Sup);
        Expect("陈工：新工单指派给你", "王工：你被加为协助人");
    }

    [Fact]
    public async Task 调整协助人_只提醒新加的人()
    {
        var id = await NewAccepted("协助订阅客户");
        Take();
        await Orders.SetAssistants(id, Set(Liu), Sup);
        Expect("刘工：你被加为协助人");
        // 刘工保留、加陈工 → 只提醒陈工
        await Orders.SetAssistants(id, Set(Liu, Chen), Sup);
        Expect("陈工：你被加为协助人");
        // 只移除 → 不提醒；没有变化 → 不提醒
        await Orders.SetAssistants(id, Set(Chen), Sup);
        ExpectNone();
        await Orders.SetAssistants(id, Set(Chen), Sup);
        ExpectNone();
    }

    // ---------- 给工程师：转交 ----------

    [Fact]
    public async Task 发起转交_提醒接手人_超长原因被截断()
    {
        var id = await NewAccepted("转交订阅客户");
        Take();
        await StartTransfer(id, Wang, Liu);
        var m = Assert.Single(Expect("刘工：有工单转交给你，请确认"));
        Assert.StartsWith("王工想把工单转交给你：", Content(m));
        Assert.True(Content(m).Length <= 20);
        Assert.Equal("王工", m.Data["name6"]);
    }

    [Fact]
    public async Task 确认接手_提醒发起人_接手人自己没有消息()
    {
        var id = await NewAccepted("接手订阅客户");
        var tid = await StartTransfer(id, Wang, Liu);
        Take();
        await Orders.AcceptTransfer(id, tid, Liu);
        var m = Assert.Single(Expect("王工：转交已被接手"));
        Assert.Equal("刘工已接手，你已退出本单", Content(m));
        // 已退出的人仍有查看权：消息指向同一张工单详情
        Assert.Equal($"pages/order-detail/index?id={id}", m.Page);
    }

    [Fact]
    public async Task 拒绝接手_提醒发起人_有无理由两种说明()
    {
        var id = await NewAccepted("拒绝订阅客户");
        var tid = await StartTransfer(id, Wang, Liu);
        Take();
        await Orders.RejectTransfer(id, tid, new ReplyRequest(), Liu);
        Assert.Equal("刘工拒绝接手，工单仍由你负责", Content(Assert.Single(Expect("王工：转交被拒绝"))));

        tid = await StartTransfer(id, Wang, Liu);
        Take();
        await Orders.RejectTransfer(id, tid, new ReplyRequest { Reply = "手上有急单" }, Liu);
        Assert.Equal("刘工拒绝接手：手上有急单", Content(Assert.Single(Expect("王工：转交被拒绝"))));
    }

    [Fact]
    public async Task 主管撤销转交_提醒发起人_发起人自己撤回不提醒()
    {
        var id = await NewAccepted("撤销订阅客户");
        var tid = await StartTransfer(id, Wang, Liu);
        Take();
        await Orders.CancelTransfer(id, tid, Wang);
        ExpectNone();

        tid = await StartTransfer(id, Wang, Liu);
        Take();
        await Orders.CancelTransfer(id, tid, Sup);
        Assert.Equal("赵主管撤销了转交，工单仍由你负责", Content(Assert.Single(Expect("王工：转交已失效"))));
    }

    // ---------- 给工程师：申请的审批结果 ----------

    [Fact]
    public async Task 暂停申请_同意与驳回都提醒申请人_协助人没有消息()
    {
        var id = await NewAccepted("暂停审批客户");
        await Orders.SetAssistants(id, Set(Chen), Sup);
        await Orders.RequestHold(id, HoldBody("WaitCustomer"), Wang);
        Take();
        await Orders.RejectRequest(id, RequestsOf(id).Single().Id, new ReplyRequest { Reply = "客户明天就到" }, Sup);
        Assert.Equal("驳回原因：客户明天就到", Content(Assert.Single(Expect("王工：暂停申请被驳回"))));

        await Orders.RequestHold(id, HoldBody("WaitCustomer"), Wang);
        Take();
        await Orders.ApproveRequest(id, RequestsOf(id).Last().Id, Sup);
        var m = Assert.Single(Expect("王工：暂停申请已同意"));
        Assert.Equal("赵主管已同意，工单已暂停搁置", Content(m));
        Assert.Equal("暂停搁置", m.Data["phrase7"]);
    }

    [Fact]
    public async Task 协助申请_同意时提醒申请人和被加的人_驳回只提醒申请人()
    {
        var id = await NewAccepted("协助审批客户");
        await Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = Liu.Id }, Wang);
        Take();
        await Orders.ApproveRequest(id, RequestsOf(id).Single().Id, Sup);
        var sent = Expect("王工：协助申请已同意", "刘工：你被加为协助人");
        Assert.Equal("赵主管已同意，刘工加入协助", Content(sent.Single(m => Who(m) == "王工")));
        Assert.Equal("赵主管同意你协助，负责人王工", Content(sent.Single(m => Who(m) == "刘工")));

        await Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = Chen.Id }, Wang);
        Take();
        await Orders.RejectRequest(id, RequestsOf(id).Last().Id, new ReplyRequest(), Sup);
        Assert.Equal("赵主管驳回了你的申请", Content(Assert.Single(Expect("王工：协助申请被驳回"))));
    }

    // ---------- 给工程师：驳回完工 / 暂停 / 恢复；给建单人：闭环 ----------

    [Fact]
    public async Task 完工被驳回_提醒负责人()
    {
        var id = await RemoteSubmitted("驳回订阅客户");
        Take();
        await Orders.Reject(id, new ReasonRequest { Reason = "缺少维修前照片" }, Sup);
        var m = Assert.Single(Expect("王工：完工被驳回，请重新处理"));
        Assert.Equal("退回原因：缺少维修前照片", Content(m));
        Assert.Equal("处理中", m.Data["phrase7"]);

        await Orders.Complete(id, Done(Wang), Wang);
        Take();
        await Orders.Reject(id, new ReasonRequest(), Sup);
        Assert.Equal("赵主管退回了完工资料", Content(Assert.Single(Expect("王工：完工被驳回，请重新处理"))));
    }

    [Fact]
    public async Task 主管暂停与恢复_只提醒负责人()
    {
        var id = await NewAccepted("暂停订阅客户");
        await Orders.SetAssistants(id, Set(Chen), Sup);
        Take();
        await Orders.Hold(id, HoldBody("WaitParts", "加热管下周到"), Sup);
        var m = Assert.Single(Expect("王工：工单已暂停"));
        Assert.StartsWith("暂停原因：等备件", Content(m));
        Assert.Equal("暂停搁置", m.Data["phrase7"]);

        await Orders.Resume(id, Sup);
        m = Assert.Single(Expect("王工：工单已恢复处理"));
        Assert.Equal("赵主管已恢复工单，可继续处理", Content(m));
        Assert.Equal("待启动", m.Data["phrase7"]);
    }

    [Fact]
    public async Task 闭环_提醒建单人_销售或工程师()
    {
        var id = await RemoteSubmitted("闭环订阅客户");
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        Take();
        await Orders.Close(id, Sup);
        var m = Assert.Single(Expect("王敏：工单已闭环"));
        Assert.Equal("赵主管已确认闭环", Content(m));
        Assert.Equal("已闭环", m.Data["phrase7"]);

        // 工程师建的单：提醒这位工程师
        id = await RemoteSubmitted("闭环订阅客户二", lead: "王工", creator: "李工");
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        Take();
        await Orders.Close(id, Sup);
        Expect("李工：工单已闭环");
    }

    [Fact]
    public async Task 闭环_主管建的单不提醒()
    {
        var other = AddEmployee("钱主管", Role.Supervisor, "oid-qian");
        var id = (await Orders.Create(NewOrder("主管建单闭环", ServiceType.Remote), other)).Id;
        await Orders.Assign(id, new AssignRequest { EngineerId = Wang.Id }, Sup);
        await Orders.Start(id, Wang);
        await Orders.Complete(id, Done(Wang), Wang);
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        Take();
        await Orders.Close(id, Sup);
        ExpectNone();
    }

    // ---------- 收件人过滤 ----------

    [Fact]
    public async Task 不发给操作人本人()
    {
        // 王工是负责人，之后被改成主管角色：他自己暂停自己负责的工单 → 不提醒自己
        var id = await NewAccepted("自己暂停客户");
        Db.Employees.Where(e => e.Name == "王工").ExecuteUpdate(s => s.SetProperty(e => e.Role, Role.Supervisor));
        Take();
        await Orders.Hold(id, HoldBody("WaitParts"), U("王工"));
        ExpectNone();
        Assert.Equal(OrderStatus.OnHold, OrderRow(id).Status);
    }

    [Fact]
    public async Task 收件人过滤_本人_未绑定_停用_空值都不发_同一个人只收第一条()
    {
        var wang = Emp("王工");
        var liu = Emp("刘工");
        var unbound = Emp("陈工");
        unbound.OpenId = " ";
        var disabled = Emp("李工");
        disabled.Enabled = false;
        var slots = new SubscribeSlots("事项一", "SH-1", "客户", "说明", Clock.Now, "王工", "处理中", "设备");
        await Subscribe.NotifyAsync(wang.Id, new[]
        {
            new SubscribeNotice(7, new Employee?[] { wang, null, unbound, disabled, liu, liu }, slots),
            new SubscribeNotice(7, new Employee?[] { liu, Emp("王敏") }, slots with { Title = "事项二" }),
        });
        Expect("刘工：事项一", "王敏：事项二");
    }

    [Fact]
    public async Task 未绑定微信的人不发_停用的人不发()
    {
        SetOpenId("王工", null);
        var id = await NewPending("未绑定客户", ServiceType.Remote);
        Take();
        await Orders.Assign(id, new AssignRequest { EngineerId = Wang.Id }, Sup);
        ExpectNone();

        // 建单人已停用 → 闭环不提醒
        await Orders.Start(id, Wang);
        await Orders.Complete(id, Done(Wang), Wang);
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        Db.Employees.Where(e => e.Name == "王敏").ExecuteUpdate(s => s.SetProperty(e => e.Enabled, false));
        Db.ChangeTracker.Clear();
        Take();
        await Orders.Close(id, Sup);
        ExpectNone();
        Assert.Equal(OrderStatus.Closed, OrderRow(id).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 未启用时什么都不发(bool noTemplate)
    {
        if (noTemplate) WxOptions.Subscribe.TemplateId = "";
        else WxOptions.Subscribe.Fields = new();
        Assert.False(Subscribe.Enabled);
        Assert.Empty(Subscribe.TemplateIds);

        var id = await NewPending("未启用客户", ServiceType.Remote);
        await Orders.Assign(id, new AssignRequest { EngineerId = Wang.Id, AssistantIds = new() { Liu.Id } }, Sup);
        var tid = await StartTransfer(id, Wang, Chen);
        await Orders.AcceptTransfer(id, tid, Chen);
        await Orders.Start(id, Chen);
        await Orders.Complete(id, Done(Chen), Chen);
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        await Orders.Close(id, Sup);
        ExpectNone();
        Assert.Equal(OrderStatus.Closed, OrderRow(id).Status);
    }

    // ---------- 内容硬规则 ----------

    [Fact]
    public async Task 全流程的消息里没有价格_定价与价格建议本身不发消息()
    {
        var id = await NewPending("保密测试客户", ServiceType.Remote);
        await Orders.Assign(id, new AssignRequest { EngineerId = Wang.Id, AssistantIds = new() { Liu.Id } }, Sup);
        var before = Subscribe.MockSent.Count;
        await Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 87654.32m, Note = "老客户，建议八万七" }, Wm);
        await Orders.SetPrice(id, new PriceRequest { Price = 98765.43m }, Sup);
        Assert.Equal(before, Subscribe.MockSent.Count);

        await Orders.Hold(id, HoldBody("WaitParts"), Sup);
        await Orders.Resume(id, Sup);
        var tid = await StartTransfer(id, Wang, Chen);
        await Orders.AcceptTransfer(id, tid, Chen);
        await Orders.Start(id, Chen);
        await Orders.Complete(id, Done(Chen), Chen);
        await Orders.Reject(id, new ReasonRequest { Reason = "补照片" }, Sup);
        await Orders.Complete(id, Done(Chen), Chen);
        await Orders.SetPrice(id, new PriceRequest { Price = 91234.56m }, Sup);
        await Orders.Close(id, Sup);

        var sent = Take();
        Assert.True(sent.Count >= 9);
        Assert.Contains(sent, m => Title(m) == "工单已闭环");
        foreach (var v in sent.SelectMany(m => m.Data.Values).Append(string.Join("|", sent.Select(m => m.Page))))
        {
            foreach (var secret in new[] { "98765", "98,765", "87654", "87,654", "91234", "91,234", "八万七", "¥", "￥", "价", "元" })
                Assert.DoesNotContain(secret, v);
        }
    }

    // ---------- 不影响主流程 ----------

    private sealed class ThrowingNotifier : ISubscribeNotifier
    {
        public int Calls;
        public bool Enabled => true;
        public IReadOnlyList<string> TemplateIds => new[] { "x" };
        public Task NotifyAsync(int actorId, IReadOnlyList<SubscribeNotice> notices)
        {
            Calls++;
            throw new InvalidOperationException("微信挂了");
        }
    }

    [Fact]
    public async Task 发送抛异常_业务照常成功()
    {
        var bad = new ThrowingNotifier();
        var orders = new OrderService(Db, Clock, View, Files, Notifier, NullLogger<OrderService>.Instance, bad);
        var id = (await orders.Create(NewOrder("发送失败客户", ServiceType.Remote), Wm)).Id;
        var d = await orders.Assign(id, new AssignRequest { EngineerId = Wang.Id }, Sup);
        Assert.Equal(OrderStatus.Accepted, d.Status);
        var tid = (await orders.Transfer(id, To(Liu), Wang)).PendingTransfer!.Id;
        d = await orders.AcceptTransfer(id, tid, Liu);
        Assert.Equal(Liu.Id, OrderRow(id).AssigneeId);
        await orders.Start(id, Liu);
        d = await orders.Complete(id, Done(Liu), Liu);
        Assert.Equal(OrderStatus.Submitted, d.Status);
        await orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        d = await orders.Close(id, Sup);
        Assert.Equal(OrderStatus.Closed, d.Status);
        Assert.True(bad.Calls >= 6);
    }

    /// <summary>记录每次被调用时的数据库状态：是否还在事务里、工单是否已经是动作之后的样子</summary>
    private sealed class ProbeNotifier : ISubscribeNotifier
    {
        private readonly Huotong.AfterSales.Api.Data.AppDbContext _db;
        public List<(string Title, bool InTransaction, OrderStatus Status, int? AssigneeId, int? PendingTo)> Seen { get; } = new();
        public ProbeNotifier(Huotong.AfterSales.Api.Data.AppDbContext db) => _db = db;
        public bool Enabled => true;
        public IReadOnlyList<string> TemplateIds => new[] { "x" };
        public Task NotifyAsync(int actorId, IReadOnlyList<SubscribeNotice> notices)
        {
            var row = _db.Orders.AsNoTracking().Single(o => o.Id == notices[0].OrderId);
            Seen.Add((notices[0].Slots.Title, _db.Database.CurrentTransaction != null, row.Status, row.AssigneeId, row.PendingTransferToId));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task 提醒在事务提交之后才发()
    {
        var probe = new ProbeNotifier(Db);
        var orders = new OrderService(Db, Clock, View, Files, Notifier, NullLogger<OrderService>.Instance, probe);
        var id = (await orders.Create(NewOrder("提交后客户", ServiceType.Remote), Wm)).Id;
        await orders.Assign(id, new AssignRequest { EngineerId = Wang.Id }, Sup);
        var tid = (await orders.Transfer(id, To(Liu), Wang)).PendingTransfer!.Id;
        await orders.AcceptTransfer(id, tid, Liu);
        await orders.RequestHold(id, HoldBody("WaitParts"), Liu);
        await orders.ApproveRequest(id, RequestsOf(id).Single().Id, Sup);
        await orders.Resume(id, Sup);
        await orders.Start(id, Liu);
        await orders.Complete(id, Done(Liu), Liu);

        Assert.Equal(new[] { "新工单待指派", "新工单指派给你", "有工单转交给你，请确认", "转交已被接手", "有申请待审批", "暂停申请已同意", "工单已恢复处理", "工单待验收" },
            probe.Seen.Select(x => x.Title));
        Assert.All(probe.Seen, x => Assert.False(x.InTransaction));
        Assert.Equal(OrderStatus.Pending, probe.Seen[0].Status);
        Assert.Equal((OrderStatus.Accepted, (int?)Wang.Id), (probe.Seen[1].Status, probe.Seen[1].AssigneeId));
        Assert.Equal((int?)Liu.Id, probe.Seen[2].PendingTo);
        Assert.Equal(((int?)Liu.Id, (int?)null), (probe.Seen[3].AssigneeId, probe.Seen[3].PendingTo));
        Assert.Equal(OrderStatus.OnHold, probe.Seen[5].Status);
        Assert.Equal(OrderStatus.Submitted, probe.Seen[7].Status);
    }
}
