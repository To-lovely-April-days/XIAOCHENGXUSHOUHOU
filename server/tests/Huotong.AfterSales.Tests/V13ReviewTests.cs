using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.3 审查后补的回归测试（每个用例对应一个已修复的缺陷）</summary>
public sealed class V13ReviewTests : V13TestBase
{
    private CurrentUser Wang => U("王工");
    private CurrentUser Liu => U("刘工");
    private CurrentUser Chen => U("陈工");
    private CurrentUser Wm => U("王敏");

    private static AssistantsRequest Set(params CurrentUser[] users) => new() { EngineerIds = users.Select(u => u.Id).ToList() };
    private static AttachmentTypeRequest T(string type) => new() { Type = type };

    // ---------- 待确认转交的接手人在确认前只读：「上传人本人」不能成为写权限 ----------

    /// <summary>
    /// 缺陷：刘工曾是本单协助人并上传过附件，被移除后本来看不了工单；王工把单子转交给他（待确认）后，
    /// 他靠「待确认接手人」的只读查看权 +「上传人本人」这一条，就能在确认接手之前删除 / 改类别自己以前传的附件。
    /// 契约 §2.8 / §12：接手人确认前没有任何写权限。
    /// </summary>
    [Fact]
    public async Task PendingRecipientCannotDeleteOrRetypeHisEarlierUploads()
    {
        var id = await NewAccepted("接手前只读客户", "王工");
        await Orders.SetAssistants(id, Set(Liu), Sup);
        var mine = await UpPdf(Liu, "刘工的现场记录.pdf");
        await Orders.AddAttachments(id, Att(mine), Liu);
        var others = await UpPdf(Wang, "王工的记录.pdf");
        await Orders.AddAttachments(id, Att(others), Wang);
        await Orders.SetAssistants(id, Set(), Sup);
        await Fails(40300, "无权查看该工单", () => Orders.Detail(id, Liu));

        var tid = await StartTransfer(id, Wang, Liu);

        var d = await Orders.Detail(id, Liu);
        Assert.Equal(new[] { "acceptTransfer", "rejectTransfer" }, d.Actions);
        Assert.False(d.CanAddAttachment);
        Assert.All(d.Attachments, a => Assert.False(a.CanDelete || a.CanChangeType));
        await Fails(40300, "只有建单人、负责工程师、协助人或主管可以删除附件", () => Orders.DeleteAttachment(id, mine, Liu));
        await Fails(40300, "只有建单人、负责工程师、协助人或主管可以修改附件类别", () => Orders.SetAttachmentType(id, mine, T("Sat"), Liu));
        // 不是自己上传的：文案与契约 §6.1 的表一致，不变
        await Fails(40300, "只能删除自己上传的附件", () => Orders.DeleteAttachment(id, others, Liu));
        await Fails(40300, "只能修改自己上传的附件", () => Orders.SetAttachmentType(id, others, T("Sat"), Liu));
        Db.ChangeTracker.Clear();
        var row = FileRow(mine);
        Assert.Null(row.DeletedAt);
        Assert.Equal(AttachmentType.Other, row.AttachmentType);
        // 主管视角不受影响
        Assert.All((await Orders.Detail(id, Sup)).Attachments, a => Assert.True(a.CanDelete && a.CanChangeType));

        // 确认接手后他是负责人：恢复「上传人本人」的权限
        d = await Orders.AcceptTransfer(id, tid, Liu);
        var own = d.Attachments.Single(a => a.Id == mine);
        Assert.True(own.CanDelete);
        Assert.True(own.CanChangeType);
        d = await Orders.SetAttachmentType(id, mine, T("RepairForm"), Liu);
        Assert.Equal(AttachmentType.RepairForm, d.Attachments.Single(a => a.Id == mine).AttachmentType);
        d = await Orders.DeleteAttachment(id, mine, Liu);
        Assert.DoesNotContain(d.Attachments, a => a.Id == mine);
        AssertInvariants();
    }

    /// <summary>上面的收紧只针对「不是参与方」的接手人：接手人本来就是建单人时，建单人的附件权限照旧</summary>
    [Fact]
    public async Task PendingRecipientWhoIsCreatorKeepsCreatorAttachmentRights()
    {
        // 陈工自己建单，王工认领后又转交给陈工（待确认）
        var id = await NewAccepted("建单人被转交客户", "王工", creator: "陈工");
        var f = await UpPdf(Chen, "建单人补充.pdf");
        await Orders.AddAttachments(id, Att(f), Chen);
        await StartTransfer(id, Wang, Chen);

        var d = await Orders.Detail(id, Chen);
        Assert.Equal(new[] { "acceptTransfer", "rejectTransfer" }, d.Actions);
        Assert.True(d.CanAddAttachment);
        var a = d.Attachments.Single(x => x.Id == f);
        Assert.True(a.CanDelete);
        Assert.True(a.CanChangeType);
        d = await Orders.SetAttachmentType(id, f, T("Sat"), Chen);
        Assert.Equal(AttachmentType.Sat, d.Attachments.Single(x => x.Id == f).AttachmentType);
        d = await Orders.DeleteAttachment(id, f, Chen);
        Assert.DoesNotContain(d.Attachments, x => x.Id == f);
        AssertInvariants();
    }

    // ---------- 主管调整协助人与「确认接手」排队（写锁） ----------

    /// <summary>
    /// 调整协助人进了写锁之后，校验顺序与文案不变；没有变化时不写记录、不发通知。
    /// （并发用例见 V13TransferTests.ConcurrentSetAssistantsWaitsForAccept。）
    /// </summary>
    [Fact]
    public async Task SetAssistantsKeepsValidationOrderAndNoOpBehaviour()
    {
        var id = await NewAccepted("协助人写锁客户", "王工");
        await Fails(40300, "只有主管可以调整协助人", () => Orders.SetAssistants(id, Set(Liu), Wang));
        await Fails(40000, "请选择协助人", () => Orders.SetAssistants(id, new AssistantsRequest(), Sup));
        await Fails(40400, "工单不存在", () => Orders.SetAssistants(999999, Set(Liu), Sup));
        await Fails(40000, "协助人不能是负责人", () => Orders.SetAssistants(id, Set(Wang), Sup));
        // 失败的调用不能把事务留在 DbContext 上：随后的写操作照常成功
        var d = await Orders.SetAssistants(id, Set(Liu), Sup);
        Assert.Equal(new[] { "刘工" }, d.Assistants.Select(x => x.Name));
        var logs = d.Logs.Count;
        var sent = Notifier.Sent.Count;
        d = await Orders.SetAssistants(id, Set(Liu), Sup); // 没有变化
        Assert.Equal(logs, d.Logs.Count);
        Assert.Equal(sent, Notifier.Sent.Count);
        Assert.Null(Db.Database.CurrentTransaction);

        var pending = await NewPending("协助人写锁客户-待认领");
        await Fails(40900, "工单当前为「待认领」，不能调整协助人", () => Orders.SetAssistants(pending, Set(Liu), Sup));
        Assert.Null(Db.Database.CurrentTransaction);
        AssertInvariants();
    }

    // ---------- 验收补充：按列表上显示的设备汇总文字整串搜索 ----------

    /// <summary>
    /// 名称与型号分开存，列表上显示的是「名称 型号」（deviceText）；照着列表上的字整串搜也必须能搜到，
    /// 逐项的子串匹配不受影响。
    /// </summary>
    [Fact]
    public async Task SearchMatchesDeviceSummaryText()
    {
        var id = (await Orders.Create(NewOrder13("汇总文字搜索客户", ServiceType.Onsite,
            Dev("高温反应釜", "SN-SUM-1", "HTX-777"), Dev("循环泵", "SN-SUM-2", "CP-12")), Wm)).Id;
        async Task<int[]> Find(string k) => (await Orders.Query(new OrderQuery { Keyword = k, PageSize = 200 }, Sup)).Items.Select(i => i.Id).ToArray();

        Assert.Equal("高温反应釜 HTX-777 等 2 台", OrderRow(id).DeviceText);
        Assert.Equal(new[] { id }, await Find("高温反应釜 HTX-777"));          // 名称 + 型号整串
        Assert.Equal(new[] { id }, await Find("高温反应釜 HTX-777 等 2 台"));  // 列表上显示的全文
        Assert.Equal(new[] { id }, await Find("循环泵"));                      // 逐项匹配照旧
        Assert.Equal(new[] { id }, await Find("SN-SUM-2"));
        Assert.Empty(await Find("循环泵 CP-12 等"));
    }

    // ---------- 验收补充：预约 / 开始处理进写锁后，校验顺序与文案不变、失败不留事务 ----------

    [Fact]
    public async Task AppointmentAndStartKeepValidationOrderInsideWriteLock()
    {
        var id = (await Orders.Create(NewOrder13("预约写锁客户", ServiceType.Remote), Wm)).Id;
        await Orders.Claim(id, Wang);
        var appt = new AppointmentRequest { AppointmentTime = Clock.Now.AddDays(1) };
        await Fails(40400, "工单不存在", () => Orders.Appointment(999999, appt, Wang));
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Appointment(id, appt, Liu));
        await Fails(40000, "请选择预约时间", () => Orders.Appointment(id, new AppointmentRequest(), Wang));
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Start(id, Liu));
        Assert.Null(Db.Database.CurrentTransaction);
        // 失败的调用不能把事务留在 DbContext 上：随后的写操作照常成功
        Assert.NotNull((await Orders.Appointment(id, appt, Wang)).AppointmentTime);
        Assert.Equal(OrderStatus.InProgress, (await Orders.Start(id, Wang)).Status);
        Assert.Null(Db.Database.CurrentTransaction);

        // 转交出去之后：原负责人的预约 / 开始处理被拦，不会再落下他的记录
        await StartTransfer(id, Wang, Liu);
        var tid = OrderRow(id).PendingTransferToId;
        Assert.Equal(Liu.Id, tid);
        await Orders.AcceptTransfer(id, Db.OrderTransfers.AsNoTracking().Single(t => t.OrderId == id).Id, Liu);
        var logs = (await Orders.Detail(id, Sup)).Logs.Count;
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Appointment(id, appt, Wang));
        await Fails(40300, "只有负责工程师可以操作", () => Orders.Start(id, Wang));
        Assert.Equal(logs, (await Orders.Detail(id, Sup)).Logs.Count);
        Assert.Null(Db.Database.CurrentTransaction);
        AssertInvariants();
    }
}
