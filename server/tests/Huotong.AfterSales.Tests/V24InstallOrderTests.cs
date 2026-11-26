using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v2.4 安装调试工单：售后自己建、自己负责到底，不收费，送货单 / 验收报告</summary>
public sealed class V24InstallOrderTests : V12TestBase
{
    private CurrentUser Wang => U("王工");
    private CurrentUser Liu => U("刘工");
    private CurrentUser Wm => U("王敏");

    private static CreateOrderRequest Install(string customer, int? assigneeId = null) => new()
    {
        Kind = "Install", CustomerName = customer, ContactName = "张工", ContactPhone = "13800001234",
        Address = "苏州市工业园区星湖街 1 号", Latitude = 31.30, Longitude = 120.72,
        ProjectNo = "HT-2026-118", SatSigned = false, Devices = new() { new DeviceItem { Name = "高压反应釜", Model = "HTR-250" } },
        InstallNote = "2 台设备，客户已备好水电气", AssigneeId = assigneeId
    };

    private PunchRequest P(int orderId, PunchKind kind, string? work = null) => new()
    {
        OrderId = orderId, Kind = kind, Latitude = 31.30, Longitude = 120.72, Address = "x",
        PhotoFileId = NewFile(Wang, FileCategory.PunchPhoto), FormVersion = 2, WorkContent = work
    };

    [Fact]
    public async Task EngineerCreatesAndOwnsItNoHallNoChargeNoTransfer()
    {
        await Fails(40300, "安装调试工单由售后建单", () => Orders.Create(Install("销售不能建"), Wm));
        var d = await Orders.Create(Install("安装客户"), Wang);
        Assert.Equal((OrderKind.Install, "安装调试", OrderStatus.Accepted, ServiceType.Onsite, "王工"),
            (d.Kind, d.KindText, d.Status, d.ServiceType, d.Assignee!.Name));
        Assert.Equal("2 台设备，客户已备好水电气", d.Symptom);
        Assert.Contains(d.Logs, l => l.Text == "王工 建单（安装调试），由 本人 负责 → 待启动");
        // 不进接单大厅
        Assert.DoesNotContain((await Orders.Hall("all", Liu)).Items, i => i.Id == d.Id);
        // 不收费：价格已定为 0，不用判断收费
        var sup = await Orders.Detail(d.Id, Sup);
        Assert.Equal((0m, true), (sup.Price, sup.PriceSet));
        Assert.Equal(("None", false), (d.Charge.Status, d.Charge.CanDecide));
        await Fails(40900, "安装调试工单不收费，不用判断", () => Orders.DecideCharge(d.Id, new ChargeDecideRequest { Charge = true, Reason = "x" }, Wang));
        // 工程师不能转交、不能升级；主管可以改派
        Assert.DoesNotContain("transfer", d.Actions);
        Assert.DoesNotContain("requestEscalate", d.Actions);
        Assert.Contains("checkin", d.Actions);
        await Fails(40900, "安装调试工单不能转交，需要换人请联系主管改派",
            () => Orders.Transfer(d.Id, new TransferRequest { EngineerId = Liu.Id, Reason = "有事" }, Wang));
        await Fails(40900, "安装调试工单没有升级服务方式",
            () => Orders.RequestEscalate(d.Id, new EscalateRequest { TargetServiceType = "Factory", Reason = "a", Findings = "b" }, Wang));
        Assert.Contains("reassign", sup.Actions);
        var re = await Orders.Assign(d.Id, new AssignRequest { EngineerId = Liu.Id }, Sup);
        Assert.Equal("刘工", re.Assignee!.Name);
    }

    [Fact]
    public async Task SupervisorMustNameTheEngineer()
    {
        await Fails(40000, "请指定负责工程师", () => Orders.Create(Install("主管建单"), Sup));
        await Fails(40000, "所选工程师不存在或已停用", () => Orders.Create(Install("主管建单", Wm.Id), Sup));
        var d = await Orders.Create(Install("主管建单", Liu.Id), Sup);
        Assert.Equal(("刘工", OrderStatus.Accepted), (d.Assignee!.Name, d.Status));
        Assert.Contains(d.Logs, l => l.Text == "赵主管 建单（安装调试），由 刘工 负责 → 待启动");
        Assert.Contains(Notifier.Sent, n => n.Title == "工单指派提醒" && n.Mobiles.Count == 1);
    }

    [Fact]
    public async Task AttachmentTypesFollowOrderKind()
    {
        var install = (await Orders.Create(Install("附件客户"), Wang)).Id;
        var repair = await NewAccepted("维修附件客户");
        AttachmentsRequest Att(string type) => new()
        {
            Attachments = new() { new AttachmentItem { FileId = NewFile(Wang, FileCategory.Attachment), Type = type } }
        };
        var d = await Orders.AddAttachments(install, Att("DeliveryNote"), Wang);
        Assert.Equal(("送货单", AttachmentType.DeliveryNote), (d.Attachments.Single().AttachmentTypeShort, d.Attachments.Single().AttachmentType));
        await Orders.AddAttachments(install, Att("AcceptanceReport"), Wang);
        await Fails(40000, "安装调试工单不需要维修登记表", () => Orders.AddAttachments(install, Att("OnsiteRepairForm"), Wang));
        await Fails(40000, "送货单只能在安装调试工单里上传", () => Orders.AddAttachments(repair, Att("DeliveryNote"), Wang));
        await Fails(40000, "验收报告只能在安装调试工单里上传", () => Orders.AddAttachments(repair, Att("AcceptanceReport"), Wang));
    }

    [Fact]
    public async Task CompleteNeedsSummaryPhotoAndAcceptanceReport()
    {
        var id = (await Orders.Create(Install("完工客户"), Wang)).Id;
        await Punches.Create(Wang, P(id, PunchKind.Arrive));
        Tick(60);
        var req = new CompleteRequest { FormVersion = 3, RunStatus = RunStatus.Normal };
        await Fails(40900, "请先离场打卡", () => Orders.Complete(id, req, Wang));
        await Punches.Create(Wang, P(id, PunchKind.Leave, "就位接线、调试"));
        Tick(1);
        await Fails(40000, "请填写安装调试情况", () => Orders.Complete(id, req, Wang));
        req.InstallSummary = "2 台反应釜就位、接线，升温测试通过";
        await Fails(40000, "请至少上传 1 张现场照片", () => Orders.Complete(id, req, Wang));
        req.ProcessPhotoIds = new() { NewFile(Wang, FileCategory.ProcessPhoto) };
        await Fails(40000, "请上传验收报告（客户签字版）", () => Orders.Complete(id, req, Wang));
        req.AcceptanceIds = new() { NewFile(Wang, FileCategory.Attachment) };
        req.OpenIssues = "冷却水压力偏低";
        var d = await Orders.Complete(id, req, Wang); // 不用先判断收费
        Assert.Equal(OrderStatus.Submitted, d.Status);
        Assert.Equal(("", "2 台反应釜就位、接线，升温测试通过", "冷却水压力偏低"),
            (d.Completion!.FaultCause, d.Completion.Solution, d.Completion.OpenIssues));
        Assert.Contains(d.Attachments, a => a.AttachmentType == AttachmentType.AcceptanceReport);
        // 主管照常验收闭环（价格已是 0）
        Assert.Equal(OrderStatus.Closed, (await Orders.Close(id, Sup)).Status);
    }

    [Fact]
    public async Task ListsSplitByKind()
    {
        var install = (await Orders.Create(Install("列表安装客户"), Wang)).Id;
        var repair = await NewAccepted("列表维修客户");
        var mine = await Orders.Mine(Wang, "all", "Install");
        Assert.Contains(mine.Items, i => i.Id == install);
        Assert.DoesNotContain(mine.Items, i => i.Kind == OrderKind.Repair);
        Assert.True(mine.Counts["install"] >= 1 && mine.Counts["repair"] >= 1);
        var all = await Orders.Mine(Wang, "all");
        Assert.Contains(all.Items, i => i.Id == install);
        Assert.Contains(all.Items, i => i.Id == repair);

        var q = await Orders.Query(new OrderQuery { Kind = "Install", PageSize = 200 }, Sup);
        Assert.All(q.Items, i => Assert.Equal(OrderKind.Install, i.Kind));
        Assert.Equal(q.Total, q.Counts["install"]);
        Assert.True(q.Counts["repair"] >= 1);
        await Fails(40000, "工单种类不正确", () => Orders.Query(new OrderQuery { Kind = "x" }, Sup));
        // 旧客户端不传种类 = 维修工单
        Assert.Equal(OrderKind.Repair, (await Orders.Detail(repair, Sup)).Kind);
    }
}
