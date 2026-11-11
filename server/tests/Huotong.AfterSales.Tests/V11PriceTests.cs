using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.1 (3) 价格：建单不定价、主管定价、销售建议、工程师不可见、导出</summary>
public sealed class V11PriceTests : V11TestBase
{
    [Fact]
    public async Task NewOrderIsUnpriced()
    {
        // 防 bool 默认值陷阱：列默认值是 true，但显式写入的 false 必须落库
        var creator = Emp("王敏");
        Db.Orders.Add(new WorkOrder { OrderNo = "SH-TEST-001", CustomerName = "直接入库", CreatorId = creator.Id, CreatedAt = Clock.Now, PriceSet = false });
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        Assert.False(Db.Orders.Single(o => o.OrderNo == "SH-TEST-001").PriceSet);

        var id = await NewPending("服务建单");
        Db.ChangeTracker.Clear();
        var o = Db.Orders.Single(x => x.Id == id);
        Assert.False(o.PriceSet);
        Assert.Equal(0, o.Price);
        Assert.Null(o.PriceSetAt);
    }

    [Fact]
    public async Task CreateIgnoresPrice()
    {
        var r = NewOrder("旧客户端带价格");
        r.Price = 999;
        var d = await Orders.Create(r, U("王敏"));
        Assert.False(d.PriceSet);
        Assert.Null(d.Price);
        Assert.Equal(0, Db.Orders.AsNoTracking().Single(o => o.Id == d.Id).Price);
        Assert.Equal(new[] { "suggestPrice" }, d.Actions);

        // 工程师建单（客户直联）同样没有价格，也看不到价格
        var eng = NewOrder("工程师直联单", ServiceType.Remote);
        eng.Price = "12.5"; // 任何形态都忽略
        var e = await Orders.Create(eng, U("王工"));
        Assert.Null(e.Price);
        Assert.Null(e.PriceSet);
        Assert.False(Db.Orders.AsNoTracking().Single(o => o.Id == e.Id).PriceSet);
    }

    [Fact]
    public async Task SupervisorSetsAndChangesPrice()
    {
        var id = await NewPending("定价客户");
        var d = await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        Assert.True(d.PriceSet);
        Assert.Equal(0, d.Price);
        Assert.Equal(Clock.Now, d.PriceSetAt);
        Assert.Equal("赵主管 定价 保内免费", d.Logs.Last().Text);
        Assert.Equal(LogKind.Price, d.Logs.Last().Kind);
        Assert.Equal(OrderStatus.Pending, d.Logs.Last().ToStatus);

        Tick();
        d = await Orders.SetPrice(id, new PriceRequest { Price = 1800 }, Sup);
        Assert.Equal(1800, d.Price);
        Assert.Equal("赵主管 改价 保内免费 → ¥1,800", d.Logs.Last().Text);

        // 金额不变：不写记录，仍更新 PriceSetAt
        Tick();
        var count = d.Logs.Count;
        d = await Orders.SetPrice(id, new PriceRequest { Price = 1800.001m }, Sup);
        Assert.Equal(count, d.Logs.Count);
        Assert.Equal(Clock.Now, d.PriceSetAt);

        d = await Orders.SetPrice(id, new PriceRequest { Price = 1800.5m }, Sup);
        Assert.Equal("赵主管 改价 ¥1,800 → ¥1,800.50", d.Logs.Last().Text);

        await Fails(40000, "服务价格不能小于 0", () => Orders.SetPrice(id, new PriceRequest { Price = -1 }, Sup));
        await Fails(40000, "请填写服务价格（保内填 0）", () => Orders.SetPrice(id, new PriceRequest(), Sup));
        await Fails(40000, "服务价格过大", () => Orders.SetPrice(id, new PriceRequest { Price = 100_000_001 }, Sup));

        // 销售、工程师不能定价
        Assert.Equal(40300, (await Biz(() => Orders.SetPrice(id, new PriceRequest { Price = 1 }, U("王敏")))).Code);
        Assert.Equal(40300, (await Biz(() => Orders.SetPrice(id, new PriceRequest { Price = 1 }, U("王工")))).Code);
        Assert.Equal(1800.5m, Db.Orders.AsNoTracking().Single(o => o.Id == id).Price);

        // 已闭环仍可改价；已作废不能
        var closed = await Orders.SetPrice(OrderId("宁波某化工企业"), new PriceRequest { Price = 1600 }, Sup);
        Assert.Equal("赵主管 改价 ¥1,500 → ¥1,600", closed.Logs.Last().Text);
        await Orders.Void(id, new ReasonRequest(), Sup);
        await Fails(40900, "工单已作废，不能定价", () => Orders.SetPrice(id, new PriceRequest { Price = 1 }, Sup));
    }

    [Fact]
    public async Task EngineerNeverSeesPrice()
    {
        var wang = U("王工");
        var sales = U("王敏");
        var id = await NewAccepted("价格可见性客户");
        await Orders.SetPrice(id, new PriceRequest { Price = 1234.56m }, Sup);
        Tick();
        await Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 987.65m, Note = "建议优惠" }, sales);
        var hallId = await NewPending("大厅里的已定价单");
        await Orders.SetPrice(hallId, new PriceRequest { Price = 4321.09m }, Sup);

        var detail = await Orders.Detail(id, wang);
        Assert.Null(detail.Price);
        Assert.Null(detail.PriceSet);
        Assert.Null(detail.PriceSetAt);
        Assert.Null(detail.PriceSuggestion);
        Assert.DoesNotContain(detail.Logs, l => l.Kind == LogKind.Price);
        Assert.DoesNotContain(detail.Logs, l => l.Text.Contains("价"));
        Assert.DoesNotContain("setPrice", detail.Actions);
        Assert.DoesNotContain("suggestPrice", detail.Actions);

        var hall = await Orders.Hall("all", wang);
        Assert.Contains(hall.Items, i => i.Id == hallId);
        var hallDetail = await Orders.Detail(hallId, wang);
        var mine = await Orders.Mine(wang, "all");
        Assert.Contains(mine.Items, i => i.Id == id);
        var home = await Home.Engineer(wang);
        var punch = await Arrive(wang, id);
        var created = await Orders.Created(wang, "all");
        var context = await Punches.Context(wang, id);
        var today = await Hours.Today(wang.Id);
        var month = await Hours.Month(wang.Id, 2026, 11);

        foreach (var payload in new object?[] { detail, hall, hallDetail, mine, home, punch, created, context, today, month })
        {
            var json = Json(payload!);
            Assert.DoesNotContain("1234.56", json);
            Assert.DoesNotContain("987.65", json);
            Assert.DoesNotContain("4321.09", json);
            Assert.DoesNotMatch(new Regex("\"price\":\\s*[0-9]"), json);
            Assert.DoesNotMatch(new Regex("\"priceSet\":\\s*(true|false)"), json);
            Assert.DoesNotMatch(new Regex("\"priceSuggestion\":\\s*\\{"), json);
            Assert.DoesNotContain("\"kind\":\"Price\"", json);
            Assert.DoesNotContain("¥", json);
        }
        Assert.All(hall.Items.Concat(mine.Items).Concat(home.Hall.Items).Concat(home.Upcoming).Append(punch.Order), i =>
        {
            Assert.Null(i.Price);
            Assert.Null(i.PriceSet);
        });

        // 协助人也是工程师，同样看不到
        await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { U("刘工").Id } }, Sup);
        var assistant = await Orders.Detail(id, U("刘工"));
        Assert.Null(assistant.Price);
        Assert.Null(assistant.PriceSuggestion);

        // 销售与主管能看到
        foreach (var who in new[] { sales, Sup })
        {
            var d = await Orders.Detail(id, who);
            Assert.Equal(1234.56m, d.Price);
            Assert.True(d.PriceSet);
            Assert.Equal(987.65m, d.PriceSuggestion!.Amount);
            Assert.Contains(d.Logs, l => l.Kind == LogKind.Price);
            var q = await Orders.Query(new OrderQuery { Keyword = "价格可见性" }, who);
            Assert.Equal(1234.56m, q.Items.Single().Price);
        }
    }

    [Fact]
    public async Task SalesSuggestsPrice()
    {
        var id = await NewAccepted("建议客户");
        var before = Notifier.Sent.Count;
        // 任何销售都可以，不限建单人
        var d = await Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 1500, Note = "老客户，建议优惠" }, U("孙磊"));
        Assert.Equal("孙磊 建议价格 ¥1,500（老客户，建议优惠）", d.Logs.Last().Text);
        Assert.Equal(LogKind.Price, d.Logs.Last().Kind);
        Assert.False(d.PriceSet); // 销售不能定价
        Assert.Equal(before + 1, Notifier.Sent.Count);
        Assert.Equal("价格建议", Notifier.Last.Title);
        Assert.Equal($"孙磊 对 **建议客户**（{d.OrderNo}）提交了价格建议，请在工单详情中查看并定价。", Notifier.Last.Text);
        Assert.DoesNotContain("1500", Notifier.Last.Text);
        Assert.DoesNotContain("1,500", Notifier.Last.Text);
        Assert.Equal(new[] { Emp("赵主管").Phone }, Notifier.Last.Mobiles);

        var sup = await Orders.Detail(id, Sup);
        Assert.Equal(1500, sup.PriceSuggestion!.Amount);
        Assert.Equal("老客户，建议优惠", sup.PriceSuggestion.Note);
        Assert.Equal("孙磊", sup.PriceSuggestion.By.Name);
        Assert.True(sup.PriceSuggestion.Pending);

        Tick();
        sup = await Orders.SetPrice(id, new PriceRequest { Price = 1500 }, Sup);
        Assert.False(sup.PriceSuggestion!.Pending);

        // 定价后的新建议又是 pending；详情只展示最新一条
        Tick();
        d = await Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 1200 }, U("王敏"));
        Assert.Equal(1200, d.PriceSuggestion!.Amount);
        Assert.True(d.PriceSuggestion.Pending);
        Assert.Equal("王敏 建议价格 ¥1,200", d.Logs.Last().Text);
        Assert.Equal(1500, d.Price);

        await Fails(40300, "只有销售可以提交价格建议", () => Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 1 }, U("王工")));
        await Fails(40300, "只有销售可以提交价格建议", () => Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 1 }, Sup));
        await Fails(40000, "请填写建议价格", () => Orders.SuggestPrice(id, new PriceSuggestRequest(), U("王敏")));
        await Fails(40000, "建议价格不能小于 0", () => Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = -1 }, U("王敏")));
        await Fails(40000, "建议价格过大", () => Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 100_000_001 }, U("王敏")));
        await Fails(40000, "备注不能超过 200 个字",
            () => Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 1, Note = new string('长', 201) }, U("王敏")));
        await Fails(40900, "工单当前为「已闭环」，不能提交价格建议",
            () => Orders.SuggestPrice(OrderId("宁波某化工企业"), new PriceSuggestRequest { Amount = 1 }, U("王敏")));
    }

    [Fact]
    public async Task CloseRequiresPrice()
    {
        var wang = U("王工");
        var id = await NewAccepted("未定价完工客户", service: ServiceType.Remote);
        await Orders.Start(id, wang);
        await Orders.Complete(id, Done(wang, signature: false), wang);

        var d = await Orders.Detail(id, Sup);
        Assert.Equal("请先设置服务价格（保内填 0）", d.CloseBlockedReason);
        Assert.Equal(new[] { "reject", "setPrice" }, d.Actions);
        var todo = (await Home.Supervisor(Sup)).Todos.Single(t => t.Type == "Submitted" && t.OrderId == id);
        Assert.Equal("王工 今天提交完工，资料已上传，尚未定价", todo.Subtitle);
        await Fails(40900, "请先设置服务价格（保内填 0）", () => Orders.Close(id, Sup));
        // 非主管看不到 closeBlockedReason
        Assert.Null((await Orders.Detail(id, U("王敏"))).CloseBlockedReason);

        d = await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        Assert.Null(d.CloseBlockedReason);
        Assert.Equal(new[] { "reject", "close", "setPrice" }, d.Actions);
        todo = (await Home.Supervisor(Sup)).Todos.Single(t => t.Type == "Submitted" && t.OrderId == id);
        Assert.Equal("王工 今天提交完工，资料已上传", todo.Subtitle);
        d = await Orders.Close(id, Sup);
        Assert.Equal(OrderStatus.Closed, d.Status);
        Assert.Equal(new[] { "setPrice" }, d.Actions);
    }

    [Fact]
    public async Task ExportShowsUnpriced()
    {
        using var wb = new XLWorkbook(new MemoryStream(await Admin.ExportOrders(null, null, null)));
        var ws = wb.Worksheet(1);
        Assert.Equal("是否收费", ws.Cell(1, 16).GetString());
        Assert.Equal("服务价格", ws.Cell(1, 17).GetString());
        Assert.Equal(new[] { "协助人", "人员工时", "暂停原因", "返厂进度", "寄回单号" },
            Enumerable.Range(23, 5).Select(c => ws.Cell(1, c).GetString()));
        IXLRow Row(string customer) => ws.RowsUsed().Single(r => r.Cell(3).GetString() == customer);

        var unpriced = Row("无锡某制药企业");
        Assert.Equal("未定价", unpriced.Cell(16).GetString());
        Assert.Equal("未定价", unpriced.Cell(17).GetString());
        Assert.Equal(XLDataType.Text, unpriced.Cell(17).DataType);

        var paid = Row("江苏某化工研究院");
        Assert.Equal("是", paid.Cell(16).GetString());
        Assert.Equal(XLDataType.Number, paid.Cell(17).DataType);
        Assert.Equal(1800, paid.Cell(17).GetDouble());
        Assert.Equal("李工", paid.Cell(12).GetString());

        var free = Row("浙江某新材料公司");
        Assert.Equal("否（保内）", free.Cell(16).GetString());
        Assert.Equal(0, free.Cell(17).GetDouble());

        // 新增 5 列：协助人、人员工时、暂停原因、返厂进度、寄回单号
        var held = Row("常州某材料厂");
        Assert.Equal("王工", held.Cell(23).GetString());
        Assert.Equal("刘工 8.0h；王工 7.0h", held.Cell(24).GetString());
        Assert.Equal("等备件：进口密封件预计 11 月中旬到货", held.Cell(25).GetString());
        Assert.Equal(15, held.Cell(18).GetDouble()); // 现场工时 = 所有人合计
        Assert.Equal("", held.Cell(26).GetString());

        var repairing = Row("常熟某研究院");
        Assert.Equal("检测/维修中", repairing.Cell(26).GetString());
        Assert.Equal("", repairing.Cell(27).GetString());
        var signed = Row("苏州某药企");
        Assert.Equal("客户已签收", signed.Cell(26).GetString());
        Assert.Equal("顺丰 SF1100200399", signed.Cell(27).GetString());
        Assert.Equal("", paid.Cell(25).GetString());
        Assert.Equal("", paid.Cell(26).GetString());
    }
}
