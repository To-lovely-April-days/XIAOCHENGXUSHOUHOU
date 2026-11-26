using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.9 维修登记表拆成现场 / 返厂；文件中心按工单归档</summary>
public sealed class V19FolderTests : V12TestBase
{
    private readonly FileCenterService _center;

    public V19FolderTests() => _center = new FileCenterService(Db, View);

    private static AttachmentsRequest AttT(string type, params int[] ids) =>
        new() { Attachments = ids.Select(i => new AttachmentItem { FileId = i, Type = type }).ToList() };

    private CurrentUser Wm => U("王敏");

    [Fact]
    public async Task NewTypesAreStoredAndLabelled()
    {
        // v2.1：登记表跟着服务方式走，现场的传到上门单，返厂的传到返厂单
        var id = await NewPending("类别测试");
        var factory = await NewPending("类别测试返厂", ServiceType.Factory);
        var a = await UpPdf(Wm, "现场登记表.pdf");
        var b = await UpPdf(Wm, "返厂登记表.pdf");
        var d1 = await Orders.AddAttachments(id, AttT("OnsiteRepairForm", a), Wm);
        var d2 = await Orders.AddAttachments(factory, AttT("FactoryRepairForm", b), Wm);
        var byId = d1.Attachments.Concat(d2.Attachments).ToDictionary(x => x.Id);
        Assert.Equal((AttachmentType.OnsiteRepairForm, "现场维修登记表（签字版）", "现场维修登记表"),
            (byId[a].AttachmentType, byId[a].AttachmentTypeLabel, byId[a].AttachmentTypeShort));
        Assert.Equal((AttachmentType.FactoryRepairForm, "返厂维修登记表（签字版）", "返厂维修登记表"),
            (byId[b].AttachmentType, byId[b].AttachmentTypeLabel, byId[b].AttachmentTypeShort));
    }

    [Fact]
    public async Task LegacyRepairFormFollowsTheOrdersServiceType()
    {
        var onsite = await NewPending("现场单", ServiceType.Onsite);
        var factory = await NewPending("返厂单", ServiceType.Factory);
        var f1 = await UpPdf(Wm, "表1.pdf");
        var f2 = await UpPdf(Wm, "表2.pdf");
        var d1 = await Orders.AddAttachments(onsite, AttT("RepairForm", f1), Wm);
        var d2 = await Orders.AddAttachments(factory, AttT("RepairForm", f2), Wm);
        Assert.Equal(AttachmentType.OnsiteRepairForm, d1.Attachments.Single(x => x.Id == f1).AttachmentType);
        Assert.Equal(AttachmentType.FactoryRepairForm, d2.Attachments.Single(x => x.Id == f2).AttachmentType);

        // 文件中心里旧的 RepairForm 筛选 = 两类维修登记表
        var both = await _center.Query(new FileCenterQuery { AttachmentType = "RepairForm", PageSize = 200 }, Sup);
        Assert.Equal(new[] { f1, f2 }.OrderBy(x => x), both.Items.Select(x => x.Id).OrderBy(x => x));
        var onlyFactory = await _center.Query(new FileCenterQuery { AttachmentType = "FactoryRepairForm", PageSize = 200 }, Sup);
        Assert.Equal(new[] { f2 }, onlyFactory.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task FoldersGroupFilesByOrderNewestFirst()
    {
        var before = await _center.Folders(null, 1, 200, Sup);
        var a = await NewPending("文件夹客户甲");
        var b = await NewPending("文件夹客户乙", ServiceType.Factory);
        await Orders.AddAttachments(a, AttT("Sat", await UpPdf(Wm, "验收单.pdf")), Wm);
        await Orders.AddAttachments(a, AttT("OnsiteRepairForm", await UpPdf(Wm, "现场表.pdf")), Wm);
        Tick();
        await Orders.AddAttachments(b, AttT("FactoryRepairForm", await UpPdf(Wm, "返厂表.pdf")), Wm);

        var page = await _center.Folders(null, 1, 200, Sup);
        Assert.Equal(before.Total + 2, page.Total);
        Assert.Equal(before.TotalFiles + 3, page.TotalFiles);
        Assert.Equal(new[] { b, a }, page.Items.Take(2).Select(x => x.OrderId)); // 最近上传的在前

        var fa = page.Items.Single(x => x.OrderId == a);
        Assert.Equal((2, 1, 1, 0, "文件夹客户甲"), (fa.FileCount, fa.SatCount, fa.OnsiteRepairCount, fa.FactoryRepairCount, fa.CustomerName));
        var fb = page.Items.Single(x => x.OrderId == b);
        Assert.Equal((1, 0, 0, 1, ServiceType.Factory), (fb.FileCount, fb.SatCount, fb.OnsiteRepairCount, fb.FactoryRepairCount, fb.ServiceType));
        Assert.Equal(page.Items.Sum(x => x.FileCount), page.TotalFiles);

        // 搜索：客户名、文件名都能找到文件夹；文件夹里只算匹配到的文件
        Assert.Equal(new[] { a }, (await _center.Folders("文件夹客户甲", 1, 200, Sup)).Items.Select(x => x.OrderId));
        var byFile = await _center.Folders("返厂表", 1, 200, Sup);
        Assert.Equal(new[] { b }, byFile.Items.Select(x => x.OrderId));

        // 分页
        var p1 = await _center.Folders(null, 1, 1, Sup);
        Assert.Equal((1, page.Total, b), (p1.Items.Count, p1.Total, p1.Items[0].OrderId));
    }

    [Fact]
    public async Task OrdersWithoutFilesHaveNoFolder()
    {
        var empty = await NewPending("没有文件的工单");
        Assert.DoesNotContain(empty, (await _center.Folders(null, 1, 200, Sup)).Items.Select(x => x.OrderId));
    }
}
