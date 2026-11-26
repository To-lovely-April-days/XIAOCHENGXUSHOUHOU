using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v2.2 收费确认（工程师判断 → 销售报价 → 销售总监审核 → 售后主管确认 → 回到工程师）与提醒设置</summary>
public sealed class V22ChargeTests : V12TestBase
{
    private CurrentUser Wang => U("王工");
    private CurrentUser Liu => U("刘工");
    private CurrentUser Wm => U("王敏");
    private CurrentUser Sl => U("孙磊");
    private readonly CurrentUser _director;

    public V22ChargeTests()
    {
        WxOptions.Subscribe.TemplateId = "TPL";
        WxOptions.Subscribe.Fields = new Dictionary<string, string> { ["thing1"] = "Title", ["thing4"] = "Content" };
        var e = new Employee
        {
            Name = "钱总监", Phone = "13722220001", Role = Role.Sales, Position = Position.SalesManager, Department = "销售部门",
            Enabled = true, CreatedAt = Clock.Now
        };
        Db.Employees.Add(e);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        _director = new CurrentUser(e.Id, Role.Sales, SalesManager: true);
        foreach (var x in Db.Employees.AsNoTracking().ToList())
            Db.Employees.Where(y => y.Id == x.Id).ExecuteUpdate(s => s.SetProperty(y => y.OpenId, "oid-" + x.Id));
    }

    /// <summary>取走到目前为止的微信提醒：（收件人 openid, 标题, 内容）</summary>
    private List<(string To, string Title, string Content)> Take()
    {
        var list = Subscribe.MockSent.Select(m => (m.OpenId, m.Data["thing1"], m.Data["thing4"])).ToList();
        Subscribe.ClearMockSent();
        return list;
    }

    private static string Oid(CurrentUser u) => "oid-" + u.Id;

    private Task<OrderDetail> Decide(int id, bool charge, string? reason = "设备已过保", int? salesId = null, CurrentUser? by = null) =>
        Orders.DecideCharge(id, new ChargeDecideRequest { Charge = charge, Reason = reason, SalesId = salesId }, by ?? Wang);

    [Fact]
    public async Task FullFlowNotifiesEachStepAndComesBackToEngineer()
    {
        var id = await NewAccepted("收费全流程客户", service: ServiceType.Remote); // 王敏（销售）提的单
        var d = await Orders.Detail(id, Wang);
        Assert.Equal(("None", true, false, "王敏"), (d.Charge.Status, d.Charge.CanDecide, d.Charge.Pending, d.Charge.FixedSales!.Name));
        Take();

        // 1 工程师发起：销售提的单自动给提单的销售，选了别人也不算
        await Fails(40000, "请填写收费原因", () => Decide(id, true, " "));
        await Fails(40300, "只有负责工程师可以操作", () => Decide(id, true, by: Liu));
        d = await Decide(id, true, salesId: Sl.Id);
        Assert.Equal(("Quoting", true, false, "王敏"), (d.Charge.Status, d.Charge.Pending, d.Charge.CanDecide, d.Charge.Sales!.Name));
        Assert.Equal(new[] { (Oid(Wm), "有收费待你报价", "王工判断需要收费：设备已过保") }, Take());
        await Fails(40900, "收费确认正在进行中", () => Decide(id, false));
        // 进行中：不能开始处理
        await Fails(40900, ChargeRules.Blocked, () => Orders.Start(id, Wang));

        // 2 销售报价：只有指定的销售（或销售总监）
        await Fails(40300, "这张单由 王敏 报价", () => Orders.QuoteCharge(id, new ChargeQuoteRequest { Amount = 100 }, Sl));
        await Fails(40000, "请填写收费金额", () => Orders.QuoteCharge(id, new ChargeQuoteRequest(), Wm));
        await Fails(40000, "收费金额要大于 0；不收费请点「不收费」", () => Orders.QuoteCharge(id, new ChargeQuoteRequest { Amount = 0 }, Wm));
        Assert.Equal("Quote", Assert.Single(await Orders.ChargeTodos(Wm)).Step);
        d = await Orders.QuoteCharge(id, new ChargeQuoteRequest { Amount = 3200, Note = "配件 2400 + 人工 800" }, Wm);
        Assert.Equal(("DirectorReview", 3200m, false), (d.Charge.Status, d.Charge.Amount, d.Charge.CanQuote));
        Assert.Equal(new[] { (Oid(_director), "有收费待你审核", "王敏已报价，请审核") }, Take());
        Assert.Empty(await Orders.ChargeTodos(Wm));

        // 3 销售总监审核：先退回一次，再同意并选付款情况
        await Fails(40300, "只有销售总监可以审核收费", () => Orders.ReviewCharge(id, new ChargeReviewRequest { Approve = true, Paid = true }, Wm));
        await Fails(40000, "请填写退回原因", () => Orders.ReviewCharge(id, new ChargeReviewRequest { Approve = false }, _director));
        d = await Orders.ReviewCharge(id, new ChargeReviewRequest { Approve = false, Note = "金额偏高" }, _director);
        Assert.Equal(("Quoting", "钱总监 退回：金额偏高"), (d.Charge.Status, d.Charge.ReturnNote));
        Assert.Equal(new[] { (Oid(Wm), "报价被退回，请重新报价", "退回原因：金额偏高") }, Take());
        await Orders.QuoteCharge(id, new ChargeQuoteRequest { Amount = 2800 }, Wm);
        Take();
        await Fails(40000, "请选择客户是否已付款", () => Orders.ReviewCharge(id, new ChargeReviewRequest { Approve = true }, _director));
        Assert.Equal("Review", Assert.Single(await Orders.ChargeTodos(_director)).Step);
        d = await Orders.ReviewCharge(id, new ChargeReviewRequest { Approve = true, Paid = false, Note = "按报价执行" }, _director);
        Assert.Equal(("SupervisorConfirm", false, "未付款，先服务", null), (d.Charge.Status, d.Charge.Paid, d.Charge.PaidText, d.Charge.ReturnNote));
        Assert.Equal(new[] { (Oid(Sup), "有收费待你确认", "钱总监已审核，未付款，先服务") }, Take());

        // 4 售后主管确认：主管首页待办里有；确认后价格按报价记下，工单回到工程师
        var home = await Home.Supervisor(Sup);
        Assert.Contains(home.Todos, t => t.Type == "ChargeConfirm" && t.OrderId == id);
        Assert.Equal(1, (await Home.Me(Sup)).ChargeTodoCount);
        d = await Orders.ConfirmCharge(id, new ChargeConfirmRequest { Approve = true, Note = "备件已到库" }, Sup);
        Assert.Equal(("Confirmed", false, 2800m, true), (d.Charge.Status, d.Charge.Pending, d.Price, d.PriceSet));
        Assert.Equal(new[] { true, true, true, true }, d.Charge.Steps.Select(s => s.Done));
        Assert.Equal(new[] { "王工", "王敏", "钱总监", "赵主管" }, d.Charge.Steps.Select(s => s.Who));
        var sent = Take();
        Assert.Contains((Oid(Wang), "收费已确认，请继续处理", "赵主管已确认收费：备件已到库"), sent);
        Assert.Contains((Oid(Wm), "收费已确认", "赵主管已确认，工单继续处理"), sent);
        Assert.Contains((Oid(_director), "收费已确认", "赵主管已确认，工单继续处理"), sent);
        // 任何一条提醒里都没有金额
        Assert.DoesNotContain(Notifier.Sent, n => n.Text.Contains("2800") || n.Text.Contains("3200") || n.Text.Contains("2,800"));

        // 回到工程师：可以开始了；工程师看不到金额，也看不到带金额的记录
        await Fails(40900, "收费已经确认，不能再改", () => Decide(id, false));
        d = await Orders.Start(id, Wang);
        Assert.Equal(OrderStatus.InProgress, d.Status);
        Assert.Null(d.Charge.Amount);
        Assert.Null(d.Charge.QuoteNote);
        Assert.Null(d.Charge.DirectorNote);
        Assert.Equal("备件已到库", d.Charge.SupervisorNote);
        Assert.DoesNotContain(d.Logs, l => l.Text.Contains("¥") || l.Text.Contains("2800") || l.Text.Contains("2,800"));
        Assert.Contains(d.Logs, l => l.Text == "王工 判断需要收费，交给 王敏 报价（设备已过保）");
        Assert.Contains(d.Logs, l => l.Text == "赵主管 确认收费，工单回到 王工 继续处理（备件已到库）");
        // 主管看得到带金额的记录
        Assert.Contains((await Orders.Detail(id, Sup)).Logs, l => l.Kind == LogKind.Price && l.Text.Contains("报价"));
    }

    [Fact]
    public async Task EngineerPicksSalesWhenOrderNotCreatedBySales()
    {
        var id = await NewAccepted("主管建的单", creator: "赵主管", service: ServiceType.Remote);
        Assert.Null((await Orders.Detail(id, Wang)).Charge.FixedSales);
        await Fails(40000, "请选择交给哪位销售报价", () => Decide(id, true));
        await Fails(40000, "所选销售不存在或已停用", () => Decide(id, true, salesId: Liu.Id));
        var d = await Decide(id, true, salesId: Sl.Id);
        Assert.Equal("孙磊", d.Charge.Sales!.Name);
        Assert.Contains((await Home.Sales()), s => s.Name == "孙磊");
        // 销售总监可以代报价
        d = await Orders.QuoteCharge(id, new ChargeQuoteRequest { Amount = 500 }, _director);
        Assert.Equal(("DirectorReview", "钱总监"), (d.Charge.Status, d.Charge.Sales!.Name));
    }

    [Fact]
    public async Task FreeByEngineerOrBySalesSetsPriceZeroAndUnblocks()
    {
        var id = await NewAccepted("不收费客户", service: ServiceType.Remote);
        var d = await Decide(id, false, "保内");
        Assert.Equal(("Free", false, true), (d.Charge.Status, d.Charge.Pending, d.Charge.CanDecide)); // 判断错了还能改
        var sup = await Orders.Detail(id, Sup);
        Assert.Equal((0m, true), (sup.Price, sup.PriceSet));
        Assert.Equal("定为不收费", Assert.Single(d.Charge.Steps).Title);

        // 改成要收费，销售看了说不收：工程师收到提醒，可以继续
        await Decide(id, true);
        Take();
        d = await Orders.QuoteCharge(id, new ChargeQuoteRequest { Free = true, Note = "老客户赠送" }, Wm);
        Assert.Equal(("Free", false), (d.Charge.Status, d.Charge.Pending));
        Assert.Equal(new[] { (Oid(Wang), "本单不收费，可以继续处理", "王敏确认不收费：老客户赠送") }, Take());
        Assert.Equal(new[] { "工程师发起收费", "定为不收费" }, d.Charge.Steps.Select(s => s.Title));
        Assert.Equal(OrderStatus.InProgress, (await Orders.Start(id, Wang)).Status);
    }

    [Fact]
    public async Task PendingChargeBlocksArrivePunchAndCompleteButNotLeave()
    {
        var id = await NewAccepted("收费卡住客户");
        PunchRequest P(PunchKind kind) => new()
        {
            OrderId = id, Kind = kind, Latitude = 31.30, Longitude = 120.72, Address = "x",
            PhotoFileId = NewFile(Wang, FileCategory.PunchPhoto), FormVersion = 2, WorkContent = "排查"
        };
        await Punches.Create(Wang, P(PunchKind.Arrive));
        Tick(20);
        await Decide(id, true); // 已经在现场了才发起
        await Fails(40900, ChargeRules.Blocked, () => Orders.Complete(id, new CompleteRequest(), Wang));
        await Punches.Create(Wang, P(PunchKind.Leave)); // 离场卡照常
        Tick(5);
        await Fails(40900, ChargeRules.Blocked, () => Punches.Create(Wang, P(PunchKind.Arrive)));

        // 主管退回：有销售总监审核过回到销售总监，否则回到销售
        await Orders.QuoteCharge(id, new ChargeQuoteRequest { Amount = 100 }, Wm);
        await Orders.ReviewCharge(id, new ChargeReviewRequest { Approve = true, Paid = true }, _director);
        Take();
        var d = await Orders.ConfirmCharge(id, new ChargeConfirmRequest { Approve = false, Note = "先别收" }, Sup);
        Assert.Equal("DirectorReview", d.Charge.Status);
        Assert.Equal(new[] { (Oid(_director), "收费确认被退回，请重新审核", "退回原因：先别收") }, Take());
        await Fails(40900, "收费确认已经到了别的步骤，请刷新", () => Orders.ConfirmCharge(id, new ChargeConfirmRequest { Approve = true }, Sup));
    }

    [Fact]
    public async Task NewClientMustDecideChargeBeforeComplete()
    {
        var id = await NewAccepted("完工前要判断客户", service: ServiceType.Remote);
        await Orders.Start(id, Wang);
        var req = new CompleteRequest
        {
            FaultCause = "参数错误", Solution = "恢复参数", RunStatus = RunStatus.Normal, FormVersion = 3,
            CustomerConfirm = "电话确认", WorkContent = "远程处理"
        };
        await Fails(40000, ChargeRules.DecideFirst, () => Orders.Complete(id, req, Wang));
        await Decide(id, false);
        Assert.Equal(OrderStatus.Submitted, (await Orders.Complete(id, req, Wang)).Status);
    }

    [Fact]
    public async Task WithoutSalesDirectorQuoteGoesStraightToSupervisor()
    {
        Db.Employees.Where(e => e.Id == _director.Id).ExecuteUpdate(s => s.SetProperty(e => e.Enabled, false));
        var id = await NewAccepted("没有总监客户", service: ServiceType.Remote);
        await Decide(id, true);
        Take();
        var d = await Orders.QuoteCharge(id, new ChargeQuoteRequest { Amount = 300 }, Wm);
        Assert.Equal("SupervisorConfirm", d.Charge.Status);
        Assert.Equal(new[] { "工程师发起收费", "销售报价", "售后主管确认" }, d.Charge.Steps.Select(s => s.Title));
        Assert.Equal(new[] { (Oid(Sup), "有收费待你确认", "王敏已报价，请确认") }, Take());
        d = await Orders.ConfirmCharge(id, new ChargeConfirmRequest { Approve = false, Note = "重报" }, Sup);
        Assert.Equal("Quoting", d.Charge.Status);
    }

    [Fact]
    public async Task NoticeSettingsMuteOneCategoryOnly()
    {
        var s = await Home.Notices(Wm, true);
        Assert.Equal(new[] { "assign", "request", "progress", "charge" }, s.Items.Select(i => i.Key)); // 没有注册审批权限：没有「注册申请」
        Assert.All(s.Items, i => Assert.True(i.On));
        await Fails(40000, "提醒类别不正确", () => Home.SetNotice(Wm, new NoticeToggleRequest { Key = "register", On = false }, true));
        s = await Home.SetNotice(Wm, new NoticeToggleRequest { Key = "charge", On = false }, true);
        Assert.False(s.Items.Single(i => i.Key == "charge").On);

        // 关了「收费确认」：不再收到待报价的提醒
        var id = await NewAccepted("关提醒客户", service: ServiceType.Remote);
        Take();
        await Decide(id, true);
        Assert.Empty(Take());
        // 其他类别照常：闭环提醒属于「进度与验收」
        await Orders.QuoteCharge(id, new ChargeQuoteRequest { Free = true }, Wm);
        await Orders.Start(id, Wang);
        await Orders.Complete(id, new CompleteRequest
        {
            FaultCause = "a", Solution = "b", RunStatus = RunStatus.Normal, FormVersion = 3, CustomerConfirm = "电话确认", WorkContent = "c"
        }, Wang);
        Take();
        await Orders.Close(id, Sup);
        Assert.Equal(new[] { (Oid(Wm), "工单已闭环", "赵主管已确认闭环") }, Take());

        // 重新打开
        s = await Home.SetNotice(Wm, new NoticeToggleRequest { Key = "charge", On = true }, true);
        Assert.All(s.Items, i => Assert.True(i.On));
        Assert.Equal("", Db.Employees.AsNoTracking().Single(e => e.Id == Wm.Id).MutedNotices);
    }

    [Theory]
    [InlineData("新工单指派给你", "assign")]
    [InlineData("有工单转交给你，请确认", "assign")]
    [InlineData("你被加为协助人", "assign")]
    [InlineData("新工单待指派", "assign")]
    [InlineData("有申请待审批", "request")]
    [InlineData("协助申请已同意", "request")]
    [InlineData("服务方式已升级", "request")]
    [InlineData("工单待验收", "progress")]
    [InlineData("完工被驳回，请重新处理", "progress")]
    [InlineData("工单已闭环", "progress")]
    [InlineData("有收费待你报价", "charge")]
    [InlineData("报价被退回，请重新报价", "charge")]
    [InlineData("本单不收费，可以继续处理", "charge")]
    public void TitlesMapToCategories(string title, string category) => Assert.Equal(category, NoticeRules.CategoryOf(title));
}
