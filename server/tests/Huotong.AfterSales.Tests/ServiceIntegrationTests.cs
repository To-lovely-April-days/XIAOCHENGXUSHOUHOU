using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Tests;

/// <summary>基于 SQLite 内存库 + 种子数据的服务级测试</summary>
public sealed class ServiceIntegrationTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly AppDbContext _db;
    private readonly FixedClock _clock = new(new DateTime(2026, 11, 6, 13, 42, 10));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aftersales-test-" + Guid.NewGuid().ToString("N"));
    private readonly FileService _files;
    private readonly OrderViewBuilder _view;
    private readonly OrderService _orders;
    private readonly PunchService _punches;
    private readonly AdminService _admin;

    private class Env : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }

    private class NullNotifier : INotifier
    {
        public int Count;
        public Task NotifyAsync(string title, string text, IEnumerable<string>? atMobiles = null) { Count++; return Task.CompletedTask; }
    }

    public ServiceIntegrationTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        SeedData.RunAsync(_db, Path.Combine(_root, "uploads"), _clock.Now).GetAwaiter().GetResult();
        _db.ChangeTracker.Clear();

        _files = new FileService(_db, _clock, new Env { ContentRootPath = _root }, new HttpContextAccessor(),
            Options.Create(new AppSettings { PublicBaseUrl = "http://test" }));
        _view = new OrderViewBuilder(_db, _clock, _files);
        _orders = new OrderService(_db, _clock, _view, _files, new NullNotifier(), NullLogger<OrderService>.Instance);
        _punches = new PunchService(_db, _clock, _view, _files);
        _admin = new AdminService(_db, _clock, _view, _files);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private CurrentUser U(string name)
    {
        var e = _db.Employees.Single(x => x.Name == name);
        return new CurrentUser(e.Id, e.Role);
    }

    private int OrderId(string customer) => _db.Orders.Single(o => o.CustomerName == customer).Id;

    private static CreateOrderRequest NewOrder(string customer = "测试客户") => new()
    {
        Source = OrderSource.SalesReferral, ProblemType = ProblemType.Fault, Urgency = Urgency.Normal, ServiceType = ServiceType.Onsite,
        CustomerName = customer, ContactName = "张工", ContactPhone = "138 0000 0000", Address = "苏州市工业园区星湖街 1 号",
        Latitude = 31.30, Longitude = 120.72, DeviceModel = "HTR-500", DeviceSn = "SN1", SatDate = "2024-08", SatSigned = true,
        FaultTime = "11月4日 下午", Symptom = "不加热", CustomerTried = "重启", SiteEnvironment = "通风橱", Price = 0
    };

    [Fact]
    public async Task OrderNumbersAreDailySequence()
    {
        var sales = U("王敏");
        // 种子里今天已有 SH-20261106-002
        var a = await _orders.Create(NewOrder("A"), sales);
        var b = await _orders.Create(NewOrder("B"), sales);
        Assert.Equal("SH-20261106-003", a.OrderNo);
        Assert.Equal("SH-20261106-004", b.OrderNo);
        Assert.Equal(OrderStatus.Pending, a.Status);
        Assert.Equal("苏州", a.City);
        Assert.Equal("王敏 建单", Assert.Single(a.Logs).Text);

        _clock.Now = _clock.Now.AddDays(1);
        var c = await _orders.Create(NewOrder("C"), sales);
        Assert.Equal("SH-20261107-001", c.OrderNo);
    }

    [Fact]
    public async Task OnsiteOrderRequiresCoordinates()
    {
        var r = NewOrder();
        r.Latitude = null;
        var ex = await Assert.ThrowsAsync<BizException>(() => _orders.Create(r, U("王敏")));
        Assert.Equal(40000, ex.Code);
    }

    [Fact]
    public async Task ClaimTwiceConflicts()
    {
        var id = OrderId("浙江某新材料公司");
        var d = await _orders.Claim(id, U("王工"));
        Assert.Equal(OrderStatus.Accepted, d.Status);
        Assert.Contains("checkin", d.Actions);
        var ex = await Assert.ThrowsAsync<BizException>(() => _orders.Claim(id, U("刘工")));
        Assert.Equal("工单已被他人认领", ex.Message);
    }

    [Fact]
    public async Task PunchBlockedByOpenArriveOnAnotherOrder()
    {
        var li = U("李工");
        var ctx = await _punches.Context(li, OrderId("南通某试剂厂"));
        Assert.Equal("请先在〈江苏某化工研究院〉离场打卡", ctx!.BlockedReason);

        var noArg = await _punches.Context(li, null);
        Assert.Equal(OrderId("江苏某化工研究院"), noArg!.OrderId);
        Assert.Equal(PunchKind.Leave, noArg.NextKind);
        Assert.Equal(new DateTime(2026, 11, 6, 10, 5, 0), noArg.OpenArriveTime);
    }

    [Fact]
    public async Task CloseBlockedUntilSupplement()
    {
        var sup = U("赵主管");
        var id = OrderId("合肥某研究所");
        var before = await _orders.Detail(id, sup);
        Assert.Contains(IssueType.MissingPunch, before.Issues);
        Assert.DoesNotContain("close", before.Actions);
        Assert.Equal(495, before.TotalMinutes);
        var ex = await Assert.ThrowsAsync<BizException>(() => _orders.Close(id, sup));
        Assert.Equal("请先补录缺卡", ex.Message);

        var missing = before.IssueDetails.Single(i => i.Type == IssueType.MissingPunch);
        var after = await _admin.Supplement(new SupplementRequest
        {
            OrderId = id, ArrivePunchId = missing.PunchId, Kind = PunchKind.Leave,
            Time = new DateTime(2026, 11, 5, 18, 20, 0), Reason = "手机电量耗尽，工程师电话确认约 18:20 离场"
        }, sup);
        Assert.DoesNotContain(IssueType.MissingPunch, after.Issues);
        Assert.Contains("close", after.Actions);
        Assert.Equal(685, after.TotalMinutes); // 11:25
        var supp = after.Punches.Last();
        Assert.True(supp.IsSupplement);
        Assert.Equal(190, supp.PairMinutes);
        Assert.Equal("赵主管", supp.SupplementBy!.Name);
        Assert.Equal("赵主管 补录离场 18:20", after.Logs.Last().Text);

        var closed = await _orders.Close(id, sup);
        Assert.Equal(OrderStatus.Closed, closed.Status);
        Assert.Equal("赵主管 确认闭环 → 已闭环", closed.Logs.Last().Text);
    }

    [Fact]
    public async Task CompleteRequiresLeavingFirst()
    {
        var li = U("李工");
        var ex = await Assert.ThrowsAsync<BizException>(() => _orders.Complete(OrderId("江苏某化工研究院"), new CompleteRequest
        {
            FaultCause = "x", Solution = "y", RunStatus = RunStatus.Normal, ProcessPhotoIds = new() { 1 }, SignaturePhotoIds = new() { 2 }
        }, li));
        Assert.Equal("请先离场打卡", ex.Message);
    }

    [Fact]
    public async Task QueryCountsOnlyAffectedByKeyword()
    {
        var all = await _orders.Query(new OrderQuery { Status = "Pending" });
        Assert.Equal(3, all.Total);
        Assert.Equal(10, all.Counts["all"]);
        Assert.Equal(3, all.Counts["Pending"]);
        var kw = await _orders.Query(new OrderQuery { Keyword = "合肥" });
        Assert.Equal(1, kw.Counts["all"]);
        var issue = await _orders.Query(new OrderQuery { Issue = "DistanceAbnormal" });
        Assert.Equal("南京某检测中心", Assert.Single(issue.Items).CustomerName);
    }

    private int NewFile(CurrentUser u, FileCategory cat)
    {
        var f = new StoredFile { Path = "t/" + Guid.NewGuid().ToString("N") + ".jpg", OriginalName = "t.jpg", Category = cat, MimeType = "image/jpeg",
            Size = 1, UploaderId = u.Id, CreatedAt = _clock.Now };
        _db.Files.Add(f);
        _db.SaveChanges();
        return f.Id;
    }

    private async Task Leave(CurrentUser u, int orderId) =>
        await _punches.Create(u, new PunchRequest { OrderId = orderId, Kind = PunchKind.Leave, Latitude = 31.30, Longitude = 120.72,
            Address = "x", PhotoFileId = NewFile(u, FileCategory.PunchPhoto) });

    [Fact]
    public async Task HoldBlockedWhileEngineerOnSite()
    {
        var sup = U("赵主管");
        var li = U("李工");
        var id = OrderId("江苏某化工研究院");
        var ex = await Assert.ThrowsAsync<BizException>(() => _orders.Hold(id, new ReasonRequest { Reason = "等待备件" }, sup));
        Assert.Equal(40900, ex.Code);
        Assert.Equal("工程师正在现场计时，请先离场打卡后再搁置", ex.Message);

        _clock.Now = _clock.Now.AddMinutes(5);
        await Leave(li, id);
        var held = await _orders.Hold(id, new ReasonRequest { Reason = "等待备件" }, sup);
        Assert.Equal(OrderStatus.OnHold, held.Status);
    }

    [Fact]
    public async Task LeaveAllowedOnHeldOrderWithOpenArrive()
    {
        var li = U("李工");
        var id = OrderId("江苏某化工研究院");
        // 旧数据：工单已搁置但仍有未离场的到场卡
        var o = _db.Orders.Single(x => x.Id == id);
        o.Status = OrderStatus.OnHold;
        _db.SaveChanges();
        var ctx = await _punches.Context(li, null);
        Assert.Equal(id, ctx!.OrderId);
        Assert.Equal(PunchKind.Leave, ctx.NextKind);
        Assert.Null(ctx.BlockedReason);
        await Leave(li, id);
        var after = await _punches.Context(li, id);
        Assert.Equal(PunchKind.Arrive, after!.NextKind);
        Assert.Equal("工单当前为「暂停搁置」，不能打卡", after.BlockedReason);
    }

    [Fact]
    public async Task ResubmitReplacesCompletionFilesAndAllowsReassignee()
    {
        var sup = U("赵主管");
        var li = U("李工");
        var chen = U("陈工");
        var id = OrderId("江苏某化工研究院");
        _clock.Now = _clock.Now.AddMinutes(5);
        await Leave(li, id);

        var p1 = NewFile(li, FileCategory.ProcessPhoto);
        var p2 = NewFile(li, FileCategory.ProcessPhoto);
        var s1 = NewFile(li, FileCategory.ProcessPhoto);
        var req = new CompleteRequest { FaultCause = "加热管老化", Solution = "更换加热管", RunStatus = RunStatus.Normal,
            ProcessPhotoIds = new() { p1, p2 }, SignaturePhotoIds = new() { s1 } };
        var d1 = await _orders.Complete(id, req, li);
        Assert.Equal(new[] { p1, p2 }, d1.Files.ProcessPhotos.Select(f => f.Id));
        Assert.Equal(new[] { s1 }, d1.Files.Signatures.Select(f => f.Id));

        await _orders.Reject(id, new ReasonRequest { Reason = "照片不清晰" }, sup);
        req.ProcessPhotoIds = new() { p1 };
        var d2 = await _orders.Complete(id, req, li);
        Assert.Equal(new[] { p1 }, d2.Files.ProcessPhotos.Select(f => f.Id));
        Assert.Null(_db.Files.AsNoTracking().Single(f => f.Id == p2).OrderId);

        await _orders.Reject(id, new ReasonRequest { Reason = "改派" }, sup);
        await _orders.Assign(id, new AssignRequest { EngineerId = chen.Id }, sup);
        var p3 = NewFile(chen, FileCategory.ProcessPhoto);
        req.ProcessPhotoIds = new() { p1, p3 };
        var d3 = await _orders.Complete(id, req, chen);
        Assert.Equal(OrderStatus.Submitted, d3.Status);
        Assert.Equal(new[] { p1, p3 }, d3.Files.ProcessPhotos.Select(f => f.Id));

        // 未挂在本单的他人文件仍不允许
        await _orders.Reject(id, new ReasonRequest(), sup);
        req.ProcessPhotoIds = new() { p1, NewFile(li, FileCategory.ProcessPhoto) };
        var ex = await Assert.ThrowsAsync<BizException>(() => _orders.Complete(id, req, chen));
        Assert.Equal("处理照片不是本人上传，请重新上传", ex.Message);
    }

    [Fact]
    public async Task PunchEntryPicksSameAcceptedOrderAsHome()
    {
        var wang = U("王工");
        var mk = async (string c, DateTime? appt) =>
        {
            var o = await _orders.Create(NewOrder(c), U("王敏"));
            await _orders.Claim(o.Id, wang);
            await _orders.Appointment(o.Id, new AppointmentRequest { AppointmentTime = appt }, wang);
            return o.Id;
        };
        var a = await mk("过期预约客户", _clock.Now.Date.AddDays(-1).AddHours(10));
        await mk("今日预约客户", _clock.Now.Date.AddHours(14));
        var ctx = await _punches.Context(wang, null);
        Assert.Equal(a, ctx!.OrderId);
    }
}
