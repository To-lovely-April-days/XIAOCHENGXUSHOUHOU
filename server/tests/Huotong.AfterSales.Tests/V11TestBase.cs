using System.Text.Json;
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

/// <summary>v1.1 服务级测试的公共夹具：SQLite 内存库 + 种子数据 + 全部服务 + 可断言的通知记录</summary>
public abstract class V11TestBase : IDisposable
{
    protected readonly SqliteConnection Conn;
    protected readonly AppDbContext Db;
    protected readonly FixedClock Clock = new(new DateTime(2026, 11, 6, 13, 42, 10));
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "aftersales-v11-test-" + Guid.NewGuid().ToString("N"));
    protected readonly FileService Files;
    protected readonly OrderViewBuilder View;
    protected readonly OrderService Orders;
    protected readonly PunchService Punches;
    protected readonly AdminService Admin;
    protected readonly HoursService Hours;
    protected readonly HomeService Home;
    protected readonly AdminReportService Reports;
    protected readonly RecordingNotifier Notifier = new();

    protected class Env : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }

    public record Notice(string Title, string Text, List<string> Mobiles);

    public class RecordingNotifier : INotifier
    {
        public List<Notice> Sent { get; } = new();
        public Notice Last => Sent[^1];
        public Task NotifyAsync(string title, string text, IEnumerable<string>? atMobiles = null)
        {
            Sent.Add(new Notice(title, text, atMobiles?.ToList() ?? new()));
            return Task.CompletedTask;
        }
    }

    protected V11TestBase()
    {
        Conn = new SqliteConnection("Data Source=:memory:");
        Conn.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Conn).Options);
        Db.Database.Migrate();
        SeedData.RunAsync(Db, Path.Combine(Root, "uploads"), Clock.Now).GetAwaiter().GetResult();
        Db.ChangeTracker.Clear();

        Files = new FileService(Db, Clock, new Env { ContentRootPath = Root }, new HttpContextAccessor(),
            Options.Create(new AppSettings { PublicBaseUrl = "http://test" }));
        View = new OrderViewBuilder(Db, Clock, Files);
        Orders = new OrderService(Db, Clock, View, Files, Notifier, NullLogger<OrderService>.Instance);
        Punches = new PunchService(Db, Clock, View, Files);
        Admin = new AdminService(Db, Clock, View, Files);
        Hours = new HoursService(Db, Clock, View);
        Home = new HomeService(Db, Clock, View, Hours, Orders);
        Reports = new AdminReportService(Db, Clock, View, Hours);
    }

    public void Dispose()
    {
        Db.Dispose();
        Conn.Dispose();
        try { Directory.Delete(Root, true); } catch { }
    }

    protected Employee Emp(string name) => Db.Employees.AsNoTracking().Single(x => x.Name == name);

    protected CurrentUser U(string name)
    {
        var e = Emp(name);
        return new CurrentUser(e.Id, e.Role);
    }

    protected CurrentUser Sup => U("赵主管");

    protected int OrderId(string customer) => Db.Orders.AsNoTracking().Single(o => o.CustomerName == customer).Id;

    protected void Tick(int minutes = 1) => Clock.Now = Clock.Now.AddMinutes(minutes);

    protected static async Task<BizException> Biz(Func<Task> f) => await Assert.ThrowsAsync<BizException>(f);

    protected static async Task Fails(int code, string message, Func<Task> f)
    {
        var ex = await Biz(f);
        Assert.Equal(message, ex.Message);
        Assert.Equal(code, ex.Code);
    }

    protected static CreateOrderRequest NewOrder(string customer = "测试客户", ServiceType service = ServiceType.Onsite) => new()
    {
        Source = OrderSource.SalesReferral, ProblemType = ProblemType.Fault, Urgency = Urgency.Normal, ServiceType = service,
        CustomerName = customer, ContactName = "张工", ContactPhone = "138 0000 0000", Address = "苏州市工业园区星湖街 1 号",
        Latitude = 31.30, Longitude = 120.72, DeviceModel = "HTR-500", DeviceSn = "SN1", SatDate = "2024-08", SatSigned = true, ProjectNo = "P-TEST",
        FaultTime = "11月4日 下午", Symptom = "不加热", CustomerTried = "重启", SiteEnvironment = "通风橱"
    };

    /// <summary>销售建单（待认领）</summary>
    protected async Task<int> NewPending(string customer, ServiceType service = ServiceType.Onsite, string creator = "王敏") =>
        (await Orders.Create(NewOrder(customer, service), U(creator))).Id;

    /// <summary>销售建单 + 工程师认领（待启动）</summary>
    protected async Task<int> NewAccepted(string customer, string lead = "王工", ServiceType service = ServiceType.Onsite, string creator = "王敏")
    {
        var id = await NewPending(customer, service, creator);
        await Orders.Claim(id, U(lead));
        return id;
    }

    protected int NewFile(CurrentUser u, FileCategory cat)
    {
        var f = new StoredFile
        {
            Path = "t/" + Guid.NewGuid().ToString("N") + ".jpg", OriginalName = "t.jpg", Category = cat, MimeType = "image/jpeg",
            Size = 1, UploaderId = u.Id, CreatedAt = Clock.Now
        };
        Db.Files.Add(f);
        Db.SaveChanges();
        return f.Id;
    }

    protected Task<PunchResultDto> Punch(CurrentUser u, int orderId, PunchKind kind) =>
        Punches.Create(u, new PunchRequest
        {
            OrderId = orderId, Kind = kind, Latitude = 31.30, Longitude = 120.72, Address = "x", PhotoFileId = NewFile(u, FileCategory.PunchPhoto)
        });

    protected Task<PunchResultDto> Arrive(CurrentUser u, int orderId) => Punch(u, orderId, PunchKind.Arrive);
    protected Task<PunchResultDto> Leave(CurrentUser u, int orderId) => Punch(u, orderId, PunchKind.Leave);

    protected CompleteRequest Done(CurrentUser u, bool signature = true) => new()
    {
        FaultCause = "加热管老化", Solution = "更换加热管", RunStatus = RunStatus.Normal,
        ProcessPhotoIds = new() { NewFile(u, FileCategory.ProcessPhoto) },
        SignaturePhotoIds = signature ? new() { NewFile(u, FileCategory.Signature) } : new()
    };

    protected static HoldRequest HoldBody(string? type, string? note = null) => new() { ReasonType = type, Note = note };

    protected static FactoryNodeRequest Node(FactoryNode node, string? courier = null, string? trackingNo = null, params int[] photos) => new()
    {
        Node = node.ToString(), Courier = courier, TrackingNo = trackingNo, PhotoIds = photos.ToList()
    };

    protected List<OrderRequest> RequestsOf(int orderId) =>
        Db.OrderRequests.AsNoTracking().Where(r => r.OrderId == orderId).OrderBy(r => r.Id).ToList();

    protected static string Json(object o)
    {
        var opts = new JsonSerializerOptions();
        JsonSetup.Configure(opts);
        return JsonSerializer.Serialize(o, opts);
    }

    protected static string[] States(OrderDetail d) => d.Factory!.Nodes.Select(n => n.State).ToArray();
}
