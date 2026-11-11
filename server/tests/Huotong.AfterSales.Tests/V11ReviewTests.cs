using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.1 评审补充的回归测试：并发重复提交、照片挪用、协助人越权</summary>
public sealed class V11ReviewTests : V11TestBase
{
    /// <summary>文件库上的一套独立连接 + 服务（模拟另一个并发请求）</summary>
    private sealed class Stack : IDisposable
    {
        public readonly SqliteConnection Conn;
        public readonly AppDbContext Db;
        public readonly OrderService Orders;

        public Stack(string path, string root, FixedClock clock, INotifier notifier)
        {
            Conn = new SqliteConnection($"Data Source={path}");
            Conn.Open();
            Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Conn).Options);
            var files = new FileService(Db, clock, new Env { ContentRootPath = root }, new HttpContextAccessor(),
                Options.Create(new AppSettings { PublicBaseUrl = "http://test" }));
            var view = new OrderViewBuilder(Db, clock, files);
            Orders = new OrderService(Db, clock, view, files, notifier, NullLogger<OrderService>.Instance);
        }

        public CurrentUser User(string name)
        {
            var e = Db.Employees.AsNoTracking().Single(x => x.Name == name);
            return new CurrentUser(e.Id, e.Role);
        }

        public void Dispose()
        {
            Db.Dispose();
            Conn.Dispose();
        }
    }

    private async Task WithTwoConnections(Func<Stack, Stack, Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aftersales-v11-race-{Guid.NewGuid():N}.db");
        try
        {
            using var a = new Stack(path, Root, Clock, Notifier);
            a.Db.Database.Migrate();
            await SeedData.RunAsync(a.Db, Path.Combine(Root, "uploads"), Clock.Now);
            a.Db.ChangeTracker.Clear();
            using var b = new Stack(path, Root, Clock, Notifier);
            await body(a, b);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task ConcurrentHoldRequestsDoNotCreateTwoPending() => await WithTwoConnections(async (a, b) =>
    {
        var wang = a.User("王工");
        var id = (await a.Orders.Create(NewOrder("并发暂停申请客户"), a.User("王敏"))).Id;
        await a.Orders.Claim(id, wang);
        await b.Orders.Detail(id, wang); // 预热连接 b
        b.Db.ChangeTracker.Clear(); // 真实请求每次都是新的 DbContext

        // 连接 a 已取得写锁并写入一条待审批的暂停申请，尚未提交（= 第一次点击正在处理）
        var tx = await a.Db.Database.BeginTransactionAsync();
        a.Db.OrderRequests.Add(new OrderRequest
        {
            OrderId = id, Type = OrderRequestType.Hold, Status = RequestStatus.Pending, RequesterId = wang.Id, CreatedAt = Clock.Now,
            HoldReasonType = HoldReasonType.WaitParts
        });
        await a.Db.SaveChangesAsync();

        // 连接 b 的第二次提交必须排队，等 a 提交后看到已有申请
        var second = Task.Run(() => b.Orders.RequestHold(id, HoldBody("WaitParts"), wang));
        await Task.Delay(500);
        Assert.False(second.IsCompleted);
        await tx.CommitAsync();
        var ex = await Assert.ThrowsAsync<BizException>(() => second);
        Assert.Equal("已有待审批的暂停申请", ex.Message);
        Assert.Equal(40900, ex.Code);
        Assert.Equal(1, await a.Db.OrderRequests.CountAsync(r => r.OrderId == id && r.Status == RequestStatus.Pending));
    });

    [Fact]
    public async Task ConcurrentApproveAndFactoryRecordAreSerialized() => await WithTwoConnections(async (a, b) =>
    {
        var wang = a.User("王工");
        var liu = a.User("刘工");
        var sup = a.User("赵主管");
        var id = (await a.Orders.Create(NewOrder("并发审批客户", ServiceType.Factory), a.User("王敏"))).Id;
        await a.Orders.Claim(id, wang);
        var rid = (await a.Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = liu.Id }, wang)).Requests.Single().Id;
        await b.Orders.Detail(id, sup);
        b.Db.ChangeTracker.Clear(); // 真实请求每次都是新的 DbContext

        // a 正在同意申请（已写入协助人、未提交）；b 的重复同意要等 a 提交后得到「该申请已处理」，而不是撞唯一索引
        var tx = await a.Db.Database.BeginTransactionAsync();
        a.Db.OrderAssistants.Add(new OrderAssistant { OrderId = id, EngineerId = liu.Id, AddedById = sup.Id, AddedAt = Clock.Now });
        var req = await a.Db.OrderRequests.SingleAsync(r => r.Id == rid);
        req.Status = RequestStatus.Approved;
        await a.Db.SaveChangesAsync();
        var second = Task.Run(() => b.Orders.ApproveRequest(id, rid, sup));
        await Task.Delay(500);
        Assert.False(second.IsCompleted);
        await tx.CommitAsync();
        Assert.Equal("该申请已处理", (await Assert.ThrowsAsync<BizException>(() => second)).Message);

        // 返厂节点：a 正在首次记录客户寄出；b 的重复提交排队后按「更正」处理，仍只有 1 行
        a.Db.ChangeTracker.Clear();
        tx = await a.Db.Database.BeginTransactionAsync();
        a.Db.FactoryRecords.Add(new FactoryRecord
        {
            OrderId = id, Node = FactoryNode.CustomerShipped, OperatorId = wang.Id, Time = Clock.Now, Courier = "顺丰", TrackingNo = "SF1"
        });
        await a.Db.SaveChangesAsync();
        b.Db.ChangeTracker.Clear();
        var again = Task.Run(() => b.Orders.RecordFactoryNode(id, Node(FactoryNode.CustomerShipped, "顺丰", "SF1"), wang));
        await Task.Delay(500);
        Assert.False(again.IsCompleted);
        await tx.CommitAsync();
        var d = await again;
        Assert.Equal("王工 更正客户寄出（顺丰 SF1）", d.Logs.Last().Text);
        Assert.Equal(1, await a.Db.FactoryRecords.CountAsync(r => r.OrderId == id));
    });

    [Fact]
    public async Task FactoryPhotoCannotBeTakenFromAnotherNode()
    {
        var id = await NewAccepted("返厂照片挪用客户", "王工", ServiceType.Factory);
        var receive = NewFile(Sup, FileCategory.FactoryReceivePhoto);
        var ship = NewFile(Sup, FileCategory.FactoryShipPhoto);
        await Orders.RecordFactoryNode(id, Node(FactoryNode.FactoryReceived, null, null, receive), Sup); // 主管可跳过客户寄出
        await Orders.RecordFactoryNode(id, Node(FactoryNode.ShippedBack, "顺丰", "SF9", ship), Sup);

        // 更正工厂收货时把寄回照片的 id 传进来：不能把寄回节点的必填照片抽走
        await Fails(40000, "收货照片已用于本单其他资料，请重新上传",
            () => Orders.RecordFactoryNode(id, Node(FactoryNode.FactoryReceived, null, null, ship), Sup));
        Db.ChangeTracker.Clear();
        var d = await Orders.Detail(id, Sup);
        Assert.Equal(new[] { receive }, d.Factory!.Nodes[1].Photos.Select(f => f.Id));
        Assert.Equal(new[] { ship }, d.Factory.Nodes[3].Photos.Select(f => f.Id));

        // 正常更正（同节点已有照片 + 新照片）不受影响
        var extra = NewFile(Sup, FileCategory.FactoryReceivePhoto);
        d = await Orders.RecordFactoryNode(id, Node(FactoryNode.FactoryReceived, null, null, receive, extra), Sup);
        Assert.Equal(new[] { receive, extra }, d.Factory!.Nodes[1].Photos.Select(f => f.Id));
    }

    [Fact]
    public async Task AssistantAndOutsidersCannotDoLeadOrSupervisorActions()
    {
        var wang = U("王工");
        var liu = U("刘工");
        var chen = U("陈工");
        var id = await NewAccepted("协助人越权客户", "王工", ServiceType.Remote);
        await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { liu.Id } }, Sup);

        // 协助人：只能看，不能做负责人的动作
        Assert.Empty((await Orders.Detail(id, liu)).Actions);
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Start(id, liu));
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Appointment(id, new AppointmentRequest { AppointmentTime = Clock.Now.AddDays(1) }, liu));
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Complete(id, Done(liu, false), liu));
        await Fails(40300, "只有负责工程师可以申请暂停", () => Orders.RequestHold(id, HoldBody("WaitParts"), liu));
        await Fails(40300, "只有负责工程师可以申请协助人", () => Orders.RequestAssistant(id, new AssistantRequestRequest { EngineerId = chen.Id }, liu));
        await Fails(40900, "远程/返厂工单无需打卡", () => Arrive(liu, id));

        // 无关工程师：看不了、打不了卡
        await Fails(40300, "无权查看该工单", () => Orders.Detail(id, chen));
        await Fails(40300, "只有本单的负责人或协助人可以打卡", () => Arrive(chen, id));

        // 申请只能由本人撤回、只能由主管审批；工程师 / 销售不能定价、暂停、恢复、调整协助人
        var rid = (await Orders.RequestHold(id, HoldBody("WaitParts"), wang)).Requests.Single().Id;
        await Fails(40300, "只能撤回本人的申请", () => Orders.CancelRequest(id, rid, liu));
        foreach (var who in new[] { wang, liu, U("王敏") })
        {
            Assert.Equal(40300, (await Biz(() => Orders.ApproveRequest(id, rid, who))).Code);
            Assert.Equal(40300, (await Biz(() => Orders.RejectRequest(id, rid, new ReplyRequest(), who))).Code);
            Assert.Equal(40300, (await Biz(() => Orders.Hold(id, HoldBody("WaitParts"), who))).Code);
            Assert.Equal(40300, (await Biz(() => Orders.Resume(id, who))).Code);
            Assert.Equal(40300, (await Biz(() => Orders.SetPrice(id, new PriceRequest { Price = 1 }, who))).Code);
            Assert.Equal(40300, (await Biz(() => Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() }, who))).Code);
        }
        Assert.Equal(40300, (await Biz(() => Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 1 }, wang))).Code);
        Assert.Equal(40300, (await Biz(() => Orders.SuggestPrice(id, new PriceSuggestRequest { Amount = 1 }, Sup))).Code);
        Db.ChangeTracker.Clear();
        var d = await Orders.Detail(id, Sup);
        Assert.Equal(OrderStatus.Accepted, d.Status);
        Assert.False(d.PriceSet);
        Assert.Equal(RequestStatus.Pending, d.Requests.Single().Status);

        // 返厂单：协助人不能记录任何节点；非建单销售不能记录节点 1
        var fid = await NewAccepted("协助人返厂越权客户", "王工", ServiceType.Factory);
        await Orders.SetAssistants(fid, new AssistantsRequest { EngineerIds = new() { liu.Id } }, Sup);
        await Fails(40300, "只有建单人、负责工程师或主管可以记录此节点",
            () => Orders.RecordFactoryNode(fid, Node(FactoryNode.CustomerShipped, "顺丰", "SF1"), liu));
        await Fails(40300, "只有负责工程师或主管可以记录此节点",
            () => Orders.RecordFactoryNode(fid, Node(FactoryNode.FactoryReceived, null, null, NewFile(liu, FileCategory.FactoryReceivePhoto)), liu));
        await Fails(40300, "只有建单人、负责工程师或主管可以记录此节点",
            () => Orders.RecordFactoryNode(fid, Node(FactoryNode.CustomerShipped, "顺丰", "SF1"), U("孙磊")));
        Assert.Empty((await Orders.Detail(fid, liu)).Actions);
    }
}
