using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v2.0 完工资料按服务方式区分；升级服务方式（需主管同意）</summary>
public sealed class V20ServiceFormTests : V12TestBase
{
    private CurrentUser Wang => U("王工");

    private CompleteRequest V2() => new()
    {
        FormVersion = 2, FaultCause = "加热管老化", Solution = "更换加热管", RunStatus = RunStatus.Normal, WorkContent = "更换加热管并测试"
    };
    private int Pic() => NewFile(Wang, FileCategory.ProcessPhoto);

    private async Task<int> OnsiteInProgress(string customer)
    {
        var id = await NewAccepted(customer);
        await Arrive(Wang, id);
        Tick(30);
        await Leave(Wang, id);
        return id;
    }

    // ---------- 上门 ----------

    [Fact]
    public async Task OnsiteNeedsBeforeAfterPhotosAndSignedRepairForm()
    {
        var id = await OnsiteInProgress("上门完工客户");
        var r = V2();
        await Fails(40000, "请至少上传 1 张维修前照片（设备原始状态）", () => Orders.Complete(id, r, Wang));
        var before = Pic();
        r.BeforePhotoIds = new() { before };
        await Fails(40000, "请至少上传 1 张维修后照片", () => Orders.Complete(id, r, Wang));
        var after = Pic();
        r.AfterPhotoIds = new() { after };
        await Fails(40000, "请上传现场维修登记表（客户签字版）", () => Orders.Complete(id, r, Wang));
        var form = await UpPdf(Wang, "现场维修登记表.pdf");
        r.RepairFormIds = new() { form };

        var d = await Orders.Complete(id, r, Wang);
        Assert.Equal(OrderStatus.Submitted, d.Status);
        var photos = d.Files.ProcessPhotos.ToDictionary(x => x.Id, x => x.Stage);
        Assert.Equal(("Before", "After"), (photos[before], photos[after]));
        var att = d.Attachments.Single(a => a.Id == form);
        Assert.Equal(AttachmentType.OnsiteRepairForm, att.AttachmentType);
    }

    [Fact]
    public async Task OnsiteRepairFormUploadedEarlierCounts()
    {
        var id = await OnsiteInProgress("先传登记表客户");
        var form = await UpPdf(Wang, "登记表.pdf");
        await Orders.AddAttachments(id, new AttachmentsRequest
            { Attachments = new() { new AttachmentItem { FileId = form, Type = "OnsiteRepairForm" } } }, Wang);
        var r = V2();
        r.BeforePhotoIds = new() { Pic() };
        r.AfterPhotoIds = new() { Pic() };
        Assert.Equal(OrderStatus.Submitted, (await Orders.Complete(id, r, Wang)).Status);
    }

    [Fact]
    public async Task OldClientsKeepTheOldRules()
    {
        var id = await OnsiteInProgress("旧版客户端客户");
        Assert.Equal(OrderStatus.Submitted, (await Orders.Complete(id, Done(Wang), Wang)).Status);
    }

    // ---------- 远程 ----------

    [Fact]
    public async Task RemoteNeedsConfirmMethodButNoPhotos()
    {
        var id = await NewAccepted("远程完工客户", service: ServiceType.Remote);
        await Orders.Start(id, Wang);
        var r = V2();
        await Fails(40000, "请选择客户确认方式", () => Orders.Complete(id, r, Wang));
        r.CustomerConfirm = "随便写的";
        await Fails(40000, "请选择客户确认方式", () => Orders.Complete(id, r, Wang));
        r.CustomerConfirm = "微信确认";
        var d = await Orders.Complete(id, r, Wang);
        Assert.Equal((OrderStatus.Submitted, "微信确认"), (d.Status, d.Completion!.CustomerConfirm));
        Assert.Empty(d.Files.ProcessPhotos);
    }

    // ---------- 返厂 ----------

    [Fact]
    public async Task FactoryNeedsPhotoTestResultAndFactoryRepairForm()
    {
        var id = await NewAccepted("返厂完工客户", service: ServiceType.Factory);
        await Orders.RecordFactoryNode(id, Node(FactoryNode.CustomerShipped, "顺丰", "SF1"), U("王敏"));
        await Orders.RecordFactoryNode(id, Node(FactoryNode.FactoryReceived, null, null, NewFile(Wang, FileCategory.FactoryReceivePhoto)), Wang);
        await Orders.RecordFactoryNode(id, Node(FactoryNode.ShippedBack, null, null, NewFile(Wang, FileCategory.FactoryShipPhoto)), Wang);
        var r = V2();
        await Fails(40000, "请至少上传 1 张维修照片", () => Orders.Complete(id, r, Wang));
        r.ProcessPhotoIds = new() { Pic() };
        await Fails(40000, "请填写寄回前的测试结果", () => Orders.Complete(id, r, Wang));
        r.TestResult = "整机功能测试通过";
        await Fails(40000, "请上传返厂维修登记表", () => Orders.Complete(id, r, Wang));
        var form = await UpPdf(Wang, "返厂维修登记表.pdf");
        r.RepairFormIds = new() { form };
        var d = await Orders.Complete(id, r, Wang);
        Assert.Equal((OrderStatus.Submitted, "整机功能测试通过"), (d.Status, d.Completion!.TestResult));
        Assert.Equal(AttachmentType.FactoryRepairForm, d.Attachments.Single(a => a.Id == form).AttachmentType);
    }

    // ---------- 升级服务方式 ----------

    private static EscalateRequest Esc(string target, string? reason = "远程无法定位", string? findings = "已视频排查参数和接线") =>
        new() { TargetServiceType = target, Reason = reason, Findings = findings };

    [Fact]
    public async Task RemoteEscalatesToOnsiteAfterSupervisorApproves()
    {
        var id = await NewAccepted("远程转上门客户", service: ServiceType.Remote);
        await Orders.Start(id, Wang);
        Assert.Contains("requestEscalate", (await Orders.Detail(id, Wang)).Actions);

        await Fails(40000, "请选择要升级成哪种服务方式", () => Orders.RequestEscalate(id, Esc(""), Wang));
        await Fails(40000, "请填写升级原因", () => Orders.RequestEscalate(id, Esc("Onsite", reason: " "), Wang));
        await Fails(40000, "请填写已排查情况", () => Orders.RequestEscalate(id, Esc("Onsite", findings: null), Wang));
        await Fails(40300, "只有负责工程师可以申请升级服务方式", () => Orders.RequestEscalate(id, Esc("Onsite"), U("李工")));

        var d = await Orders.RequestEscalate(id, Esc("Onsite"), Wang);
        Assert.Equal((ServiceType.Remote, OrderStatus.InProgress), (d.ServiceType, d.Status)); // 主管同意前什么都不变
        Assert.DoesNotContain("requestEscalate", d.Actions);
        var req = d.Requests.Single(x => x.Type == OrderRequestType.Escalate);
        Assert.Equal((RequestStatus.Pending, ServiceType.Onsite, "升级为上门服务：远程无法定位；已排查：已视频排查参数和接线"),
            (req.Status, req.TargetServiceType, req.Text));
        await Fails(40900, "已有待审批的升级申请", () => Orders.RequestEscalate(id, Esc("Factory"), Wang));

        d = await Orders.ApproveRequest(id, req.Id, Sup);
        Assert.Equal((ServiceType.Onsite, OrderStatus.Accepted), (d.ServiceType, d.Status));
        Assert.Equal("赵主管 同意升级：远程 → 上门服务，工单回到待启动", d.Logs[^1].Text);
        // 之后按上门流程走：到场打卡进入处理中，完工按上门的要求
        await Arrive(Wang, id);
        Assert.Equal(OrderStatus.InProgress, (await Orders.Detail(id, Wang)).Status);
        Assert.Contains("checkin", (await Orders.Detail(id, Wang)).Actions);
    }

    [Fact]
    public async Task OnsiteEscalatesToFactoryButNotWhileSomeoneIsOnSite()
    {
        var id = await NewAccepted("上门转返厂客户");
        await Arrive(Wang, id);
        var d = await Orders.RequestEscalate(id, Esc("Factory", "需拆回厂里维修", "现场更换温控模块无效"), Wang);
        var reqId = d.Requests.Single(x => x.Type == OrderRequestType.Escalate).Id;
        await Fails(40900, "王工还在现场计时，请其先离场打卡", () => Orders.ApproveRequest(id, reqId, Sup));
        Tick(20);
        await Leave(Wang, id);
        d = await Orders.ApproveRequest(id, reqId, Sup);
        Assert.Equal((ServiceType.Factory, OrderStatus.Accepted), (d.ServiceType, d.Status));
        Assert.True(d.TotalMinutes >= 20); // 已打的卡和工时保留
        Assert.Contains("factory", (await Orders.Detail(id, Wang)).Actions);
        Assert.DoesNotContain("requestEscalate", (await Orders.Detail(id, Wang)).Actions); // 返厂已是最重的方式
    }

    [Fact]
    public async Task EscalationCannotGoBackwardsAndCanBeRejectedOrCancelled()
    {
        var onsite = await NewAccepted("不能往回改客户");
        await Fails(40000, "现场服务不能改为远程服务", () => Orders.RequestEscalate(onsite, Esc("Remote"), Wang));

        var id = await NewAccepted("驳回与撤回客户", service: ServiceType.Remote);
        var d = await Orders.RequestEscalate(id, Esc("Factory"), Wang);
        var first = d.Requests.Single(x => x.Type == OrderRequestType.Escalate).Id;
        d = await Orders.RejectRequest(id, first, new ReplyRequest { Reply = "先再远程试一次" }, Sup);
        Assert.Equal(ServiceType.Remote, d.ServiceType);
        Assert.Equal("赵主管 驳回升级为返厂维修的申请（先再远程试一次）", d.Logs[^1].Text);

        d = await Orders.RequestEscalate(id, Esc("Onsite"), Wang);
        var second = d.Requests.Where(x => x.Type == OrderRequestType.Escalate).Single(x => x.Status == RequestStatus.Pending).Id;
        d = await Orders.CancelRequest(id, second, Wang);
        Assert.Equal("王工 撤回升级服务方式的申请", d.Logs[^1].Text);
        Assert.Contains("requestEscalate", d.Actions);
    }

    // ---------- 地址：只有上门必填 ----------

    [Fact]
    public async Task AddressIsOnlyRequiredForOnsite()
    {
        var sales = U("王敏");
        var onsite = NewOrder("上门没地址");
        onsite.Address = " ";
        await Fails(40000, "请填写客户地址", () => Orders.Create(onsite, sales));

        foreach (var st in new[] { ServiceType.Factory, ServiceType.Remote })
        {
            var r = NewOrder("不用地址" + st, st);
            r.Address = null;
            r.Latitude = null;
            r.Longitude = null;
            var d = await Orders.Create(r, sales);
            Assert.Equal(("", st), (d.Address, d.ServiceType));
        }
    }

    [Fact]
    public async Task EscalatingToOnsiteAsksForAddressWhenOrderHasNone()
    {
        var r = NewOrder("远程没地址转上门", ServiceType.Remote);
        r.Address = null;
        r.Latitude = null;
        r.Longitude = null;
        var id = (await Orders.Create(r, U("王敏"))).Id;
        await Orders.Claim(id, Wang);

        await Fails(40000, "转为上门服务请在地图上选择客户地址", () => Orders.RequestEscalate(id, Esc("Onsite"), Wang));
        // 转返厂不需要地址
        var toFactory = await Orders.RequestEscalate(id, Esc("Factory"), Wang);
        await Orders.CancelRequest(id, toFactory.Requests.Single(x => x.Type == OrderRequestType.Escalate).Id, Wang);

        var body = Esc("Onsite");
        body.Address = "苏州市工业园区星湖街 1 号";
        body.Latitude = 31.30;
        body.Longitude = 120.72;
        var d = await Orders.RequestEscalate(id, body, Wang);
        Assert.Equal(("苏州市工业园区星湖街 1 号", 31.30), (d.Address, d.Latitude));
        Assert.Contains(d.Logs, l => l.Text == "王工 补充客户地址：苏州市工业园区星湖街 1 号");
    }
}
