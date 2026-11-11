using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.1 迁移、主管待办、概况、查询参数、种子数据、文字规则</summary>
public sealed class V11MiscTests : V11TestBase
{
    [Fact]
    public void MigrationUpgradeKeepsExistingOrdersPriced()
    {
        // 先迁移到 v1.0 的最后一个迁移，用原始 SQL 写入「已有数据」，再升级到最新
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        db.GetService<IMigrator>().Migrate("AddEmployeePassword");
        Assert.Contains(db.Database.GetPendingMigrations(), m => m.EndsWith("V11_PriceHoldAssistFactory"));

        db.Database.ExecuteSqlRaw("""
            INSERT INTO Employees (Id, Name, Phone, Role, Department, Enabled, TokenVersion, CreatedAt)
            VALUES (1, '老员工', '13800000001', 'Engineer', '售后部', 1, 0, '2026-10-01 09:00:00');
            INSERT INTO WorkOrders (Id, OrderNo, Status, Source, ProblemType, Urgency, ServiceType, CustomerName, ContactName, ContactPhone,
                Address, City, Region, DeviceModel, DeviceSn, SatDate, SatSigned, FaultTime, Symptom, CustomerTried, SiteEnvironment,
                Price, CreatorId, AssigneeId, CreatedAt, HoldReason)
            VALUES (1, 'SH-20261101-001', 'OnHold', 'SalesReferral', 'Fault', 'Normal', 'Onsite', '老客户', '张工', '13800000000',
                '苏州市', '苏州', '苏州市', 'HTR-500', 'SN1', '2024-08', 1, '昨天', '不加热', '重启', '通风橱',
                1800.0, 1, 1, '2026-11-01 09:00:00', '等待备件');
            INSERT INTO WorkOrders (Id, OrderNo, Status, Source, ProblemType, Urgency, ServiceType, CustomerName, ContactName, ContactPhone,
                Address, City, Region, DeviceModel, DeviceSn, SatDate, SatSigned, FaultTime, Symptom, CustomerTried, SiteEnvironment,
                Price, CreatorId, CreatedAt)
            VALUES (2, 'SH-20261101-002', 'Pending', 'SalesReferral', 'Fault', 'Normal', 'Factory', '保内老客户', '李工', '13800000000',
                '苏州市', '苏州', '苏州市', 'HTM-100', 'SN2', '2024-08', 1, '昨天', '异响', '无', '实验室',
                0.0, 1, '2026-11-01 10:00:00');
            INSERT INTO StatusLogs (OrderId, ActorId, Text, Time, ToStatus) VALUES (1, 1, '老员工 建单', '2026-11-01 09:00:00', 'Pending');
            INSERT INTO Punches (OrderId, EngineerId, Kind, Time, Address, OutOfRange, IsSupplement)
            VALUES (1, 1, 'Arrive', '2026-11-01 10:00:00', '', 0, 0), (1, 1, 'Leave', '2026-11-01 12:00:00', '', 0, 0);
            """);

        db.Database.Migrate();
        Assert.Empty(db.Database.GetPendingMigrations());

        var orders = db.Orders.OrderBy(o => o.Id).ToList();
        Assert.All(orders, o =>
        {
            Assert.True(o.PriceSet); // 已有数据全部视为已定价（含保内 0 元）
            Assert.Null(o.PriceSetAt);
            Assert.Null(o.HoldReasonType);
            Assert.Null(o.HoldFromStatus);
            Assert.Null(o.HoldAt);
            Assert.Null(o.HoldById);
        });
        Assert.Equal(1800, orders[0].Price);
        Assert.Equal("等待备件", orders[0].HoldReason);
        Assert.Null(db.StatusLogs.Single().Kind);
        Assert.Equal(2, db.Punches.Count());
        Assert.Equal(0, db.OrderAssistants.Count());
        Assert.Equal(0, db.OrderRequests.Count());
        Assert.Equal(0, db.PriceSuggestions.Count());
        Assert.Equal(0, db.FactoryRecords.Count());

        // 升级后的旧数据可以直接走 v1.1 的映射与流程
        var clock = new FixedClock(new DateTime(2026, 11, 6, 10, 0, 0));
        var view = new OrderViewBuilder(db, clock, Files);
        var sup = new Huotong.AfterSales.Api.Infrastructure.CurrentUser(99, Role.Supervisor);
        var held = view.BuildDetail(1, sup).GetAwaiter().GetResult();
        Assert.Equal(1800, held.Price);
        Assert.True(held.PriceSet);
        Assert.Equal(HoldReasonType.Other, held.Hold!.ReasonType);
        Assert.Equal("等待备件", held.Hold.Text);
        Assert.Equal(OrderStatus.InProgress, held.Hold.FromStatus);
        Assert.Equal(3, held.ProgressStep);
        Assert.Equal(120, held.TotalMinutes);
        Assert.Equal(LogKind.Status, held.Logs.Single().Kind);
        var factory = view.BuildDetail(2, sup).GetAwaiter().GetResult();
        Assert.Equal(0, factory.Price);
        Assert.Equal("待客户寄出", factory.FactoryText);
        Assert.Equal(5, factory.Factory!.Nodes.Count);

        // 新写入的行仍能显式写 false，并可使用新表
        db.Orders.Add(new WorkOrder { OrderNo = "SH-20261106-001", CustomerName = "新单", CreatorId = 1, CreatedAt = clock.Now, PriceSet = false });
        db.OrderAssistants.Add(new OrderAssistant { OrderId = 1, EngineerId = 1, AddedById = 1, AddedAt = clock.Now });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        Assert.False(db.Orders.Single(o => o.OrderNo == "SH-20261106-001").PriceSet);
    }

    [Fact]
    public async Task SupervisorTodosOrderAndNewTypes()
    {
        var todos = (await Home.Supervisor(Sup)).Todos;
        // 固定顺序
        var order = new[] { "HoldRequest", "AssistantRequest", "Unassigned", "Submitted", "PriceSuggestion", "MissingPunch", "DistanceAbnormal" };
        Assert.Equal(order, todos.Select(t => t.Type).Distinct());
        Assert.Equal(todos.Select(t => Array.IndexOf(order, t.Type)).OrderBy(i => i), todos.Select(t => Array.IndexOf(order, t.Type)));

        var hold = todos.Single(t => t.Type == "HoldRequest");
        Assert.Equal(OrderId("南通某试剂厂"), hold.OrderId);
        Assert.Equal(Db.OrderRequests.AsNoTracking().Single(r => r.Type == OrderRequestType.Hold).Id, hold.RequestId);
        Assert.Equal("南通某试剂厂", hold.Title);
        Assert.False(hold.Urgent);
        Assert.Equal("李工 申请暂停 · 等客户：客户要求下周再安排停机", hold.Subtitle);

        var assist = todos.Single(t => t.Type == "AssistantRequest");
        Assert.Equal(OrderId("南京某检测中心"), assist.OrderId);
        Assert.NotNull(assist.RequestId);
        Assert.Equal("陈工 申请添加协助人 王工：釜盖拆装需要两人", assist.Subtitle);

        var price = todos.Single(t => t.Type == "PriceSuggestion");
        Assert.Equal(OrderId("无锡某制药企业"), price.OrderId);
        Assert.Null(price.RequestId);
        Assert.True(price.Urgent);
        Assert.Equal("无锡某制药企业", price.Title);
        Assert.Equal("王敏 建议价格 ¥2,400（老客户，按上次价格）", price.Subtitle);

        // 原有 4 类 requestId 为 null
        Assert.All(todos.Where(t => t.Type is "Unassigned" or "Submitted" or "MissingPunch" or "DistanceAbnormal"), t => Assert.Null(t.RequestId));
        Assert.Equal("刘工 11月5日提交完工，资料已上传", todos.Single(t => t.Type == "Submitted").Subtitle);

        // 多条申请按创建时间正序；定价后价格建议待办消失
        Tick();
        var id = await NewAccepted("待办客户");
        await Orders.RequestHold(id, HoldBody("WaitParts"), U("王工"));
        await Orders.SetPrice(OrderId("无锡某制药企业"), new PriceRequest { Price = 2400 }, Sup);
        todos = (await Home.Supervisor(Sup)).Todos;
        Assert.Equal(new[] { "南通某试剂厂", "待办客户" }, todos.Where(t => t.Type == "HoldRequest").Select(t => t.Title));
        Assert.Equal("王工 申请暂停 · 等备件", todos.Last(t => t.Type == "HoldRequest").Subtitle);
        Assert.DoesNotContain(todos, t => t.Type == "PriceSuggestion");
    }

    [Fact]
    public async Task OverviewCountsV11()
    {
        var o = await Admin.Overview();
        Assert.Equal(1, o.OnHold);            // 常州某材料厂
        Assert.Equal(2, o.PendingRequests);   // 南通暂停申请 + 南京协助人申请
        Assert.Equal(1, o.Unpriced);          // 无锡某制药企业
        Assert.Equal(1, o.PriceSuggestions);
        Assert.Equal(3, o.Pending);
        Assert.Equal(3, o.InProgress);        // 江苏、南京、常熟

        await Orders.SetPrice(OrderId("无锡某制药企业"), new PriceRequest { Price = 2400 }, Sup);
        var id = await NewPending("概况未定价");
        await Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 100 }, U("王敏"));
        await Orders.Void(await NewPending("作废的未定价单"), new ReasonRequest(), Sup);
        await Orders.Resume(OrderId("常州某材料厂"), Sup);
        var req = Db.OrderRequests.AsNoTracking().Single(r => r.Type == OrderRequestType.Hold);
        await Orders.RejectRequest(req.OrderId, req.Id, new ReplyRequest(), Sup);
        o = await Admin.Overview();
        Assert.Equal(0, o.OnHold);
        Assert.Equal(1, o.PendingRequests);
        Assert.Equal(1, o.Unpriced);          // 作废的不计
        Assert.Equal(1, o.PriceSuggestions);
    }

    [Fact]
    public async Task OrdersQueryPriceSetAndPendingRequestFilters()
    {
        var unpriced = await Orders.Query(new OrderQuery { PriceSet = "false" }, Sup);
        Assert.Equal("无锡某制药企业", Assert.Single(unpriced.Items).CustomerName);
        Assert.Null(unpriced.Items[0].Price);
        Assert.False(unpriced.Items[0].PriceSet);
        Assert.Equal(12, unpriced.Counts["all"]); // counts 规则不变
        Assert.Equal(11, (await Orders.Query(new OrderQuery { PriceSet = "true" }, Sup)).Total);
        Assert.Equal(12, (await Orders.Query(new OrderQuery(), Sup)).Total);
        await Fails(40000, "定价筛选不正确", () => Orders.Query(new OrderQuery { PriceSet = "maybe" }, Sup));

        var pending = await Orders.Query(new OrderQuery { PendingRequest = true }, Sup);
        Assert.Equal(new[] { "南京某检测中心", "南通某试剂厂" }, pending.Items.Select(i => i.CustomerName).OrderBy(x => x));
        Assert.All(pending.Items, i => Assert.Equal(1, i.PendingRequestCount));
        Assert.Equal(12, (await Orders.Query(new OrderQuery { PendingRequest = false }, Sup)).Total);

        // 销售可用这两个参数，且能看到价格
        var sales = await Orders.Query(new OrderQuery { PriceSet = "true", PendingRequest = true, Status = "InProgress" }, U("王敏"));
        Assert.Equal("南京某检测中心", Assert.Single(sales.Items).CustomerName);
        Assert.Equal(960, sales.Items[0].Price);
        Assert.True(sales.Items[0].PriceSet);
    }

    [Fact]
    public async Task SeedDataExercisesV11Features()
    {
        // 暂停搁置 + 原因 + 协助人及其打卡
        var held = await Orders.Detail(OrderId("常州某材料厂"), Sup);
        Assert.Equal(OrderStatus.OnHold, held.Status);
        Assert.Equal("等备件：进口密封件预计 11 月中旬到货", held.HoldText);
        Assert.Equal(3, held.ProgressStep);
        Assert.Equal("赵主管", held.Hold!.By!.Name);
        Assert.Equal(new[] { ("刘工", ParticipantRole.Lead, 480), ("王工", ParticipantRole.Assistant, 420) },
            held.Participants.Select(p => (p.User.Name, p.Role, p.TotalMinutes)));
        Assert.Equal(900, held.TotalMinutes);
        Assert.Equal(ParticipantRole.Assistant, (await Orders.Mine(U("王工"), "doing")).Items.Single(i => i.Id == held.Id).MyRole);
        Assert.Contains((await Home.Engineer(U("王工"))).OnHold, i => i.Id == held.Id);

        // 待审批的暂停申请、协助人申请
        Assert.Equal(RequestStatus.Pending, (await Orders.Detail(OrderId("南通某试剂厂"), Sup)).Requests.Single().Status);
        Assert.Equal("添加协助人 王工：釜盖拆装需要两人", (await Orders.Detail(OrderId("南京某检测中心"), Sup)).Requests.Single().Text);

        // 返厂进行中
        var factory = await Orders.Detail(OrderId("常熟某研究院"), U("王工"));
        Assert.Equal(new[] { "done", "done", "current", "current", "todo" }, States(factory));
        Assert.Equal("检测/维修中", factory.FactoryText);
        Assert.Equal(new[] { "appoint", "factory", "requestHold", "requestAssistant", "transfer" }, factory.Actions);

        // 未定价 + 价格建议
        var unpriced = await Orders.Detail(OrderId("无锡某制药企业"), Sup);
        Assert.False(unpriced.PriceSet);
        Assert.Null(unpriced.Price);
        Assert.True(unpriced.PriceSuggestion!.Pending);
        Assert.Equal(2400, unpriced.PriceSuggestion.Amount);
        // 其余工单全部已定价
        Assert.Equal(11, Db.Orders.AsNoTracking().Count(o => o.PriceSet));
    }

    [Theory]
    [InlineData(null, null, "其他")]
    [InlineData(null, "等待备件", "等待备件")]
    [InlineData(HoldReasonType.Other, "客户停产", "客户停产")]
    [InlineData(HoldReasonType.Other, " ", "其他")]
    [InlineData(HoldReasonType.WaitParts, null, "等备件")]
    [InlineData(HoldReasonType.WaitParts, "轴承预计周五到", "等备件：轴承预计周五到")]
    [InlineData(HoldReasonType.WaitCustomer, "", "等客户")]
    [InlineData(HoldReasonType.WaitThirdParty, "计量院", "等第三方：计量院")]
    public void HoldTextRule(HoldReasonType? type, string? note, string expected) => Assert.Equal(expected, EnumText.HoldText(type, note));

    [Theory]
    [InlineData(0, "保内免费")]
    [InlineData(1800, "¥1,800")]
    [InlineData(1800.5, "¥1,800.50")]
    [InlineData(1234567.89, "¥1,234,567.89")]
    [InlineData(50, "¥50")]
    public void MoneyRule(double amount, string expected) => Assert.Equal(expected, EnumText.Money((decimal)amount));

    [Fact]
    public void EnumTextsAndJsonNames()
    {
        Assert.Equal(new[] { "等客户", "等备件", "等第三方", "其他" }, Enum.GetValues<HoldReasonType>().Select(EnumText.HoldReason));
        Assert.Equal(new[] { "客户寄出", "工厂收货", "检测/维修", "寄回客户", "客户签收" }, Enum.GetValues<FactoryNode>().Select(EnumText.Factory));
        Assert.Equal(new[] { "负责人", "协助人", "已退出" }, Enum.GetValues<ParticipantRole>().Select(EnumText.Participant));
        Assert.Equal(new[] { "待审批", "已同意", "已驳回", "已撤销" }, Enum.GetValues<RequestStatus>().Select(EnumText.RequestStatusName));
        Assert.Equal("待客户寄出", EnumText.FactoryText(Array.Empty<FactoryNode>()));
        Assert.Equal("已寄回客户", EnumText.FactoryText(new[] { FactoryNode.FactoryReceived, FactoryNode.ShippedBack }));

        var json = Json(new OrderDetail { HoldReasonType = HoldReasonType.WaitParts, MyRole = ParticipantRole.Lead });
        foreach (var key in new[] { "price", "priceSet", "assistants", "myRole", "myMinutes", "myOnSiteSince", "progressStep", "holdReasonType",
                     "holdText", "holdFromStatus", "pendingRequestCount", "factoryText", "priceSetAt", "priceSuggestion", "holdReason", "hold",
                     "requests", "participants", "factory", "closeBlockedReason", "closeHints" })
            Assert.Contains($"\"{key}\":", json);
        Assert.Contains("\"holdReasonType\":\"WaitParts\"", json);
        Assert.Contains("\"myRole\":\"Lead\"", json);
        Assert.Contains("\"factoryPhotos\":[]", json);
        Assert.Contains("\"requestId\":null", Json(new TodoDto()));
        Assert.Contains("\"kind\":\"Status\"", Json(new StatusLogDto()));
    }
}
