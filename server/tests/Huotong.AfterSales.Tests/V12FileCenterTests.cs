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
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.2 工单文件中心与迁移（CHANGES-v1.2 §5、§9.2 第 21–25 项）</summary>
public sealed class V12FileCenterTests : V12TestBase
{
    private const string Jiangsu = "江苏某化工研究院";
    private readonly FileCenterService _center;

    public V12FileCenterTests() => _center = new FileCenterService(Db, View);

    private Task<FileCenterPage> Q(FileCenterQuery? q = null, CurrentUser? u = null) => _center.Query(q ?? new FileCenterQuery { PageSize = 200 }, u ?? Sup);

    private List<StoredFile> Expected() =>
        Db.Files.AsNoTracking().Where(f => f.OrderId != null && f.Category != FileCategory.Avatar).ToList();

    [Fact]
    public async Task SeedQueryCoversAllSourcesWithTotals()
    {
        var li = U("李工");
        // 不应出现：头像、未挂载、已删除
        var avatar = (await Up(li, Png, "me.png", "Avatar")).Id;
        var loose = await UpPdf(li, "未挂载.pdf");
        var wm = U("王敏");
        var deleted = await UpPdf(wm, "已删除.pdf");
        await Orders.AddAttachments(OrderId(Jiangsu), Att(deleted), wm);
        await Orders.DeleteAttachment(OrderId(Jiangsu), deleted, wm);
        // 种子没有签收凭证 / 完工视频：不影响来源覆盖（种子含 6 种来源）

        var r = await Q();
        var exp = Expected();
        Assert.Equal(40, exp.Count); // 种子：3 建单照片 + 2 附件 + 20 打卡照片 + 3 返厂照片 + 8 完工照片 + 4 签字件
        Assert.Equal(40, r.Total);
        Assert.Equal(exp.Sum(f => f.Size), r.TotalSize);
        Assert.Equal(40, r.Items.Count);
        Assert.Equal(1, r.Page);
        Assert.Equal(200, r.PageSize);
        Assert.Equal(new[] { "Attachment", "Factory", "OrderPhoto", "ProcessPhoto", "PunchPhoto", "Signature" },
            r.Items.Select(i => i.Source).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        Assert.DoesNotContain(r.Items, i => i.Id == avatar || i.Id == loose || i.Id == deleted);
        Assert.DoesNotContain(r.Items, i => i.Category == FileCategory.Avatar);
        Assert.Equal(new Dictionary<string, int> { ["all"] = 40, ["Image"] = 38, ["Video"] = 0, ["Document"] = 2 }, r.Counts);

        // 排序：时间倒序、id 倒序
        var sorted = r.Items.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).Select(i => i.Id).ToList();
        Assert.Equal(sorted, r.Items.Select(i => i.Id).ToList());

        // 行字段
        var quote = r.Items.Single(i => i.Name == "报价单.pdf");
        Assert.True(quote.Restricted);
        Assert.Equal("Attachment", quote.Source);
        Assert.Equal("附件", quote.SourceLabel);
        Assert.Equal(FileKind.Document, quote.Kind);
        Assert.Equal("pdf", quote.Ext);
        Assert.Equal("王敏", quote.Uploader!.Name);
        Assert.Equal(OrderId(Jiangsu), quote.OrderId);
        Assert.Equal(Jiangsu, quote.CustomerName);
        Assert.Equal(OrderStatus.InProgress, quote.OrderStatus);
        Assert.StartsWith("SH-", quote.OrderNo);
        Assert.StartsWith("http://test/uploads/", quote.Url);
        var labels = r.Items.Select(i => i.SourceLabel).Distinct().ToHashSet();
        Assert.Subset(new HashSet<string> { "附件", "建单照片", "完工照片", "签字件", "打卡照片", "返厂·收货照片", "返厂·寄回照片", "返厂·签收凭证" }, labels);
        Assert.Contains("返厂·收货照片", labels);
        Assert.Contains("返厂·寄回照片", labels);
        Assert.Matches(@"^打卡照片_\d{8}-\d{6}\.png$", r.Items.First(i => i.Source == "PunchPhoto").Name);
        Assert.Matches(@"^完工照片_\d{8}-\d{6}\.png$", r.Items.First(i => i.Source == "ProcessPhoto").Name);
        Assert.Matches(@"^签字件_\d{8}-\d{6}\.png$", r.Items.First(i => i.Source == "Signature").Name);
        Assert.Matches(@"^收货照片_\d{8}-\d{6}\.png$", r.Items.First(i => i.Category == FileCategory.FactoryReceivePhoto).Name);
        // 非附件来源：restricted=false
        Assert.All(r.Items.Where(i => i.Source != "Attachment" && i.Source != "OrderPhoto"), i => Assert.False(i.Restricted));
    }

    [Fact]
    public async Task IncludesVoidedOrders()
    {
        var wm = U("王敏");
        var id = await NewPending("作废单的文件");
        var f = await UpPdf(wm, "作废单附件.pdf");
        await Orders.AddAttachments(id, Att(f), wm);
        await Orders.Void(id, new ReasonRequest(), Sup);
        var item = Assert.Single((await Q(new FileCenterQuery { OrderId = id })).Items);
        Assert.Equal(OrderStatus.Voided, item.OrderStatus);
        Assert.True(item.CanDelete); // 主管在已作废工单上仍可删除
    }

    [Fact]
    public async Task Filters()
    {
        var jiangsu = OrderId(Jiangsu);
        var wm = U("王敏");
        // 增加一个视频附件与一个 docx，便于按类型筛选
        var video = (await Up(wm, Mp4, "故障视频.mp4", name: "故障视频.mp4")).Id;
        var docx = (await Up(wm, Ooxml(), "验收报告.docx", name: "验收报告.docx")).Id;
        Tick(5);
        await Orders.AddAttachments(jiangsu, Att(video, docx), wm);
        var all = await Q();
        Assert.Equal(42, all.Total);
        Assert.Equal(new Dictionary<string, int> { ["all"] = 42, ["Image"] = 38, ["Video"] = 1, ["Document"] = 3 }, all.Counts);

        // keyword：文件名 / 工单号 / 客户
        var r = await Q(new FileCenterQuery { Keyword = "报价" });
        Assert.Equal("报价单.pdf", Assert.Single(r.Items).Name);
        Assert.Equal(1, r.Counts["all"]);
        r = await Q(new FileCenterQuery { Keyword = " 化工研究院 ", PageSize = 200 });
        Assert.Equal(10, r.Total); // 江苏：3 建单照片 + 2 附件 + 3 打卡照片 + 2 新增
        Assert.All(r.Items, i => Assert.Equal(jiangsu, i.OrderId));
        var orderNo = Db.Orders.AsNoTracking().Single(o => o.Id == jiangsu).OrderNo;
        Assert.Equal(10, (await Q(new FileCenterQuery { Keyword = orderNo })).Total);
        Assert.Equal(0, (await Q(new FileCenterQuery { Keyword = "不存在的关键字" })).Total);
        Assert.Equal(0, (await Q(new FileCenterQuery { Keyword = "不存在的关键字" })).TotalSize);
        Assert.True((await Q(new FileCenterQuery { Keyword = "seed.png", PageSize = 200 })).Total >= 38); // OriginalName

        // type：counts 不受 type 影响，total / totalSize 受影响
        r = await Q(new FileCenterQuery { Type = "Document" });
        Assert.Equal(3, r.Total);
        Assert.All(r.Items, i => Assert.Equal(FileKind.Document, i.Kind));
        Assert.Equal(all.Counts, r.Counts);
        Assert.Equal(r.Items.Sum(i => i.Size), r.TotalSize);
        r = await Q(new FileCenterQuery { Type = "Video" });
        Assert.Equal(video, Assert.Single(r.Items).Id);
        Assert.Equal(Mp4.Length, r.TotalSize);
        r = await Q(new FileCenterQuery { Type = "image", PageSize = 200 });
        Assert.Equal(38, r.Total);
        Assert.All(r.Items, i => Assert.Equal(FileKind.Image, i.Kind));

        // source
        r = await Q(new FileCenterQuery { Source = "Factory" });
        Assert.Equal(3, r.Total);
        Assert.Equal(new[] { FileCategory.FactoryReceivePhoto, FileCategory.FactoryShipPhoto },
            r.Items.Select(i => i.Category).Distinct().OrderBy(c => c));
        Assert.All(r.Items, i => Assert.Equal("Factory", i.Source));
        Assert.Equal(3, r.Counts["Image"]); // counts 受 source 影响
        Assert.Equal(20, (await Q(new FileCenterQuery { Source = "PunchPhoto" })).Total);
        Assert.Equal(4, (await Q(new FileCenterQuery { Source = "Attachment" })).Total);
        Assert.Equal(3, (await Q(new FileCenterQuery { Source = "OrderPhoto" })).Total);
        Assert.Equal(8, (await Q(new FileCenterQuery { Source = "ProcessPhoto" })).Total);
        Assert.Equal(4, (await Q(new FileCenterQuery { Source = "Signature" })).Total);
        // 返厂签收凭证也算 Factory
        var sl = U("孙磊");
        var changshu = OrderId("常熟某研究院");
        var wang = U("王工");
        await Orders.RecordFactoryNode(changshu, Node(FactoryNode.ShippedBack, "顺丰", "SF1", NewFile(wang, FileCategory.FactoryShipPhoto)), wang);
        var wmSign = NewFile(wm, FileCategory.FactorySignPhoto);
        await Orders.RecordFactoryNode(changshu, Node(FactoryNode.CustomerSigned, null, null, wmSign), wm);
        r = await Q(new FileCenterQuery { Source = "factory" });
        Assert.Equal(5, r.Total);
        Assert.Equal("返厂·签收凭证", r.Items.Single(i => i.Id == wmSign).SourceLabel);
        Assert.StartsWith("签收凭证_", r.Items.Single(i => i.Id == wmSign).Name);

        // uploaderId / orderId / restricted
        r = await Q(new FileCenterQuery { UploaderId = sl.Id });
        Assert.Equal(0, r.Total);
        r = await Q(new FileCenterQuery { UploaderId = wm.Id, PageSize = 200 });
        Assert.Equal(8, r.Total); // 3 建单照片 + 4 附件 + 1 签收凭证
        Assert.All(r.Items, i => Assert.Equal("王敏", i.Uploader!.Name));
        r = await Q(new FileCenterQuery { OrderId = jiangsu, PageSize = 200 });
        Assert.Equal(10, r.Total);
        r = await Q(new FileCenterQuery { Restricted = "true" });
        Assert.Equal("报价单.pdf", Assert.Single(r.Items).Name);
        Assert.Equal(43, (await Q(new FileCenterQuery { Restricted = "false" })).Total);

        // from / to（含两端）
        var today = Clock.Now.ToString("yyyy-MM-dd");
        r = await Q(new FileCenterQuery { From = today, To = today, PageSize = 200 });
        Assert.Equal(Expected().Count(f => f.CreatedAt.Date == Clock.Now.Date), r.Total);
        Assert.Contains(r.Items, i => i.Id == video);
        Assert.Equal(44, (await Q(new FileCenterQuery { To = today })).Total);
        Assert.Equal(0, (await Q(new FileCenterQuery { From = Clock.Now.AddDays(1).ToString("yyyy-MM-dd") })).Total);
        r = await Q(new FileCenterQuery { From = "2026-11-05", To = "2026-11-05", PageSize = 200 });
        Assert.Equal(Expected().Count(f => f.CreatedAt.Date == new DateTime(2026, 11, 5)), r.Total);
        Assert.True(r.Total > 0);

        // 组合
        r = await Q(new FileCenterQuery { OrderId = jiangsu, Type = "Document", Source = "Attachment", UploaderId = wm.Id });
        Assert.Equal(3, r.Total);

        // 分页
        var p1 = await Q(new FileCenterQuery { PageSize = 10 });
        var p2 = await Q(new FileCenterQuery { PageSize = 10, Page = 2 });
        var p5 = await Q(new FileCenterQuery { PageSize = 10, Page = 5 });
        Assert.Equal(44, p1.Total);
        Assert.Equal(10, p1.Items.Count);
        Assert.Equal(10, p2.Items.Count);
        Assert.Equal(4, p5.Items.Count);
        Assert.Empty(p1.Items.Select(i => i.Id).Intersect(p2.Items.Select(i => i.Id)));
        Assert.Equal(p1.TotalSize, p5.TotalSize); // totalSize 是全部符合条件的，不是当前页
        Assert.Equal(30, (await _center.Query(new FileCenterQuery(), Sup)).PageSize); // 默认 30
        Assert.Equal(30, (await _center.Query(new FileCenterQuery(), Sup)).Items.Count);
        Assert.Equal(200, (await Q(new FileCenterQuery { PageSize = 9999 })).PageSize);
        Assert.Equal(1, (await Q(new FileCenterQuery { PageSize = 0, Page = -3 })).PageSize);
        Assert.Equal(1, (await Q(new FileCenterQuery { PageSize = 0, Page = -3 })).Page);
    }

    [Fact]
    public async Task InvalidParameters()
    {
        await Fails(40000, "文件类型不正确", () => Q(new FileCenterQuery { Type = "Nope" }));
        await Fails(40000, "文件来源不正确", () => Q(new FileCenterQuery { Source = "Avatar" }));
        await Fails(40000, "文件来源不正确", () => Q(new FileCenterQuery { Source = "FactoryReceivePhoto" }));
        await Fails(40000, "可见范围筛选不正确", () => Q(new FileCenterQuery { Restricted = "yes" }));
        await Fails(40000, "开始日期格式应为 yyyy-MM-dd", () => Q(new FileCenterQuery { From = "2026/11/01" }));
        await Fails(40000, "结束日期格式应为 yyyy-MM-dd", () => Q(new FileCenterQuery { To = "11-01" }));
        await Fails(40000, "开始日期不能晚于结束日期", () => Q(new FileCenterQuery { From = "2026-11-06", To = "2026-11-05" }));
    }

    [Fact]
    public async Task VisibilityPerRole()
    {
        var li = U("李工");
        var r = await Q(null, li);
        // 李工能查看的工单：待认领 + 本人负责 / 协助 / 建单
        var visibleOrders = Db.Orders.AsNoTracking().Include(o => o.Assistants).ToList()
            .Where(o => o.Status == OrderStatus.Pending || o.AssigneeId == li.Id || o.CreatorId == li.Id || o.Assistants.Any(a => a.EngineerId == li.Id))
            .Select(o => o.Id).ToHashSet();
        var exp = Expected().Where(f => visibleOrders.Contains(f.OrderId!.Value) && !f.Restricted).ToList();
        Assert.Equal(exp.Count, r.Total);
        Assert.Equal(exp.Sum(f => f.Size), r.TotalSize);
        Assert.Equal(exp.Select(f => f.Id).OrderBy(x => x), r.Items.Select(i => i.Id).OrderBy(x => x));
        Assert.True(r.Total > 0 && r.Total < 40);
        Assert.DoesNotContain(r.Items, i => i.Restricted);
        Assert.DoesNotContain(r.Items, i => i.Name == "报价单.pdf");
        Assert.Contains(r.Items, i => i.Name == "现场情况说明.pdf");
        Assert.DoesNotContain("报价单", Json(r));
        Assert.Equal(1, r.Counts["Document"]);
        Assert.DoesNotContain(r.Items, i => i.CustomerName == "南京某检测中心"); // 陈工的单
        Assert.All(r.Items, i => Assert.False(i.CanToggleRestricted));
        Assert.All(r.Items, i => Assert.False(i.CanDelete)); // 都不是李工上传的附件
        // 工程师按 restricted=true 筛也拿不到
        Assert.Equal(0, (await Q(new FileCenterQuery { Restricted = "true" }, li)).Total);
        Assert.Equal(0, (await Q(new FileCenterQuery { Keyword = "报价" }, li)).Total);

        // 协助人：王工能看到常州某材料厂（刘工负责）的文件
        var wang = await Q(null, U("王工"));
        Assert.Contains(wang.Items, i => i.CustomerName == "常州某材料厂");
        Assert.DoesNotContain(wang.Items, i => i.CustomerName == Jiangsu);

        // 销售：全部，含受限附件；can* 按规则
        foreach (var who in new[] { "王敏", "孙磊" })
        {
            var s = await Q(null, U(who));
            Assert.Equal(40, s.Total);
            var quote = s.Items.Single(i => i.Name == "报价单.pdf");
            Assert.True(quote.Restricted);
            Assert.Equal(who == "王敏", quote.CanDelete); // 上传人、工单未闭环
            Assert.False(quote.CanToggleRestricted);
        }
    }

    [Fact]
    public async Task RowCapabilities()
    {
        var r = await Q();
        Assert.All(r.Items, i =>
        {
            var isAttachment = i.Source is "Attachment" or "OrderPhoto";
            Assert.Equal(isAttachment, i.CanDelete);
            Assert.Equal(isAttachment, i.CanToggleRestricted);
        });
        Assert.Equal(5, r.Items.Count(i => i.CanDelete));

        // 闭环工单上的建单照片：上传人（销售）不能删，主管可以
        var closed = await NewClosed("闭环单");
        var wm = U("王敏");
        await Orders.AddAttachments(closed, Att(await UpPdf(Sup, "结算.pdf")), Sup);
        var row = Assert.Single((await Q(new FileCenterQuery { OrderId = closed, Source = "Attachment" })).Items);
        Assert.True(row.CanDelete);
        Assert.True(row.CanToggleRestricted);
        Assert.Equal("赵主管", row.Uploader!.Name);
        Assert.False(Assert.Single((await Q(new FileCenterQuery { OrderId = closed, Source = "Attachment" }, wm)).Items).CanDelete);
        // 完工照片行：任何人都不能删
        Assert.All((await Q(new FileCenterQuery { OrderId = closed, Source = "ProcessPhoto" })).Items, i => Assert.False(i.CanDelete));
    }

    // ---------- 迁移 ----------

    [Fact]
    public async Task MigrationUpgradesV11DatabaseWithExistingRows()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        db.GetService<IMigrator>().Migrate("V11_PriceHoldAssistFactory");
        Assert.Equal(new[] { "V12_Attachments", "V13_DevicesTransfers" }, db.Database.GetPendingMigrations().Select(m => m[(m.IndexOf('_') + 1)..]));

        db.Database.ExecuteSqlRaw("""
            INSERT INTO Employees (Id, Name, Phone, Role, Department, Enabled, TokenVersion, CreatedAt)
            VALUES (1, '老主管', '13800000001', 'Supervisor', '售后部', 1, 0, '2026-10-01 09:00:00');
            INSERT INTO Employees (Id, Name, Phone, Role, Department, Enabled, TokenVersion, CreatedAt)
            VALUES (2, '老工程师', '13800000002', 'Engineer', '售后部', 1, 0, '2026-10-01 09:00:00');
            INSERT INTO WorkOrders (Id, OrderNo, Status, Source, ProblemType, Urgency, ServiceType, CustomerName, ContactName, ContactPhone,
                Address, City, Region, DeviceModel, DeviceSn, SatDate, SatSigned, FaultTime, Symptom, CustomerTried, SiteEnvironment,
                Price, PriceSet, CreatorId, AssigneeId, CreatedAt)
            VALUES (1, 'SH-20261101-001', 'InProgress', 'SalesReferral', 'Fault', 'Normal', 'Onsite', '老客户', '张工', '13800000000',
                '苏州市', '苏州', '苏州市', 'HTR-500', 'SN1', '2024-08', 1, '昨天', '不加热', '重启', '通风橱',
                1800.0, 1, 1, 2, '2026-11-01 09:00:00');
            INSERT INTO StoredFiles (Id, Path, OriginalName, Category, MimeType, Size, UploaderId, OrderId, CreatedAt)
            VALUES (1, '2026/11/a.jpg', 'tmp_1.jpg', 'OrderPhoto', 'image/jpeg', 1000, 1, 1, '2026-11-01 09:00:00');
            INSERT INTO StoredFiles (Id, Path, OriginalName, Category, MimeType, Size, UploaderId, OrderId, CreatedAt)
            VALUES (2, '2026/11/b.mp4', 'tmp_2.mp4', 'OrderPhoto', 'video/mp4', 5000, 1, 1, '2026-11-01 09:00:01');
            INSERT INTO StoredFiles (Id, Path, OriginalName, Category, MimeType, Size, UploaderId, OrderId, CreatedAt)
            VALUES (3, '2026/11/c.jpg', 'tmp_3.jpg', 'PunchPhoto', 'image/jpeg', 2000, 2, 1, '2026-11-02 10:00:00');
            INSERT INTO StoredFiles (Id, Path, OriginalName, Category, MimeType, Size, UploaderId, OrderId, CreatedAt)
            VALUES (4, '2026/11/d.png', 'me.png', 'Avatar', 'image/png', 300, 2, NULL, '2026-11-02 11:00:00');
            INSERT INTO StatusLogs (OrderId, ActorId, Text, Time, ToStatus) VALUES (1, 1, '老主管 建单', '2026-11-01 09:00:00', 'Pending');
            """);

        db.Database.Migrate();
        Assert.Empty(db.Database.GetPendingMigrations());
        db.ChangeTracker.Clear();

        // 旧文件行：未受限、未删除、无展示名
        var rows = db.Files.IgnoreQueryFilters().AsNoTracking().OrderBy(f => f.Id).ToList();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, f =>
        {
            Assert.False(f.Restricted);
            Assert.Null(f.DeletedAt);
            Assert.Null(f.DeletedById);
            Assert.Null(f.DisplayName);
        });
        Assert.Equal(4, db.Files.Count()); // 全局过滤器下也都在

        // 新索引存在
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_StoredFiles_CreatedAt'";
            Assert.Equal(1L, cmd.ExecuteScalar());
        }

        // 详情与文件中心正常
        var files = new FileService(db, Clock, new Env { ContentRootPath = Root }, new HttpContextAccessor(),
            Options.Create(new AppSettings { PublicBaseUrl = "http://test" }));
        var view = new OrderViewBuilder(db, Clock, files);
        var eng = new CurrentUser(2, Role.Engineer);
        var sup = new CurrentUser(1, Role.Supervisor);
        var d = await view.BuildDetail(1, eng);
        Assert.Equal(2, d.Attachments.Count);
        Assert.Equal(new[] { "建单照片_20261101-090000.jpg", "建单视频_20261101-090001.mp4" }, d.Attachments.Select(a => a.Name));
        Assert.All(d.Attachments, a => Assert.Equal("OrderPhoto", a.Source));
        Assert.Equal(2, d.Files.OrderPhotos.Count);
        Assert.Single(d.Files.PunchPhotos);
        Assert.Equal(48, d.AttachmentRemaining);
        Assert.True(d.CanAddAttachment);

        var page = await new FileCenterService(db, view).Query(new FileCenterQuery(), sup);
        Assert.Equal(3, page.Total);
        Assert.Equal(8000, page.TotalSize);
        Assert.Equal(new Dictionary<string, int> { ["all"] = 3, ["Image"] = 2, ["Video"] = 1, ["Document"] = 0 }, page.Counts);
        Assert.Equal(new[] { 3, 2, 1 }, page.Items.Select(i => i.Id));

        // 升级后的库上新功能可用：旧建单照片可设受限、可软删除
        var orders = new OrderService(db, Clock, view, files, Notifier, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrderService>.Instance);
        d = await orders.SetAttachmentRestricted(1, 1, new RestrictedRequest { Restricted = true }, sup);
        Assert.True(d.Attachments.Single(a => a.Id == 1).Restricted);
        Assert.Single((await view.BuildDetail(1, eng)).Attachments);
        d = await orders.DeleteAttachment(1, 2, sup);
        Assert.Single(d.Attachments);
        Assert.Equal(49, d.AttachmentRemaining);
    }
}
