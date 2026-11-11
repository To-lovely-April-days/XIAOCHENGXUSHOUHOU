using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.1 (1) 返厂流程：5 节点时间线</summary>
public sealed class V11FactoryTests : V11TestBase
{
    private const FactoryNode Shipped = FactoryNode.CustomerShipped;
    private const FactoryNode Received = FactoryNode.FactoryReceived;
    private const FactoryNode Back = FactoryNode.ShippedBack;
    private const FactoryNode Signed = FactoryNode.CustomerSigned;

    private CurrentUser Sales => U("王敏");
    private CurrentUser Wang => U("王工");

    private int Photo(CurrentUser u, FileCategory cat = FileCategory.FactoryReceivePhoto) => NewFile(u, cat);

    /// <summary>返厂单：销售建单、王工认领（待启动）</summary>
    private Task<int> NewFactory(string customer) => NewAccepted(customer, "王工", ServiceType.Factory);

    /// <summary>推进到「已寄回客户」（处理中）</summary>
    private async Task<int> ShippedBackOrder(string customer)
    {
        var id = await NewFactory(customer);
        await Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", "SF1"), Sales);
        await Orders.RecordFactoryNode(id, Node(Received, null, null, Photo(Wang)), Wang);
        Tick();
        await Orders.RecordFactoryNode(id, Node(Back, "顺丰", "SF2", Photo(Wang, FileCategory.FactoryShipPhoto)), Wang);
        return id;
    }

    [Fact]
    public async Task FactoryHappyPath()
    {
        var id = await NewPending("返厂全流程客户", ServiceType.Factory);
        var d = await Orders.Detail(id, Sales);
        Assert.Equal("待客户寄出", d.FactoryText);
        Assert.Equal(new[] { "current", "todo", "todo", "todo", "todo" }, States(d));
        Assert.Equal(new[] { Shipped, Received, FactoryNode.Repairing, Back, Signed }, d.Factory!.Nodes.Select(n => n.Node));
        Assert.Equal(new[] { "客户寄出", "工厂收货", "检测/维修", "寄回客户", "客户签收" }, d.Factory.Nodes.Select(n => n.Label));
        Assert.Equal(new[] { true, false, false, false, false }, d.Factory.Nodes.Select(n => n.CanRecord));
        Assert.Equal(new[] { "suggestPrice", "factory" }, d.Actions);

        d = await Orders.Claim(id, Wang);
        Assert.Equal(new[] { "appoint", "factory", "requestHold", "requestAssistant" }, d.Actions); // 返厂单没有 start
        Assert.Equal(2, d.ProgressStep);

        // 1 客户寄出（建单销售）
        var before = Notifier.Sent.Count;
        d = await Orders.RecordFactoryNode(id, Node(Shipped, " 顺丰 ", " SF 1234 5678 "), Sales);
        Assert.Equal(OrderStatus.Accepted, d.Status);
        Assert.Equal("客户已寄出", d.FactoryText);
        Assert.Equal(new[] { "done", "current", "todo", "todo", "todo" }, States(d));
        var n1 = d.Factory!.Nodes[0];
        Assert.True(n1.Recorded);
        Assert.Equal("顺丰", n1.Courier);
        Assert.Equal("SF12345678", n1.TrackingNo); // 单号去掉全部空格
        Assert.Equal("王敏", n1.Operator!.Name);
        Assert.Equal(Clock.Now, n1.Time);
        Assert.False(n1.CanRecord);
        Assert.True(n1.CanEdit);
        Assert.Equal("王敏 记录客户寄出（顺丰 SF12345678）", d.Logs.Last().Text);
        Assert.Equal(LogKind.Factory, d.Logs.Last().Kind);
        Assert.Equal(OrderStatus.Accepted, d.Logs.Last().ToStatus);
        Assert.Equal(new[] { "suggestPrice" }, d.Actions); // 销售暂无可记录节点
        Assert.Equal(before + 1, Notifier.Sent.Count);
        Assert.Equal("返厂设备已寄出", Notifier.Last.Title);
        Assert.Equal($"**返厂全流程客户**（{d.OrderNo}）客户已寄出：顺丰 SF12345678", Notifier.Last.Text);
        Assert.Equal(new[] { Emp("王工").Phone }, Notifier.Last.Mobiles);

        // 2 工厂收货（负责人）：待启动 → 处理中
        Tick();
        var p1 = Photo(Wang, FileCategory.OrderPhoto); // 类别由服务端修正
        d = await Orders.RecordFactoryNode(id, Node(Received, "忽略", "忽略", p1), Wang);
        Assert.Equal(OrderStatus.InProgress, d.Status);
        Assert.Equal(3, d.ProgressStep);
        Assert.Equal("检测/维修中", d.FactoryText);
        Assert.Equal(new[] { "done", "done", "current", "current", "todo" }, States(d));
        Assert.Equal("王工 工厂收货 → 处理中", d.Logs.Last().Text);
        Assert.Equal(OrderStatus.InProgress, d.Logs.Last().ToStatus);
        var n2 = d.Factory!.Nodes[1];
        Assert.Null(n2.Courier);
        Assert.Null(n2.TrackingNo);
        Assert.Equal(new[] { p1 }, n2.Photos.Select(f => f.Id));
        Assert.Equal(FileCategory.FactoryReceivePhoto, n2.Photos[0].Category);
        Assert.Equal(new[] { p1 }, d.Files.FactoryPhotos.Select(f => f.Id));
        var repairing = d.Factory.Nodes[2];
        Assert.False(repairing.Recorded);
        Assert.False(repairing.CanRecord);
        Assert.False(repairing.CanEdit);
        Assert.Equal(n2.Time, repairing.Time);
        Assert.Equal(new[] { "appoint", "factory", "requestHold", "requestAssistant" }, d.Actions);
        Assert.Equal(Clock.Now, Db.Orders.AsNoTracking().Single(o => o.Id == id).StartedAt);
        Assert.Equal("工厂已收货", Notifier.Last.Title);
        Assert.Equal(new[] { Emp("王敏").Phone }, Notifier.Last.Mobiles);

        // 4 寄回客户
        Tick();
        var p2 = Photo(Wang, FileCategory.FactoryShipPhoto);
        d = await Orders.RecordFactoryNode(id, Node(Back, "德邦", "DB999", p2), Wang);
        Assert.Equal(OrderStatus.InProgress, d.Status);
        Assert.Equal("已寄回客户", d.FactoryText);
        Assert.Equal(new[] { "done", "done", "done", "done", "current" }, States(d));
        Assert.Equal("王工 寄回客户（德邦 DB999）", d.Logs.Last().Text);
        Assert.Equal(new[] { "appoint", "factory", "complete", "requestHold", "requestAssistant" }, d.Actions);
        Assert.Equal("返厂设备已寄回", Notifier.Last.Title);
        Assert.Equal($"**返厂全流程客户**（{d.OrderNo}）已寄回客户：德邦 DB999", Notifier.Last.Text);
        Assert.Equal(new[] { p1, p2 }, d.Files.FactoryPhotos.Select(f => f.Id));
        Assert.Equal("已寄回客户", (await Orders.Mine(Wang, "all")).Items.Single(i => i.Id == id).FactoryText);

        // 完工（返厂单不需要签字件）
        Tick();
        d = await Orders.Complete(id, Done(Wang, signature: false), Wang);
        Assert.Equal(OrderStatus.Submitted, d.Status);
        Assert.Equal(new[] { "factory" }, d.Actions);
        Assert.Equal(new[] { "suggestPrice", "factory" }, (await Orders.Detail(id, Sales)).Actions);

        // 5 客户签收（建单销售，照片与备注选填）
        var count = Notifier.Sent.Count;
        d = await Orders.RecordFactoryNode(id, Node(Signed), Sales);
        Assert.Equal(OrderStatus.Submitted, d.Status);
        Assert.Equal("客户已签收", d.FactoryText);
        Assert.Equal(new[] { "done", "done", "done", "done", "done" }, States(d));
        Assert.Equal("王敏 记录客户签收", d.Logs.Last().Text);
        Assert.Equal(count, Notifier.Sent.Count); // 节点 5 无通知
        Assert.Equal(new[] { "suggestPrice" }, d.Actions);

        await Orders.SetPrice(id, new PriceRequest { Price = 680 }, Sup);
        d = await Orders.Close(id, Sup);
        Assert.Equal(OrderStatus.Closed, d.Status);
        Assert.Empty(d.CloseHints);
        Assert.All(d.Factory!.Nodes, n => { Assert.False(n.CanRecord); Assert.False(n.CanEdit); });
        Assert.Equal(new[] { "setPrice" }, d.Actions);
    }

    [Fact]
    public async Task FactoryOrderingRules()
    {
        var id = await NewFactory("返厂顺序客户");
        await Fails(40000, "请选择返厂节点", () => Orders.RecordFactoryNode(id, new FactoryNodeRequest(), Wang));
        await Fails(40000, "请选择返厂节点", () => Orders.RecordFactoryNode(id, new FactoryNodeRequest { Node = "Nope" }, Wang));
        await Fails(40000, "请选择返厂节点", () => Orders.RecordFactoryNode(id, new FactoryNodeRequest { Node = "0" }, Wang));
        await Fails(40000, "检测/维修无需记录", () => Orders.RecordFactoryNode(id, Node(FactoryNode.Repairing), Wang));
        await Fails(40900, "只有返厂工单需要记录返厂流程", () => Orders.RecordFactoryNode(OrderId("南通某试剂厂"), Node(Shipped, "顺丰", "SF1"), Sup));
        Assert.Null((await Orders.Detail(OrderId("南通某试剂厂"), Sup)).Factory);
        Assert.Null((await Orders.Detail(OrderId("南通某试剂厂"), Sup)).FactoryText);

        // 工程师不能跳过客户寄出
        await Fails(40900, "请先记录「客户寄出」", () => Orders.RecordFactoryNode(id, Node(Received, null, null, Photo(Wang)), Wang));
        await Fails(40900, "工单当前为「待启动」，不能记录「寄回客户」", () => Orders.RecordFactoryNode(id, Node(Back, "顺丰", "SF", Photo(Wang)), Wang));
        await Fails(40900, "工单当前为「待启动」，不能记录「客户签收」", () => Orders.RecordFactoryNode(id, Node(Signed), Wang));
        var d = await Orders.Detail(id, Wang);
        Assert.Equal(new[] { true, false, false, false, false }, d.Factory!.Nodes.Select(n => n.CanRecord));
        // 主管可跳过客户寄出，所以节点 1、2 都可记录
        Assert.Equal(new[] { true, true, false, false, false }, (await Orders.Detail(id, Sup)).Factory!.Nodes.Select(n => n.CanRecord));

        // 待认领时主管记工厂收货
        var pending = await NewPending("返厂未认领客户", ServiceType.Factory);
        await Fails(40900, "请先认领或指派工程师", () => Orders.RecordFactoryNode(pending, Node(Received, null, null, Photo(Sup)), Sup));

        // 旧数据：v1.1 之前已在处理中的返厂单没有任何节点
        var legacy = Db.Orders.Single(o => o.Id == id);
        legacy.Status = OrderStatus.InProgress;
        Db.SaveChanges();
        await Fails(40900, "请先记录「工厂收货」", () => Orders.RecordFactoryNode(id, Node(Back, "顺丰", "SF", Photo(Wang)), Wang));
        await Fails(40900, "请先记录「寄回客户」", () => Orders.RecordFactoryNode(id, Node(Signed), Wang));
        await Fails(40900, "返厂工单请先记录「寄回客户」再提交完工", () => Orders.Complete(id, Done(Wang, false), Wang));

        // 主管可跳过客户寄出直接记工厂收货 → 节点 1 为 skipped
        d = await Orders.RecordFactoryNode(id, Node(Received, null, null, Photo(Sup)), Sup);
        Assert.Equal(new[] { "skipped", "done", "current", "current", "todo" }, States(d));
        Assert.Equal("赵主管 工厂收货", d.Logs.Last().Text); // 已是处理中，不带「→ 处理中」
        Assert.Equal("检测/维修中", d.FactoryText);
        Assert.False(d.Factory!.Nodes[0].CanRecord);
        await Fails(40900, "工厂已收货，无需再记录客户寄出", () => Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", "SF1"), Sup));
        await Fails(40900, "请先记录「寄回客户」", () => Orders.RecordFactoryNode(id, Node(Signed), Wang));
        // 负责人继续走寄回
        d = await Orders.RecordFactoryNode(id, Node(Back, "顺丰", "SF2", Photo(Wang)), Wang);
        Assert.Equal(new[] { "skipped", "done", "done", "done", "current" }, States(d));
    }

    [Fact]
    public async Task FactoryFieldValidation()
    {
        var id = await NewFactory("返厂校验客户");
        await Fails(40000, "请填写快递公司", () => Orders.RecordFactoryNode(id, Node(Shipped, " ", "SF1"), Sales));
        await Fails(40000, "请填写快递单号", () => Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", "  "), Sales));
        await Fails(40000, "快递公司不能超过 50 个字", () => Orders.RecordFactoryNode(id, Node(Shipped, new string('长', 51), "SF1"), Sales));
        await Fails(40000, "快递单号不能超过 50 个字", () => Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", new string('9', 51)), Sales));
        var long1 = Node(Shipped, "顺丰", "SF1");
        long1.Note = new string('长', 501);
        await Fails(40000, "备注不能超过 500 个字", () => Orders.RecordFactoryNode(id, long1, Sales));
        // 节点 1 不收照片（忽略 photoIds）
        var d = await Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", "SF1", 99999), Sales);
        Assert.Empty(d.Factory!.Nodes[0].Photos);

        await Fails(40000, "请至少上传 1 张收货照片（外观/配件）", () => Orders.RecordFactoryNode(id, Node(Received), Wang));
        var ten = Enumerable.Range(0, 10).Select(_ => Photo(Wang)).ToArray();
        await Fails(40000, "照片最多 9 张", () => Orders.RecordFactoryNode(id, Node(Received, null, null, ten), Wang));
        await Fails(40000, "收货照片不存在，请重新上传", () => Orders.RecordFactoryNode(id, Node(Received, null, null, 99999), Wang));
        Db.ChangeTracker.Clear();
        Assert.Equal(OrderStatus.Accepted, Db.Orders.AsNoTracking().Single(o => o.Id == id).Status);
        d = await Orders.RecordFactoryNode(id, Node(Received, null, null, ten.Take(9).ToArray()), Wang);
        Assert.Equal(9, d.Factory!.Nodes[1].Photos.Count);

        await Fails(40000, "请至少上传 1 张寄回照片", () => Orders.RecordFactoryNode(id, Node(Back, "顺丰", "SF2"), Wang));
        await Fails(40000, "请填写快递公司", () => Orders.RecordFactoryNode(id, Node(Back, null, "SF2", Photo(Wang)), Wang));
        await Fails(40000, "请填写快递单号", () => Orders.RecordFactoryNode(id, Node(Back, "顺丰", null, Photo(Wang)), Wang));
    }

    [Fact]
    public async Task FactoryPermissions()
    {
        var liu = U("刘工");
        var other = U("孙磊");
        var id = await NewFactory("返厂权限客户");
        await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { liu.Id } }, Sup);
        const string m15 = "只有建单人、负责工程师或主管可以记录此节点";
        const string m24 = "只有负责工程师或主管可以记录此节点";

        // 协助人、无关销售都不能记录
        await Fails(40300, m15, () => Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", "SF1"), liu));
        await Fails(40300, m15, () => Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", "SF1"), other));
        await Fails(40300, m24, () => Orders.RecordFactoryNode(id, Node(Received, null, null, Photo(liu)), liu));
        Assert.DoesNotContain("factory", (await Orders.Detail(id, liu)).Actions);
        Assert.Equal(new[] { "suggestPrice" }, (await Orders.Detail(id, other)).Actions);
        Assert.All((await Orders.Detail(id, other)).Factory!.Nodes, n => Assert.False(n.CanRecord || n.CanEdit));
        // 协助人、销售都能看到时间线
        Assert.Equal(5, (await Orders.Detail(id, liu)).Factory!.Nodes.Count);

        // 建单销售只能记 1、5
        await Fails(40300, m24, () => Orders.RecordFactoryNode(id, Node(Received, null, null, Photo(Sales)), Sales));
        await Fails(40300, m24, () => Orders.RecordFactoryNode(id, Node(Back, "顺丰", "SF2", Photo(Sales)), Sales));
        var d = await Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", "SF1"), Sales);
        Assert.Equal(new[] { true, false, false, false, false }, d.Factory!.Nodes.Select(n => n.CanEdit));
        // 无关销售也不能更正
        await Fails(40300, m15, () => Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", "SF9"), other));

        await Orders.RecordFactoryNode(id, Node(Received, null, null, Photo(Wang)), Wang);
        await Orders.RecordFactoryNode(id, Node(Back, "顺丰", "SF2", Photo(Wang)), Wang);
        await Fails(40300, m15, () => Orders.RecordFactoryNode(id, Node(Signed), liu));
        await Fails(40300, m15, () => Orders.RecordFactoryNode(id, Node(Signed), other));
        d = await Orders.Detail(id, Sales);
        Assert.Equal(new[] { false, false, false, false, true }, d.Factory!.Nodes.Select(n => n.CanRecord));
        Assert.Equal(new[] { "suggestPrice", "factory" }, d.Actions);
        // 主管可记录任何节点；工程师建的客户直联单，建单工程师即「建单人」
        Assert.Equal("客户已签收", (await Orders.RecordFactoryNode(id, Node(Signed), Sup)).FactoryText);
        var direct = await NewPending("工程师直联返厂", ServiceType.Factory, creator: "刘工");
        Assert.Equal("客户已寄出", (await Orders.RecordFactoryNode(direct, Node(Shipped, "顺丰", "SF3"), liu)).FactoryText);
    }

    [Fact]
    public async Task FactoryCompleteRequiresShippedBack()
    {
        var id = await NewFactory("返厂完工客户");
        await Fails(40900, "返厂工单记录「工厂收货」后自动开始处理", () => Orders.Start(id, Wang));
        await Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", "SF1"), Sales);
        await Orders.RecordFactoryNode(id, Node(Received, null, null, Photo(Wang)), Wang);
        var d = await Orders.Detail(id, Wang);
        Assert.DoesNotContain("complete", d.Actions);
        Assert.DoesNotContain("start", d.Actions);
        Assert.DoesNotContain("checkin", d.Actions);
        await Fails(40900, "返厂工单请先记录「寄回客户」再提交完工", () => Orders.Complete(id, Done(Wang, false), Wang));

        // 返厂单永不打卡
        await Fails(40900, "远程/返厂工单无需打卡", () => Punches.Context(Wang, id));
        await Fails(40900, "远程/返厂工单无需打卡", () => Arrive(Wang, id));

        await Orders.RecordFactoryNode(id, Node(Back, "顺丰", "SF2", Photo(Wang)), Wang);
        Assert.Equal(OrderStatus.Submitted, (await Orders.Complete(id, Done(Wang, false), Wang)).Status);
    }

    [Fact]
    public async Task FactoryEditKeepsStatusAndReplacesPhotos()
    {
        var id = await NewFactory("返厂更正客户");
        await Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", "SF1"), Sales);
        var r1 = Photo(Wang);
        await Orders.RecordFactoryNode(id, Node(Received, null, null, r1), Wang);
        var firstTime = Clock.Now;
        var p1 = Photo(Wang, FileCategory.FactoryShipPhoto);
        var p2 = Photo(Wang, FileCategory.FactoryShipPhoto);
        await Orders.RecordFactoryNode(id, Node(Back, "顺丰", "SF2", p1, p2), Wang);

        // 更正寄回：覆盖字段，照片按最终全集替换，状态不变，不再通知
        Tick(5);
        var count = Notifier.Sent.Count;
        var p3 = Photo(Wang, FileCategory.FactoryShipPhoto);
        var edit = Node(Back, "德邦", "DB 777", p2, p3);
        edit.Note = "更换承运商";
        var d = await Orders.RecordFactoryNode(id, edit, Wang);
        Assert.Equal(OrderStatus.InProgress, d.Status);
        Assert.Equal("王工 更正寄回客户（德邦 DB777）", d.Logs.Last().Text);
        Assert.Equal(LogKind.Factory, d.Logs.Last().Kind);
        Assert.Equal(count, Notifier.Sent.Count);
        var node = d.Factory!.Nodes[3];
        Assert.Equal("德邦", node.Courier);
        Assert.Equal("DB777", node.TrackingNo);
        Assert.Equal("更换承运商", node.Note);
        Assert.Equal(new[] { p2, p3 }, node.Photos.Select(f => f.Id));
        Assert.Equal(firstTime, node.Time);          // 首次记录时间不变
        Assert.Equal("王工", node.Operator!.Name);
        Assert.Null(Db.Files.AsNoTracking().Single(f => f.Id == p1).OrderId);
        Assert.Equal(new[] { r1 }, d.Factory.Nodes[1].Photos.Select(f => f.Id)); // 其他节点照片不受影响
        var row = Db.FactoryRecords.AsNoTracking().Single(r => r.OrderId == id && r.Node == Back);
        Assert.Equal(Clock.Now, row.UpdatedAt);
        Assert.Equal(Wang.Id, row.UpdatedById);
        Assert.Equal(3, Db.FactoryRecords.AsNoTracking().Count(r => r.OrderId == id)); // 每单每节点最多 1 行

        // 他人更正：已挂在本单同类别的照片不校验上传人
        d = await Orders.RecordFactoryNode(id, Node(Back, "德邦", "DB778", p2, p3), Sup);
        Assert.Equal("赵主管 更正寄回客户（德邦 DB778）", d.Logs.Last().Text);
        Assert.Equal("王工", d.Factory!.Nodes[3].Operator!.Name);
        // 更正不检查前置：工厂收货已记录后仍可更正客户寄出
        d = await Orders.RecordFactoryNode(id, Node(Shipped, "中通", "ZT1"), Sales);
        Assert.Equal("王敏 更正客户寄出（中通 ZT1）", d.Logs.Last().Text);
        d = await Orders.RecordFactoryNode(id, Node(Received, null, null, r1), Wang);
        Assert.Equal("王工 更正工厂收货", d.Logs.Last().Text);
        Assert.Equal(OrderStatus.InProgress, d.Status);

        // 暂停时也可以更正；闭环后不能
        await Orders.Hold(id, HoldBody("WaitCustomer"), Sup);
        await Orders.RecordFactoryNode(id, Node(Back, "德邦", "DB779", p2), Wang);
        await Orders.Resume(id, Sup);
        await Orders.Complete(id, Done(Wang, false), Wang);
        await Orders.RecordFactoryNode(id, Node(Signed), Sales);
        d = await Orders.RecordFactoryNode(id, Node(Signed, null, null, Photo(Sales, FileCategory.FactorySignPhoto)), Sales);
        Assert.Equal("王敏 更正客户签收", d.Logs.Last().Text);
        Assert.Single(d.Factory!.Nodes[4].Photos);
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        await Orders.Close(id, Sup);
        await Fails(40900, "工单当前为「已闭环」，不能更正返厂记录", () => Orders.RecordFactoryNode(id, Node(Back, "德邦", "DB780", p2), Sup));
    }

    [Fact]
    public async Task FactoryHoldInteraction()
    {
        var id = await NewFactory("返厂暂停客户");
        var d = await Orders.Hold(id, HoldBody("WaitCustomer", "客户还没寄"), Sup);
        Assert.Equal(2, d.ProgressStep);
        await Fails(40900, "工单已暂停搁置，恢复后才能继续返厂流程", () => Orders.RecordFactoryNode(id, Node(Received, null, null, Photo(Wang)), Wang));
        await Fails(40900, "工单已暂停搁置，恢复后才能继续返厂流程", () => Orders.RecordFactoryNode(id, Node(Back, "顺丰", "SF", Photo(Wang)), Sup));
        // 暂停时仍可记录客户寄出
        d = await Orders.RecordFactoryNode(id, Node(Shipped, "顺丰", "SF1"), Sales);
        Assert.Equal(OrderStatus.OnHold, d.Status);
        Assert.Equal("客户已寄出", d.FactoryText);
        Assert.Equal(OrderStatus.OnHold, d.Logs.Last().ToStatus);
        Assert.False((await Orders.Detail(id, Wang)).Factory!.Nodes[1].CanRecord);

        d = await Orders.Resume(id, Sup);
        Assert.Equal(OrderStatus.Accepted, d.Status);
        d = await Orders.RecordFactoryNode(id, Node(Received, null, null, Photo(Wang)), Wang);
        Assert.Equal(OrderStatus.InProgress, d.Status);
        // 处理中暂停 → 恢复回处理中；已寄回后暂停期间可记录签收
        await Orders.RecordFactoryNode(id, Node(Back, "顺丰", "SF2", Photo(Wang)), Wang);
        await Orders.Hold(id, HoldBody("WaitThirdParty"), Sup);
        Assert.Equal(new[] { "factory" }, (await Orders.Detail(id, Wang)).Actions);
        d = await Orders.RecordFactoryNode(id, Node(Signed), Wang);
        Assert.Equal("客户已签收", d.FactoryText);
        Assert.Equal(OrderStatus.InProgress, (await Orders.Resume(id, Sup)).Status);
    }

    [Fact]
    public async Task CloseHintWhenNotSigned()
    {
        var id = await ShippedBackOrder("返厂未签收客户");
        await Orders.Complete(id, Done(Wang, false), Wang);
        await Orders.SetPrice(id, new PriceRequest { Price = 300 }, Sup);
        var d = await Orders.Detail(id, Sup);
        Assert.Equal(new[] { "客户签收尚未记录" }, d.CloseHints);
        Assert.Null(d.CloseBlockedReason);
        Assert.Equal(new[] { "reject", "close", "setPrice", "factory" }, d.Actions);
        Assert.Empty((await Orders.Detail(id, Sales)).CloseHints); // 仅主管
        // 闭环不要求客户签收
        d = await Orders.Close(id, Sup);
        Assert.Equal(OrderStatus.Closed, d.Status);
        Assert.Equal(new[] { "done", "done", "done", "done", "todo" }, States(d)); // 已闭环没有 current
        await Fails(40900, "工单当前为「已闭环」，不能记录「客户签收」", () => Orders.RecordFactoryNode(id, Node(Signed), Sales));

        // 非返厂单没有提示
        Assert.Empty((await Orders.Detail(OrderId("合肥某研究所"), Sup)).CloseHints);
        // 种子：已闭环返厂单走完 4 个节点
        var seeded = await Orders.Detail(OrderId("苏州某药企"), Sup);
        Assert.Equal(new[] { "done", "done", "done", "done", "done" }, States(seeded));
        Assert.Equal("SF1100200399", seeded.Factory!.Nodes[3].TrackingNo);
        Assert.Equal(2, seeded.Files.FactoryPhotos.Count);
    }
}
