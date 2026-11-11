using ClosedXML.Excel;
using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.3 测试公共夹具：新请求体（devices）、转交的便捷方法、不变量 I1 / I2 的公共断言</summary>
public abstract class V13TestBase : V12TestBase
{
    protected const string FormerDenied = "你已退出本单，只能查看";

    protected static DeviceItem Dev(string? name, string? sn, string? model) => new() { Name = name, Sn = sn, Model = model };

    /// <summary>v1.3 客户端的建单请求体：不传 deviceModel / deviceSn，传 devices</summary>
    protected static CreateOrderRequest NewOrder13(string customer, ServiceType service = ServiceType.Onsite, params DeviceItem[] devices)
    {
        var r = NewOrder(customer, service);
        r.DeviceModel = null;
        r.DeviceSn = null;
        r.SatDate = "";
        r.ProjectNo = "HT-2026-001";
        r.Devices = devices.ToList();
        return r;
    }

    protected static TransferRequest To(CurrentUser to, string? reason = "电气部分我搞不定，需要更熟的同事") => new() { EngineerId = to.Id, Reason = reason };

    /// <summary>发起转交并返回转交 id</summary>
    protected async Task<int> StartTransfer(int orderId, CurrentUser from, CurrentUser to, string reason = "电气部分我搞不定，需要更熟的同事") =>
        (await Orders.Transfer(orderId, To(to, reason), from)).PendingTransfer!.Id;

    protected OrderTransfer TransferRow(int id) => Db.OrderTransfers.AsNoTracking().Single(t => t.Id == id);

    protected WorkOrder OrderRow(int id) => Db.Orders.AsNoTracking().Single(o => o.Id == id);

    protected List<OrderExit> ExitsOf(int orderId) => Db.OrderExits.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.Id).ToList();

    /// <summary>
    /// 不变量（CHANGES-v1.3 §1.4），对全库检查：
    /// I1：PendingTransferToId != null ⇔ 本单恰有一行 Pending 转交，且接手人 / 发起人（= 当前负责人）/ 工单状态都对得上；
    /// I2：退出记录里的人一定不是该单当前的负责人或协助人。
    /// </summary>
    protected void AssertInvariants()
    {
        var orders = Db.Orders.AsNoTracking().Include(o => o.Assistants).Include(o => o.Exits).ToList();
        var pending = Db.OrderTransfers.AsNoTracking().Where(t => t.Status == TransferStatus.Pending).ToList();
        foreach (var o in orders)
        {
            var mine = pending.Where(t => t.OrderId == o.Id).ToList();
            if (o.PendingTransferToId == null) Assert.Empty(mine);
            else
            {
                var t = Assert.Single(mine);
                Assert.Equal(o.PendingTransferToId, t.ToEngineerId);
                Assert.Equal(o.AssigneeId, t.FromEngineerId);
                Assert.Contains(o.Status, new[] { OrderStatus.Accepted, OrderStatus.InProgress, OrderStatus.OnHold });
            }
            foreach (var x in o.Exits)
            {
                Assert.NotEqual(o.AssigneeId, x.EngineerId);
                Assert.DoesNotContain(o.Assistants, a => a.EngineerId == x.EngineerId);
            }
            Assert.Equal(o.Exits.Count, o.Exits.Select(x => x.EngineerId).Distinct().Count());
        }
        Assert.All(pending, t => Assert.Contains(orders, o => o.Id == t.OrderId && o.PendingTransferToId == t.ToEngineerId));
    }
}

/// <summary>v1.3 设备资料：多台设备、项目编号必填、SAT 信息可改、搜索 / 导出 / 最近客户、迁移回填（CHANGES-v1.3 §3、§11.2 第 1–10、39 项）</summary>
public sealed class V13DeviceTests : V13TestBase
{
    private CurrentUser Wm => U("王敏");

    [Fact]
    public async Task CreateWithMultipleDevicesDropsEmptyRows()
    {
        var req = NewOrder13("多设备客户", ServiceType.Onsite,
            Dev(" 高压反应釜 ", " SN2405009 ", "HTR-250"), Dev("  ", "", null), Dev("搅拌器", "", "HTS-80"));
        req.Devices!.Insert(1, null!); // null 元素丢弃
        var d = await Orders.Create(req, Wm);

        Assert.Equal(2, d.Devices.Count);
        Assert.Equal(new[] { 1, 2 }, d.Devices.Select(x => x.Seq));
        Assert.Equal(new[] { "高压反应釜", "搅拌器" }, d.Devices.Select(x => x.Name));
        Assert.Equal(new[] { "SN2405009", "" }, d.Devices.Select(x => x.Sn));
        Assert.Equal(new[] { "HTR-250", "HTS-80" }, d.Devices.Select(x => x.Model));
        Assert.Equal(new[] { "高压反应釜 HTR-250", "搅拌器 HTS-80" }, d.Devices.Select(x => x.Title));
        Assert.Equal("高压反应釜 HTR-250 等 2 台", d.DeviceText);
        Assert.Equal("HT-2026-001", d.ProjectNo);
        // 兼容列（旧客户端读）= 第一台
        Assert.Equal("高压反应釜 HTR-250", d.DeviceModel);
        Assert.Equal("SN2405009", d.DeviceSn);
        var row = OrderRow(d.Id);
        Assert.Equal("高压反应釜 HTR-250 等 2 台", row.DeviceText);
        Assert.Equal("高压反应釜 HTR-250", row.DeviceModel);
        Assert.Equal("SN2405009", row.DeviceSn);
        Assert.Equal(2, Db.OrderDevices.Count(x => x.OrderId == d.Id));
        // 设备不单独写状态记录
        Assert.Equal("王敏 建单", Assert.Single(d.Logs).Text);

        // 列表项与详情同一口径
        var item = (await Orders.Created(Wm, "all")).Items.Single(i => i.Id == d.Id);
        Assert.Equal("高压反应釜 HTR-250 等 2 台", item.DeviceText);
        Assert.Equal("HT-2026-001", item.ProjectNo);
        Assert.Equal("高压反应釜 HTR-250", item.DeviceModel);
    }

    [Fact]
    public async Task CreateWithoutDevicesOrDate()
    {
        var req = NewOrder13("没有设备的客户");
        req.SatSigned = false;
        req.SatDate = null;
        var d = await Orders.Create(req, Wm);
        Assert.Empty(d.Devices);
        Assert.Equal("未填设备", d.DeviceText);
        Assert.Equal("", d.DeviceModel);
        Assert.Equal("", d.DeviceSn);
        Assert.Equal("", d.SatDate);
        Assert.False(d.SatSigned);
        Assert.Equal("未完成", d.SatText);
        Assert.Equal("发货时间", d.SatDateLabel);
        Assert.Equal("新产品", d.ProductStage);
        Assert.True(d.CanEditSat);
        Assert.Equal(0, Db.OrderDevices.Count(x => x.OrderId == d.Id));

        // yyyy-MM-dd 与旧格式 yyyy-MM 都接受；可以是将来的日期
        var dated = NewOrder13("带日期");
        dated.SatDate = " 2030-03-15 ";
        var dd = await Orders.Create(dated, Wm);
        Assert.Equal("2030-03-15", dd.SatDate);
        Assert.Equal("已完成", dd.SatText);
        Assert.Equal("SAT 完成时间", dd.SatDateLabel);
        Assert.Equal("老产品", dd.ProductStage);
    }

    [Fact]
    public async Task OldClientPayloadSynthesizesOneDevice()
    {
        // V11TestBase.NewOrder 就是旧请求体：deviceModel / deviceSn / satDate yyyy-MM，不传 devices
        var d = await Orders.Create(NewOrder("旧客户端建单"), Wm);
        var dev = Assert.Single(d.Devices);
        Assert.Equal((1, "", "SN1", "HTR-500", "HTR-500"), (dev.Seq, dev.Name, dev.Sn, dev.Model, dev.Title));
        Assert.Equal("HTR-500", d.DeviceText);
        Assert.Equal("HTR-500", d.DeviceModel);
        Assert.Equal("SN1", d.DeviceSn);
        Assert.Equal("2024-08", d.SatDate);
        Assert.True(d.SatSigned);

        // 只有序列号
        var snOnly = NewOrder("只有序列号");
        snOnly.DeviceModel = " ";
        var s = await Orders.Create(snOnly, Wm);
        Assert.Equal("SN1", Assert.Single(s.Devices).Title);
        Assert.Equal("SN1", s.DeviceText);
        Assert.Equal("SN1", s.DeviceModel);

        // 两项都不传：成功、没有设备（旧表单里它们曾是必填，现在不拦）
        var none = NewOrder("旧客户端没填设备");
        none.DeviceModel = null;
        none.DeviceSn = null;
        var n = await Orders.Create(none, Wm);
        Assert.Empty(n.Devices);
        Assert.Equal("未填设备", n.DeviceText);
    }

    [Fact]
    public async Task DevicesFieldWinsOverLegacyFields()
    {
        var req = NewOrder("两种都传"); // 带 deviceModel = HTR-500 / deviceSn = SN1
        req.Devices = new() { Dev("冷凝器", "SN-9", "HTC-20") };
        var d = await Orders.Create(req, Wm);
        Assert.Equal("冷凝器 HTC-20", Assert.Single(d.Devices).Title);
        Assert.Equal("冷凝器 HTC-20", d.DeviceText);
        Assert.Equal("SN-9", d.DeviceSn);

        // devices: [] = 新客户端明确表示没有设备，旧字段同样被忽略
        var empty = NewOrder("空数组");
        empty.Devices = new();
        var e = await Orders.Create(empty, Wm);
        Assert.Empty(e.Devices);
        Assert.Equal("未填设备", e.DeviceText);
        Assert.Equal("", e.DeviceModel);
    }

    [Fact]
    public async Task CreateValidationOfDeviceSection()
    {
        CreateOrderRequest R(Action<CreateOrderRequest> f)
        {
            var r = NewOrder13("校验", ServiceType.Onsite, Dev("釜", "SN", "M"));
            f(r);
            return r;
        }
        await Fails(40000, "请填写项目编号", () => Orders.Create(R(r => r.ProjectNo = null), Wm));
        await Fails(40000, "请填写项目编号", () => Orders.Create(R(r => r.ProjectNo = "  "), Wm));
        await Fails(40000, "项目编号不能超过 50 个字", () => Orders.Create(R(r => r.ProjectNo = new string('P', 51)), Wm));
        Assert.Equal(new string('P', 50), (await Orders.Create(R(r => r.ProjectNo = new string('P', 50)), Wm)).ProjectNo);

        await Fails(40000, "设备最多 20 台",
            () => Orders.Create(R(r => r.Devices = Enumerable.Range(1, 21).Select(i => Dev("釜" + i, "", "")).ToList()), Wm));
        // 全空的行不占名额：20 台 + 5 个空行 可以
        var twenty = R(r => r.Devices = Enumerable.Range(1, 20).Select(i => Dev("釜" + i, "", "")).Concat(Enumerable.Repeat(Dev("", " ", null), 5)).ToList());
        var d = await Orders.Create(twenty, Wm);
        Assert.Equal(20, d.Devices.Count);
        Assert.Equal("釜1 等 20 台", d.DeviceText);

        var long101 = new string('x', 101);
        await Fails(40000, "设备名称不能超过 100 个字", () => Orders.Create(R(r => r.Devices = new() { Dev(long101, "", "") }), Wm));
        await Fails(40000, "设备编号不能超过 100 个字", () => Orders.Create(R(r => r.Devices = new() { Dev("", long101, "") }), Wm));
        await Fails(40000, "设备型号不能超过 100 个字", () => Orders.Create(R(r => r.Devices = new() { Dev("", "", long101) }), Wm));
        var max = new string('x', 100);
        Assert.Equal($"{max} {max}", (await Orders.Create(R(r => r.Devices = new() { Dev(max, max, max) }), Wm)).DeviceText);

        await Fails(40000, "请选择是否已完成 SAT", () => Orders.Create(R(r => r.SatSigned = null), Wm));
        foreach (var bad in new[] { "2026-13-01", "abc", "2026-02-30", "2026/03/15", "2026-3-5", "20260315" })
        {
            await Fails(40000, "SAT 完成时间格式应为 yyyy-MM-dd", () => Orders.Create(R(r => { r.SatSigned = true; r.SatDate = bad; }), Wm));
            await Fails(40000, "发货时间格式应为 yyyy-MM-dd", () => Orders.Create(R(r => { r.SatSigned = false; r.SatDate = bad; }), Wm));
        }
        // 顺序：项目编号 → 设备 → SAT → 发生时间
        await Fails(40000, "请填写项目编号", () => Orders.Create(R(r => { r.ProjectNo = ""; r.SatSigned = null; r.FaultTime = null; }), Wm));
        await Fails(40000, "请选择是否已完成 SAT", () => Orders.Create(R(r => { r.SatSigned = null; r.FaultTime = null; }), Wm));
        await Fails(40000, "现场工单请在地图上选择客户地址", () => Orders.Create(R(r => { r.Latitude = null; r.ProjectNo = null; }), Wm));
    }

    [Fact]
    public void TitleAndSummaryRules()
    {
        static OrderDevice D(string name, string sn, string model) => new() { Name = name, Sn = sn, Model = model };
        Assert.Equal("高压反应釜 HTR-250", DeviceRules.Title(D("高压反应釜", "SN1", "HTR-250")));
        Assert.Equal("高压反应釜", DeviceRules.Title(D("高压反应釜", "SN1", "")));
        Assert.Equal("HTR-500", DeviceRules.Title(D("", "SN1", "HTR-500")));
        Assert.Equal("SN2405011", DeviceRules.Title(D("", "SN2405011", "")));
        Assert.Equal("", DeviceRules.Title(D("", "", "")));

        Assert.Equal("未填设备", DeviceRules.Summary(new List<OrderDevice>()));
        Assert.Equal("HTR-500", DeviceRules.Summary(new[] { D("", "SN1", "HTR-500") }));
        Assert.Equal("高压反应釜 HTR-250 等 3 台",
            DeviceRules.Summary(new[] { D("高压反应釜", "SN1", "HTR-250"), D("搅拌器", "", "HTS-80"), D("", "SN3", "") }));
        Assert.Equal("SN9 等 2 台", DeviceRules.Summary(new[] { D("", "SN9", ""), D("x", "", "") }));
        Assert.Equal(20, DeviceRules.Max);
        Assert.Equal(100, DeviceRules.FieldMax);

        // 种子数据：南京某检测中心是多设备示例，其余每单 1 台
        var nj = Db.Orders.AsNoTracking().Include(o => o.Devices).Single(o => o.CustomerName == "南京某检测中心");
        Assert.Equal("高压反应釜 HTR-250 等 3 台", nj.DeviceText);
        Assert.Equal("HT-2024-031", nj.ProjectNo);
        Assert.Equal(new[] { "", "搅拌器", "冷凝器" }, nj.Devices.OrderBy(x => x.Seq).Select(x => x.Name));
        Assert.Equal("高压反应釜 HTR-250", nj.DeviceModel); // 兼容列不变
        Assert.Equal(14, Db.OrderDevices.Count());
        Assert.All(Db.Orders.AsNoTracking().Where(o => o.CustomerName != "南京某检测中心").ToList(), o => Assert.Equal(o.DeviceModel, o.DeviceText));
    }

    [Fact]
    public async Task SearchMatchesAnyDeviceAndProjectNo()
    {
        var req = NewOrder13("搜索客户", ServiceType.Onsite, Dev("主釜", "SN-MAIN-1", "HTR-900"), Dev("真空泵组", "SN-PUMP-77", "VP-3000X"));
        req.ProjectNo = "PRJ-SEARCH-88";
        var id = (await Orders.Create(req, Wm)).Id;
        async Task<int[]> Find(string k) => (await Orders.Query(new OrderQuery { Keyword = k, PageSize = 200 }, Sup)).Items.Select(i => i.Id).ToArray();

        Assert.Equal(new[] { id }, await Find("真空泵组"));      // 第二台的名称
        Assert.Equal(new[] { id }, await Find("SN-PUMP-77"));    // 第二台的编号
        Assert.Equal(new[] { id }, await Find(" VP-3000X "));    // 第二台的型号
        Assert.Equal(new[] { id }, await Find("PRJ-SEARCH"));    // 项目编号
        Assert.Equal(new[] { id }, await Find("搜索客户"));
        Assert.Empty(await Find("不存在的关键字"));
        // counts 同样受 keyword 影响
        Assert.Equal(1, (await Orders.Query(new OrderQuery { Keyword = "SN-PUMP-77" }, Sup)).Counts["all"]);

        // 种子工单按原型号 / 序列号仍能搜到；多设备种子按第 2、3 台也能搜到
        Assert.Equal("江苏某化工研究院", Assert.Single((await Orders.Query(new OrderQuery { Keyword = "SN2408017" }, Sup)).Items).CustomerName);
        Assert.Contains((await Orders.Query(new OrderQuery { Keyword = "HTR-500", PageSize = 200 }, Sup)).Items, i => i.CustomerName == "江苏某化工研究院");
        Assert.Equal("南京某检测中心", Assert.Single((await Orders.Query(new OrderQuery { Keyword = "SN2405011" }, Sup)).Items).CustomerName);
        Assert.Equal("南京某检测中心", Assert.Single((await Orders.Query(new OrderQuery { Keyword = "HT-2024-031" }, Sup)).Items).CustomerName);

        // 文件中心关键字：项目编号
        var center = new FileCenterService(Db, View);
        var page = await center.Query(new FileCenterQuery { Keyword = "HT-2024-031", PageSize = 200 }, Sup);
        Assert.True(page.Total > 0);
        Assert.All(page.Items, i => Assert.Equal("南京某检测中心", i.CustomerName));
    }

    [Fact]
    public async Task RecentCustomersReturnDevices()
    {
        var sl = U("孙磊");
        await Orders.Create(NewOrder13("最近客户甲", ServiceType.Onsite, Dev("釜", "SN-1", "M-1"), Dev("泵", "", "M-2")), sl);
        Tick();
        await Orders.Create(NewOrder("最近客户乙"), sl); // 旧请求体
        var list = await Orders.RecentCustomers(sl);
        var a = list.Single(c => c.CustomerName == "最近客户甲");
        Assert.Equal(new[] { ("釜", "SN-1", "M-1"), ("泵", "", "M-2") }, a.Devices.Select(x => (x.Name, x.Sn, x.Model)));
        Assert.Equal("HT-2026-001", a.ProjectNo);
        Assert.Equal("釜 M-1", a.DeviceModel); // 旧字段照旧（第一台）
        Assert.Equal("SN-1", a.DeviceSn);
        var b = list.Single(c => c.CustomerName == "最近客户乙");
        Assert.Equal(("", "SN1", "HTR-500"), Assert.Single(b.Devices.Select(x => (x.Name, x.Sn, x.Model))));
        // 种子：多设备
        Assert.Equal(3, list.Single(c => c.CustomerName == "南京某检测中心").Devices.Count);
    }

    [Fact]
    public async Task SatCanBeEditedByCreatorLeadAndSupervisor()
    {
        var wg = U("王工");
        var chen = U("陈工");
        var id = await NewAccepted("SAT 修改客户", "王工"); // 王敏建单、王工负责；初值 已完成 + 2024-08
        await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { chen.Id } }, Sup);
        static SatRequest S(bool? signed, string? date) => new() { SatSigned = signed, SatDate = date };
        var notices = Notifier.Sent.Count;

        // 建单人
        var d = await Orders.SetSat(id, S(true, "2026-03-15"), Wm);
        Assert.Equal("2026-03-15", d.SatDate);
        Assert.Equal("王敏 修改 SAT 信息：已完成 SAT，SAT 完成时间 2026-03-15", d.Logs[^1].Text);
        Assert.Equal(LogKind.Info, d.Logs[^1].Kind);
        Assert.Equal(OrderStatus.Accepted, d.Logs[^1].ToStatus);
        Assert.True(d.CanEditSat);
        // 负责人：改为未完成 + 清空时间
        Tick();
        d = await Orders.SetSat(id, S(false, null), wg);
        Assert.False(d.SatSigned);
        Assert.Equal("", d.SatDate);
        Assert.Equal("未完成", d.SatText);
        Assert.Equal("发货时间", d.SatDateLabel);
        Assert.Equal("新产品", d.ProductStage);
        Assert.Equal("王工 修改 SAT 信息：未完成 SAT，发货时间未填", d.Logs[^1].Text);
        Assert.True(d.CanEditSat);
        // Info 类记录所有角色可见
        Assert.Contains((await Orders.Detail(id, chen)).Logs, l => l.Kind == LogKind.Info);
        // 主管
        Tick();
        d = await Orders.SetSat(id, S(false, " 2026-05-01 "), Sup);
        Assert.Equal("赵主管 修改 SAT 信息：未完成 SAT，发货时间 2026-05-01", d.Logs[^1].Text);
        Assert.True(d.CanEditSat);
        // 值不变：不写记录
        var logs = d.Logs.Count;
        d = await Orders.SetSat(id, S(false, "2026-05-01"), Wm);
        Assert.Equal(logs, d.Logs.Count);
        Assert.Equal(notices, Notifier.Sent.Count); // 修改 SAT 信息不发通知

        // 无权限的人：协助人、非建单销售、无关工程师
        const string denied = "只有建单人、负责工程师或主管可以修改 SAT 信息";
        await Fails(40300, denied, () => Orders.SetSat(id, S(true, ""), chen));
        Assert.False((await Orders.Detail(id, chen)).CanEditSat);
        await Fails(40300, denied, () => Orders.SetSat(id, S(true, ""), U("孙磊")));
        Assert.False((await Orders.Detail(id, U("孙磊"))).CanEditSat);
        await Fails(40300, "无权查看该工单", () => Orders.SetSat(id, S(true, ""), U("刘工")));

        // 请求体校验在工单是否存在之前
        await Fails(40000, "请选择是否已完成 SAT", () => Orders.SetSat(999999, S(null, "2026-01-01"), Wm));
        await Fails(40000, "SAT 完成时间格式应为 yyyy-MM-dd", () => Orders.SetSat(999999, S(true, "2026-13-01"), Wm));
        await Fails(40000, "发货时间格式应为 yyyy-MM-dd", () => Orders.SetSat(id, S(false, "abc"), Wm));
        await Fails(40400, "工单不存在", () => Orders.SetSat(999999, S(true, ""), Wm));
        // 旧格式 yyyy-MM 仍然接受
        Assert.Equal("2024-08", (await Orders.SetSat(id, S(true, "2024-08"), Wm)).SatDate);
    }

    [Fact]
    public async Task SatEditOnClosedAndVoided()
    {
        var wg = U("王工");
        var closed = await NewClosed("SAT 闭环客户");
        var d = await Orders.SetSat(closed, new SatRequest { SatSigned = true, SatDate = "2026-11-01" }, wg);
        Assert.Equal("2026-11-01", d.SatDate);
        Assert.Equal(OrderStatus.Closed, d.Logs[^1].ToStatus);
        Assert.True(d.CanEditSat);
        Assert.True((await Orders.Detail(closed, Wm)).CanEditSat);

        var voided = await NewAccepted("SAT 作废客户", "王工");
        await Orders.Void(voided, new ReasonRequest(), Sup);
        foreach (var u in new[] { Wm, wg, Sup })
        {
            await Fails(40900, "工单已作废，不能修改 SAT 信息", () => Orders.SetSat(voided, new SatRequest { SatSigned = false }, u));
            Assert.False((await Orders.Detail(voided, u)).CanEditSat);
        }
    }

    [Fact]
    public async Task ExportHasDeviceColumns()
    {
        var req = NewOrder13("导出多设备", ServiceType.Remote, Dev("主釜", "SN-A", "HTR-900"), Dev("真空泵", "", "VP-3"), Dev("", "SN-C", ""));
        req.SatSigned = false;
        req.SatDate = "2026-12-20";
        await Orders.Create(req, Wm);
        var none = NewOrder13("导出无设备", ServiceType.Remote);
        none.SatDate = "2026-01-05";
        await Orders.Create(none, Wm);

        using var wb = new XLWorkbook(new MemoryStream(await Admin.ExportOrders(null, null, null)));
        var ws = wb.Worksheet(1);
        Assert.Equal(30, ws.Row(1).LastCellUsed()!.Address.ColumnNumber);
        Assert.Equal(new[] { "项目编号", "工单号", "客户全称", "设备型号", "设备编号", "产品阶段" }, Enumerable.Range(1, 6).Select(c => ws.Cell(1, c).GetString()));
        Assert.Equal("寄回单号", ws.Cell(1, 27).GetString());
        Assert.Equal(new[] { "设备台数", "SAT 完成时间", "发货时间" }, Enumerable.Range(28, 3).Select(c => ws.Cell(1, c).GetString()));
        IXLRow Row(string customer) => ws.RowsUsed().Single(r => r.Cell(3).GetString() == customer);

        var multi = Row("导出多设备");
        Assert.Equal("HT-2026-001", multi.Cell(1).GetString());
        Assert.Equal("主釜 HTR-900；真空泵 VP-3；SN-C", multi.Cell(4).GetString());
        Assert.Equal("SN-A；—；SN-C", multi.Cell(5).GetString());
        Assert.Equal("新产品", multi.Cell(6).GetString());
        Assert.Equal(3, multi.Cell(28).GetDouble());
        Assert.Equal("", multi.Cell(29).GetString());
        Assert.Equal("2026-12-20", multi.Cell(30).GetString());

        var empty = Row("导出无设备");
        Assert.Equal("", empty.Cell(4).GetString());
        Assert.Equal("", empty.Cell(5).GetString());
        Assert.Equal(0, empty.Cell(28).GetDouble());
        Assert.Equal("2026-01-05", empty.Cell(29).GetString());
        Assert.Equal("", empty.Cell(30).GetString());

        // 旧（种子）工单：前 27 列与 v1.2 相同
        var old = Row("江苏某化工研究院");
        Assert.Equal("", old.Cell(1).GetString());
        Assert.Equal("高压反应釜 HTR-500", old.Cell(4).GetString());
        Assert.Equal("SN2408017", old.Cell(5).GetString());
        Assert.Equal("老产品", old.Cell(6).GetString());
        Assert.Equal(1800, old.Cell(17).GetDouble());
        Assert.Equal(1, old.Cell(28).GetDouble());
        Assert.Equal("2024-08", old.Cell(29).GetString());
        Assert.Equal("", old.Cell(30).GetString());
        var notSigned = Row("苏州某药企"); // 种子：SAT 未签字 → 时间显示在「发货时间」名下
        Assert.Equal("", notSigned.Cell(29).GetString());
        Assert.Equal("2024-08", notSigned.Cell(30).GetString());
        var seedMulti = Row("南京某检测中心");
        Assert.Equal("高压反应釜 HTR-250；搅拌器 HTS-80；冷凝器 HTC-20", seedMulti.Cell(4).GetString());
        Assert.Equal("SN2405009；SN2405010；SN2405011", seedMulti.Cell(5).GetString());
    }

    // ---------- 迁移 ----------

    [Fact]
    public async Task MigrationBackfillsDevicesOnV12Database()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        db.GetService<IMigrator>().Migrate("V12_Attachments");
        Assert.Equal(new[] { "V13_DevicesTransfers" }, db.Database.GetPendingMigrations().Select(m => m[(m.IndexOf('_') + 1)..]));

        db.Database.ExecuteSqlRaw("""
            INSERT INTO Employees (Id, Name, Phone, Role, Department, Enabled, TokenVersion, CreatedAt)
            VALUES (1, '老主管', '13800000001', 'Supervisor', '售后部', 1, 0, '2026-10-01 09:00:00');
            INSERT INTO Employees (Id, Name, Phone, Role, Department, Enabled, TokenVersion, CreatedAt)
            VALUES (2, '老工程师', '13800000002', 'Engineer', '售后部', 1, 0, '2026-10-01 09:00:00');
            INSERT INTO WorkOrders (Id, OrderNo, Status, Source, ProblemType, Urgency, ServiceType, CustomerName, ContactName, ContactPhone,
                Address, City, Region, DeviceModel, DeviceSn, SatDate, SatSigned, ProjectNo, FaultTime, Symptom, CustomerTried, SiteEnvironment,
                Price, PriceSet, CreatorId, AssigneeId, CreatedAt)
            VALUES (1, 'SH-20261101-001', 'InProgress', 'SalesReferral', 'Fault', 'Normal', 'Onsite', '老客户', '张工', '13800000000',
                '苏州市', '苏州', '苏州市', ' HTR-500 ', 'SN-OLD-1', '2024-08', 1, 'OLD-PRJ', '昨天', '不加热', '重启', '通风橱',
                1800.0, 1, 1, 2, '2026-11-01 09:00:00');
            INSERT INTO WorkOrders (Id, OrderNo, Status, Source, ProblemType, Urgency, ServiceType, CustomerName, ContactName, ContactPhone,
                Address, City, Region, DeviceModel, DeviceSn, SatDate, SatSigned, FaultTime, Symptom, CustomerTried, SiteEnvironment,
                Price, PriceSet, CreatorId, CreatedAt)
            VALUES (2, 'SH-20261101-002', 'Pending', 'SalesReferral', 'Consult', 'Normal', 'Remote', '没填设备的老客户', '李工', '13800000000',
                '苏州市', '苏州', '苏州市', '', '', '2024-09', 0, '昨天', '咨询', '无', '实验室',
                0.0, 1, 1, '2026-11-01 10:00:00');
            INSERT INTO WorkOrders (Id, OrderNo, Status, Source, ProblemType, Urgency, ServiceType, CustomerName, ContactName, ContactPhone,
                Address, City, Region, DeviceModel, DeviceSn, SatDate, SatSigned, FaultTime, Symptom, CustomerTried, SiteEnvironment,
                Price, PriceSet, CreatorId, CreatedAt)
            VALUES (3, 'SH-20261101-003', 'Pending', 'SalesReferral', 'Fault', 'Normal', 'Remote', '只有序列号的老客户', '李工', '13800000000',
                '苏州市', '苏州', '苏州市', '  ', 'SN-ONLY-3', '2024-09', 0, '昨天', '异响', '无', '实验室',
                0.0, 1, 1, '2026-11-01 11:00:00');
            INSERT INTO StoredFiles (Id, Path, OriginalName, Category, MimeType, Size, UploaderId, OrderId, CreatedAt, Restricted)
            VALUES (1, '2026/11/a.jpg', 'tmp_1.jpg', 'OrderPhoto', 'image/jpeg', 1000, 1, 1, '2026-11-01 09:00:00', 0);
            INSERT INTO StoredFiles (Id, Path, OriginalName, Category, MimeType, Size, UploaderId, OrderId, CreatedAt, Restricted, DisplayName)
            VALUES (2, '2026/11/b.pdf', '报价.pdf', 'Attachment', 'application/pdf', 5000, 1, 1, '2026-11-01 09:00:01', 1, '报价.pdf');
            INSERT INTO StoredFiles (Id, Path, OriginalName, Category, MimeType, Size, UploaderId, OrderId, CreatedAt, Restricted)
            VALUES (3, '2026/11/c.jpg', 'tmp_3.jpg', 'PunchPhoto', 'image/jpeg', 2000, 2, 1, '2026-11-02 10:00:00', 0);
            INSERT INTO StatusLogs (OrderId, ActorId, Text, Time, ToStatus) VALUES (1, 1, '老主管 建单', '2026-11-01 09:00:00', 'Pending');
            """);

        db.Database.Migrate();
        Assert.Empty(db.Database.GetPendingMigrations());
        db.ChangeTracker.Clear();

        // 设备行回填：有型号 / 序列号的各一台（名称为空、去掉首尾空白），两项都空的没有设备行
        var devices = db.OrderDevices.AsNoTracking().OrderBy(x => x.OrderId).ToList();
        Assert.Equal(new[] { (1, 1, "", "SN-OLD-1", "HTR-500"), (3, 1, "", "SN-ONLY-3", "") },
            devices.Select(x => (x.OrderId, x.Seq, x.Name, x.Sn, x.Model)));
        var rows = db.Orders.AsNoTracking().OrderBy(o => o.Id).ToList();
        Assert.Equal(new[] { "HTR-500", "未填设备", "SN-ONLY-3" }, rows.Select(o => o.DeviceText));
        Assert.All(rows, o => Assert.Null(o.PendingTransferToId));
        Assert.Equal(new[] { "OLD-PRJ", null, null }, rows.Select(o => o.ProjectNo));
        Assert.Equal(new[] { "2024-08", "2024-09", "2024-09" }, rows.Select(o => o.SatDate)); // 原样
        // 旧文件全部落在 Other；库里存的是枚举名
        Assert.All(db.Files.IgnoreQueryFilters().AsNoTracking().ToList(), f => Assert.Equal(AttachmentType.Other, f.AttachmentType));
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM StoredFiles WHERE AttachmentType = 'Other'";
            Assert.Equal(3L, cmd.ExecuteScalar());
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('IX_WorkOrders_PendingTransferToId', " +
                              "'IX_OrderDevices_OrderId', 'IX_OrderTransfers_OrderId', 'IX_OrderTransfers_ToEngineerId_Status', " +
                              "'IX_OrderExits_OrderId_EngineerId', 'IX_OrderExits_EngineerId')";
            Assert.Equal(6L, cmd.ExecuteScalar());
        }
        Assert.Empty(db.OrderExits); // 不回填退出记录
        Assert.Empty(db.OrderTransfers);

        // 旧工单的详情、列表、搜索、文件中心正常
        var files = new FileService(db, Clock, new Env { ContentRootPath = Root }, new HttpContextAccessor(),
            Options.Create(new AppSettings { PublicBaseUrl = "http://test" }));
        var view = new OrderViewBuilder(db, Clock, files);
        var orders = new OrderService(db, Clock, view, files, Notifier, NullLogger<OrderService>.Instance);
        var eng = new CurrentUser(2, Role.Engineer);
        var sup = new CurrentUser(1, Role.Supervisor);
        var d = await view.BuildDetail(1, eng);
        var dev = Assert.Single(d.Devices);
        Assert.Equal(("", "SN-OLD-1", "HTR-500", "HTR-500"), (dev.Name, dev.Sn, dev.Model, dev.Title));
        Assert.Equal("HTR-500", d.DeviceText);
        Assert.Equal("OLD-PRJ", d.ProjectNo);
        Assert.Equal("已完成", d.SatText);
        Assert.Equal("2024-08", d.SatDate);
        Assert.Empty(d.Transfers);
        Assert.Null(d.PendingTransfer);
        Assert.Null(d.ReadOnlyText);
        Assert.Equal(AttachmentType.Other, Assert.Single(d.Attachments).AttachmentType); // 工程师看不到受限的那个
        Assert.Contains("transfer", d.Actions);
        var d2 = await view.BuildDetail(2, sup);
        Assert.Empty(d2.Devices);
        Assert.Equal("未填设备", d2.DeviceText);
        Assert.Null(d2.ProjectNo);
        Assert.Equal("发货时间", d2.SatDateLabel);

        var list = await orders.Query(new OrderQuery(), sup);
        Assert.Equal(new[] { "SN-ONLY-3", "未填设备", "HTR-500" }, list.Items.Select(i => i.DeviceText));
        Assert.Equal(new[] { 1 }, (await orders.Query(new OrderQuery { Keyword = "SN-OLD" }, sup)).Items.Select(i => i.Id));
        Assert.Equal(new[] { 1 }, (await orders.Query(new OrderQuery { Keyword = "HTR-500" }, sup)).Items.Select(i => i.Id));
        Assert.Equal(new[] { 1 }, (await orders.Query(new OrderQuery { Keyword = "OLD-PRJ" }, sup)).Items.Select(i => i.Id));

        var center = new FileCenterService(db, view);
        var page = await center.Query(new FileCenterQuery(), sup);
        Assert.Equal(3, page.Total);
        Assert.Equal(new AttachmentType?[] { null, AttachmentType.Other, AttachmentType.Other }, page.Items.Select(i => i.AttachmentType));
        Assert.Equal(2, (await center.Query(new FileCenterQuery { AttachmentType = "Other" }, sup)).Total);
        Assert.Equal(0, (await center.Query(new FileCenterQuery { AttachmentType = "Sat" }, sup)).Total);

        // 升级后的库上新功能可用：改 SAT、改附件类别、转交
        d = await orders.SetSat(1, new SatRequest { SatSigned = true, SatDate = "2026-03-15" }, sup);
        Assert.Equal("2026-03-15", d.SatDate);
        d = await orders.SetAttachmentType(1, 2, new AttachmentTypeRequest { Type = "Sat" }, sup);
        Assert.Equal(AttachmentType.Sat, d.Attachments.Single(a => a.Id == 2).AttachmentType);
        await Fails(40000, "所选工程师不存在或已停用", () => orders.Transfer(1, new TransferRequest { EngineerId = 1, Reason = "x" }, eng));
    }
}
