using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v2.1 每日工作内容；维修登记表只能传到对应服务方式的工单</summary>
public sealed class V21WorkLogTests : V12TestBase
{
    private CurrentUser Wang => U("王工");
    private CurrentUser Liu => U("刘工");

    private PunchRequest P(int orderId, PunchKind kind, string? work = null, int? version = 2) => new()
    {
        OrderId = orderId, Kind = kind, Latitude = 31.30, Longitude = 120.72, Address = "x",
        PhotoFileId = NewFile(Wang, FileCategory.PunchPhoto), FormVersion = version, WorkContent = work
    };

    [Fact]
    public async Task WorkLogIsOnePerPersonPerDayAndEditableOnlyToday()
    {
        var id = await NewAccepted("每日工作客户", service: ServiceType.Remote);
        await Fails(40000, "请填写今天的工作内容", () => Orders.SaveWorkLog(id, new WorkLogRequest { Content = " " }, Wang));
        await Fails(40300, "只有本单的负责人或协助人可以填写工作内容", () => Orders.SaveWorkLog(id, new WorkLogRequest { Content = "x" }, Liu));

        var d = await Orders.SaveWorkLog(id, new WorkLogRequest { Content = "远程排查参数" }, Wang);
        Assert.True(d.CanWriteWorkLog);
        Assert.Equal("远程排查参数", d.MyTodayWorkLog);
        d = await Orders.SaveWorkLog(id, new WorkLogRequest { Content = "远程排查参数，已定位" }, Wang); // 当天再写 = 改
        var log = Assert.Single(d.WorkLogs);
        Assert.Equal(("远程排查参数，已定位", true, "王工"), (log.Content, log.CanEdit, log.Engineer.Name));

        Clock.Now = Clock.Now.AddDays(1);
        d = await Orders.SaveWorkLog(id, new WorkLogRequest { Content = "第二天复查" }, Wang);
        Assert.Equal(new[] { "第二天复查", "远程排查参数，已定位" }, d.WorkLogs.Select(x => x.Content)); // 新的在前
        Assert.Equal(new[] { true, false }, d.WorkLogs.Select(x => x.CanEdit));                        // 昨天的不能再改

        // 主管看得到，但不能写
        var sup = await Orders.Detail(id, Sup);
        Assert.Equal(2, sup.WorkLogs.Count);
        Assert.False(sup.CanWriteWorkLog);
    }

    [Fact]
    public async Task LeavePunchNeedsTodaysWorkContentEachPersonTheirOwn()
    {
        var id = await NewAccepted("离场要写工作客户");
        await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { Liu.Id } }, Sup);
        Punches.Create(Wang, P(id, PunchKind.Arrive)).GetAwaiter().GetResult();
        var liuArrive = P(id, PunchKind.Arrive);
        liuArrive.PhotoFileId = NewFile(Liu, FileCategory.PunchPhoto);
        await Punches.Create(Liu, liuArrive);
        Tick(30);

        await Fails(40000, "请先填写今天的工作内容", () => Punches.Create(Wang, P(id, PunchKind.Leave)));
        await Punches.Create(Wang, P(id, PunchKind.Leave, "更换加热管"));
        // 王工写了不代表刘工写了
        var liuLeave = P(id, PunchKind.Leave);
        liuLeave.PhotoFileId = NewFile(Liu, FileCategory.PunchPhoto);
        await Fails(40000, "请先填写今天的工作内容", () => Punches.Create(Liu, liuLeave));
        liuLeave.WorkContent = "协助拆装";
        liuLeave.PhotoFileId = NewFile(Liu, FileCategory.PunchPhoto);
        await Punches.Create(Liu, liuLeave);

        var d = await Orders.Detail(id, Sup);
        Assert.Equal(new[] { "更换加热管", "协助拆装" }, d.WorkLogs.Select(x => x.Content));

        // 当天再次到场、离场：已经写过，不强制再写；写了就是补充
        Tick(10);
        await Punches.Create(Wang, P(id, PunchKind.Arrive));
        Tick(10);
        Assert.Equal("更换加热管", (await Punches.Context(Wang, id))!.TodayWorkContent);
        await Punches.Create(Wang, P(id, PunchKind.Leave));
        Tick(1);
        await Punches.Create(Wang, P(id, PunchKind.Arrive));
        Tick(5);
        await Punches.Create(Wang, P(id, PunchKind.Leave, "更换加热管，下午复测正常"));
        Assert.Equal("更换加热管，下午复测正常", (await Orders.Detail(id, Wang)).MyTodayWorkLog);
    }

    [Fact]
    public async Task OldClientsCanStillLeaveWithoutWorkContent()
    {
        var id = await NewAccepted("旧版打卡客户");
        await Punches.Create(Wang, P(id, PunchKind.Arrive, version: null));
        Tick(20);
        await Punches.Create(Wang, P(id, PunchKind.Leave, version: null));
        Assert.Empty((await Orders.Detail(id, Wang)).WorkLogs);
    }

    [Fact]
    public async Task CompletionNeedsAtLeastOneWorkLogFromTheLead()
    {
        var id = await NewAccepted("完工要有工作内容客户", service: ServiceType.Remote);
        await Orders.Start(id, Wang);
        var r = new CompleteRequest { FormVersion = 2, FaultCause = "参数错误", Solution = "重设参数", RunStatus = RunStatus.Normal, CustomerConfirm = "电话确认" };
        await Fails(40000, "请填写工作内容", () => Orders.Complete(id, r, Wang));
        r.WorkContent = "远程指导重设参数";
        var d = await Orders.Complete(id, r, Wang);
        Assert.Equal(OrderStatus.Submitted, d.Status);
        Assert.Equal("远程指导重设参数", Assert.Single(d.WorkLogs).Content);

        // 之前写过就不用再写
        var id2 = await NewAccepted("已写过工作内容客户", service: ServiceType.Remote);
        await Orders.Start(id2, Wang);
        await Orders.SaveWorkLog(id2, new WorkLogRequest { Content = "已排查" }, Wang);
        r.WorkContent = null;
        Assert.Equal(OrderStatus.Submitted, (await Orders.Complete(id2, r, Wang)).Status);
    }

    [Fact]
    public async Task RepairFormsFollowTheServiceType()
    {
        var sales = U("王敏");
        AttachmentsRequest Att(string type, int fileId) => new() { Attachments = new() { new AttachmentItem { FileId = fileId, Type = type } } };
        var onsite = await NewPending("上门单");
        var factory = await NewPending("返厂单", ServiceType.Factory);
        var remote = await NewPending("远程单", ServiceType.Remote);

        await Fails(40000, "返厂维修登记表只能在返厂维修的流程里上传", () => Orders.AddAttachments(onsite, Att("FactoryRepairForm", 1), sales));
        await Fails(40000, "现场维修登记表只能在上门服务的工单里上传", () => Orders.AddAttachments(factory, Att("OnsiteRepairForm", 1), sales));
        await Fails(40000, "现场维修登记表只能在上门服务的工单里上传", () => Orders.AddAttachments(remote, Att("OnsiteRepairForm", 1), sales));

        var f1 = await UpPdf(sales, "现场表.pdf");
        var f2 = await UpPdf(sales, "返厂表.pdf");
        Assert.Equal(AttachmentType.OnsiteRepairForm, (await Orders.AddAttachments(onsite, Att("OnsiteRepairForm", f1), sales)).Attachments.Single(a => a.Id == f1).AttachmentType);
        Assert.Equal(AttachmentType.FactoryRepairForm, (await Orders.AddAttachments(factory, Att("FactoryRepairForm", f2), sales)).Attachments.Single(a => a.Id == f2).AttachmentType);

        // 改类别也一样：上门单的附件不能改成返厂维修登记表
        await Fails(40000, "返厂维修登记表只能在返厂维修的流程里上传",
            () => Orders.SetAttachmentType(onsite, f1, new AttachmentTypeRequest { Type = "FactoryRepairForm" }, sales));
    }
}
