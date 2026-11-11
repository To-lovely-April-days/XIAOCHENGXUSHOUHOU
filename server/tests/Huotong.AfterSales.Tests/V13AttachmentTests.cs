using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.3 附件分类 + 随时可传：三类附件、闭环后继续上传、上限 50、改类别、文件中心筛选（CHANGES-v1.3 §4、§11.2 第 11–15、35 项）</summary>
public sealed class V13AttachmentTests : V13TestBase
{
    private readonly FileCenterService _center;

    public V13AttachmentTests() => _center = new FileCenterService(Db, View);

    private CurrentUser Wm => U("王敏");
    private CurrentUser Wg => U("王工");

    private static AttachmentsRequest AttT(string? type, params int[] ids) =>
        new() { Attachments = ids.Select(i => new AttachmentItem { FileId = i, Type = type }).ToList() };

    private static AttachmentTypeRequest T(string? type) => new() { Type = type };

    [Fact]
    public async Task TypeIsStoredOnAddAndCreate()
    {
        var id = await NewAccepted("附件分类客户", "王工");
        var sat = await UpPdf(Wm, "验收单.pdf");
        var form = await UpPdf(Wm, "维修登记表.pdf");
        var other = await UpPdf(Wm, "沟通记录.pdf");
        var plain = await UpPdf(Wm, "没传类别.pdf");
        await Orders.AddAttachments(id, AttT("Sat", sat), Wm);
        await Orders.AddAttachments(id, AttT("RepairForm", form), Wm);
        await Orders.AddAttachments(id, AttT("Other", other), Wm);
        var d = await Orders.AddAttachments(id, Att(plain), Wm); // v1.2 客户端不传 type

        Assert.Equal(new AttachmentType?[] { AttachmentType.Sat, AttachmentType.RepairForm, AttachmentType.Other, AttachmentType.Other },
            d.Attachments.Select(a => a.AttachmentType));
        Assert.Equal(new[] { "SAT 验收单（签字版）", "现场维修登记表/返厂维修登记表（签字版）", "设备照片、沟通记录及其他附件", "设备照片、沟通记录及其他附件" },
            d.Attachments.Select(a => a.AttachmentTypeLabel));
        Assert.Equal(new[] { "SAT 验收单", "维修登记表", "其他附件", "其他附件" }, d.Attachments.Select(a => a.AttachmentTypeShort));
        Assert.Equal(AttachmentType.Sat, FileRow(sat).AttachmentType);
        Assert.Equal(AttachmentType.RepairForm, FileRow(form).AttachmentType);
        Assert.Equal(AttachmentType.Other, FileRow(plain).AttachmentType);
        // 上传记录不带类别（保持 v1.2 文案）；不发通知
        Assert.Equal("王敏 上传附件 验收单.pdf", d.Logs.First(l => l.Kind == LogKind.Attachment).Text);
        Assert.DoesNotContain(d.Logs, l => l.Text.Contains("归为"));
        // 展示顺序固定 Sat → RepairForm → Other；枚举声明顺序 Other 在最前（CLR 默认值）
        Assert.Equal(new[] { AttachmentType.Sat, AttachmentType.RepairForm, AttachmentType.Other }, AttachmentRules.TypeOrder);
        Assert.Equal(AttachmentType.Other, default(AttachmentType));
        Assert.Equal(AttachmentType.Other, new StoredFile().AttachmentType);

        // 非法类别：一批里有一个非法，全部失败；在「请选择要添加的附件」之后
        var bad = await UpPdf(Wm, "非法类别.pdf");
        await Fails(40000, "附件类别不正确", () => Orders.AddAttachments(id, AttT("Nope", bad), Wm));
        await Fails(40000, "附件类别不正确", () => Orders.AddAttachments(id, AttT("1", bad), Wm));
        await Fails(40000, "附件类别不正确", () => Orders.AddAttachments(999999, AttT("-1", bad), Wm)); // 请求体校验在工单是否存在之前
        await Fails(40000, "请选择要添加的附件", () => Orders.AddAttachments(id, new AttachmentsRequest { Attachments = new() }, Wm));
        // 同一项既缺 fileId 又类别非法：先报缺 fileId
        await Fails(40000, "请选择要添加的附件",
            () => Orders.AddAttachments(id, new AttachmentsRequest { Attachments = new() { new AttachmentItem { Type = "Nope" } } }, Wm));
        Assert.Null(FileRow(bad).OrderId);

        // 建单时的 attachments[].type 同样生效；重复 fileId 取第一次；photoFileIds 一律 Other
        var c1 = await UpPdf(Wm, "建单验收单.pdf");
        var c2 = await UpPdf(Wm, "建单登记表.pdf");
        var photo = (await Up(Wm, Png, "p.png", "OrderPhoto")).Id;
        var req = NewOrder13("建单带分类附件", ServiceType.Remote, Dev("釜", "", ""));
        req.PhotoFileIds = new() { photo };
        req.Attachments = new()
        {
            new AttachmentItem { FileId = c1, Type = " sat " }, new AttachmentItem { FileId = c2, Type = "REPAIRFORM", Restricted = true },
            new AttachmentItem { FileId = c1, Type = "Other" }
        };
        var created = await Orders.Create(req, Wm);
        Assert.Equal(AttachmentType.Sat, created.Attachments.Single(a => a.Id == c1).AttachmentType);
        Assert.Equal(AttachmentType.RepairForm, created.Attachments.Single(a => a.Id == c2).AttachmentType);
        Assert.Equal(AttachmentType.Other, created.Attachments.Single(a => a.Id == photo).AttachmentType);
        Assert.Equal("OrderPhoto", created.Attachments.Single(a => a.Id == photo).Source);
        var badCreate = NewOrder13("建单非法类别");
        badCreate.Attachments = new() { new AttachmentItem { FileId = await UpPdf(Wm, "x.pdf"), Type = "2" } };
        await Fails(40000, "附件类别不正确", () => Orders.Create(badCreate, Wm));
        badCreate.Attachments = new() { new AttachmentItem { Type = "2" } };
        await Fails(40000, "附件不存在，请重新上传", () => Orders.Create(badCreate, Wm));
    }

    [Fact]
    public void TypeParsingHasSingleEntry()
    {
        static (bool Ok, AttachmentType? Type) P(string? s) => (AttachmentRules.TryParseType(s, out var t), t);
        Assert.Equal((true, (AttachmentType?)null), P(null));
        Assert.Equal((true, (AttachmentType?)null), P(""));
        Assert.Equal((true, (AttachmentType?)null), P("   "));
        Assert.Equal((true, AttachmentType.Sat), P("Sat"));
        Assert.Equal((true, AttachmentType.Sat), P("sat "));
        Assert.Equal((true, AttachmentType.Sat), P("SAT"));
        Assert.Equal((true, AttachmentType.RepairForm), P(" repairform"));
        Assert.Equal((true, AttachmentType.Other), P("Other"));
        foreach (var bad in new[] { "1", "0", "-1", "Nope", "Sat,Other", "Sat1", "SAT 验收单" })
            Assert.Equal((false, (AttachmentType?)null), P(bad));
    }

    [Fact]
    public async Task ClosedOrderStillAcceptsAttachments()
    {
        var chen = U("陈工");
        var id = await NewAccepted("闭环后上传客户", "王工", ServiceType.Remote);
        await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { chen.Id } }, Sup);
        var before = (await Orders.AddAttachments(id, Att(await UpPdf(Wg, "闭环前.pdf")), Wg)).Attachments.Single().Id;
        await Orders.Start(id, Wg);
        await Orders.Complete(id, Done(Wg), Wg);
        // 缺哪一类附件都不拦提交完工、不拦闭环
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        Assert.Equal(OrderStatus.Closed, (await Orders.Close(id, Sup)).Status);

        var n = 1;
        foreach (var who in new[] { "王敏", "王工", "陈工", "赵主管" }) // 建单人、负责人、协助人、主管
        {
            var u = U(who);
            var d = await Orders.AddAttachments(id, AttT("Sat", await UpPdf(u, $"{who}-闭环后.pdf")), u);
            Assert.Equal(++n, d.Attachments.Count);
            Assert.True(d.CanAddAttachment);
            Assert.Equal(OrderStatus.Closed, d.Status);
            Assert.Equal(OrderStatus.Closed, d.Logs[^1].ToStatus);
        }
        // 无关工程师 / 非建单销售
        var liu = U("刘工");
        await Fails(40300, "无权查看该工单", async () => await Orders.AddAttachments(id, Att(await UpPdf(liu, "x.pdf")), liu));
        var sl = U("孙磊");
        await Fails(40300, "只有建单人、负责工程师、协助人或主管可以添加附件", async () => await Orders.AddAttachments(id, Att(await UpPdf(sl, "x.pdf")), sl));

        // 闭环后只能新增不能删除（主管除外）；类别仍可改
        var mine = (await Orders.Detail(id, Wg)).Attachments.Single(a => a.Name == "王工-闭环后.pdf");
        Assert.False(mine.CanDelete);
        Assert.True(mine.CanChangeType);
        await Fails(40900, "工单已闭环，只有主管可以删除附件", () => Orders.DeleteAttachment(id, mine.Id, Wg));
        await Fails(40900, "工单已闭环，只有主管可以删除附件", () => Orders.DeleteAttachment(id, before, Wg));
        Assert.Equal(AttachmentType.RepairForm, (await Orders.SetAttachmentType(id, mine.Id, T("RepairForm"), Wg)).Attachments.Single(a => a.Id == mine.Id).AttachmentType);
        var sup = await Orders.DeleteAttachment(id, mine.Id, Sup);
        Assert.DoesNotContain(sup.Attachments, a => a.Id == mine.Id);

        // 作废后任何人都不能添加
        var voided = await NewAccepted("作废后上传客户", "王工");
        await Orders.Void(voided, new ReasonRequest(), Sup);
        foreach (var u in new[] { Wm, Wg, Sup })
        {
            await Fails(40900, "工单已作废，不能添加附件", async () => await Orders.AddAttachments(voided, Att(await UpPdf(u, "v.pdf")), u));
            Assert.False((await Orders.Detail(voided, u)).CanAddAttachment);
        }
    }

    [Fact]
    public async Task LimitIsFifty()
    {
        var id = await NewAccepted("上限五十", "王工");
        int[] Fresh(int n) => Enumerable.Range(0, n).Select(_ => NewFile(Wm, FileCategory.Attachment)).ToArray();
        var d = await Orders.AddAttachments(id, AttT("Sat", Fresh(20)), Wm);
        Assert.Equal(50, d.AttachmentLimit);
        Assert.Equal(30, d.AttachmentRemaining);
        d = await Orders.AddAttachments(id, AttT("RepairForm", Fresh(27)), Wm);
        Assert.Equal(3, d.AttachmentRemaining);
        await Fails(40000, "附件最多 50 个，本单还可添加 3 个", () => Orders.AddAttachments(id, Att(Fresh(4)), Wm));
        d = await Orders.AddAttachments(id, Att(Fresh(3)), Wm);
        Assert.Equal(50, d.Attachments.Count);
        Assert.Equal(0, d.AttachmentRemaining);
        // 第 51 个
        await Fails(40000, "附件最多 50 个，本单已达上限", () => Orders.AddAttachments(id, Att(Fresh(1)), Wm));
        await Fails(40000, "附件最多 50 个，本单已达上限", () => Orders.AddAttachments(id, Att(NewFile(Sup, FileCategory.Attachment)), Sup));
        Assert.Equal(new[] { 20, 27, 3 }, AttachmentRules.TypeOrder.Select(t => d.Attachments.Count(a => a.AttachmentType == t)));
        Assert.Equal(50, AttachmentRules.Limit);
        Assert.Equal(50, new OrderDetail().AttachmentLimit);
    }

    [Fact]
    public async Task ChangeTypeRules()
    {
        var chen = U("陈工");
        var id = await NewAccepted("改类别客户", "王工");
        await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { chen.Id } }, Sup);
        var fEng = await UpPdf(Wg, "验收单.pdf");
        var fSales = await UpPdf(Wm, "销售的.pdf");
        var fSecret = await UpPdf(Wm, "底价.pdf");
        await Orders.AddAttachments(id, Att(fEng), Wg);
        await Orders.AddAttachments(id, Att(fSales), Wm);
        await Orders.AddAttachments(id, new AttachmentsRequest { Attachments = new() { new AttachmentItem { FileId = fSecret, Restricted = true } } }, Wm);

        // 上传人本人
        Tick();
        var d = await Orders.SetAttachmentType(id, fEng, T("Sat"), Wg);
        Assert.Equal(AttachmentType.Sat, d.Attachments.Single(a => a.Id == fEng).AttachmentType);
        Assert.Equal("王工 将附件 验收单.pdf 归为「SAT 验收单」", d.Logs[^1].Text);
        Assert.Equal(LogKind.Attachment, d.Logs[^1].Kind);
        Assert.Equal(AttachmentType.Sat, FileRow(fEng).AttachmentType);
        Assert.Empty(Notifier.Sent.Where(n => n.Text.Contains("归为")));
        // 值不变：不写记录
        var count = d.Logs.Count;
        Assert.Equal(count, (await Orders.SetAttachmentType(id, fEng, T(" sat "), Wg)).Logs.Count);
        // 主管改别人的
        Tick();
        d = await Orders.SetAttachmentType(id, fEng, T("RepairForm"), Sup);
        Assert.Equal("赵主管 将附件 验收单.pdf 归为「维修登记表」", d.Logs[^1].Text);
        // 受限附件：记录为 AttachmentPrivate，工程师看不到这条记录
        Tick();
        d = await Orders.SetAttachmentType(id, fSecret, T("Other"), Wm); // 值不变（默认就是 Other）
        Assert.DoesNotContain(d.Logs, l => l.Text.Contains("底价.pdf 归为"));
        d = await Orders.SetAttachmentType(id, fSecret, T("Sat"), Wm);
        Assert.Equal("王敏 将附件 底价.pdf 归为「SAT 验收单」", d.Logs[^1].Text);
        Assert.Equal(LogKind.AttachmentPrivate, d.Logs[^1].Kind);
        Assert.DoesNotContain((await Orders.Detail(id, Wg)).Logs, l => l.Text.Contains("底价"));

        // 非上传人
        await Fails(40300, "只能修改自己上传的附件", () => Orders.SetAttachmentType(id, fSales, T("Sat"), Wg));
        await Fails(40300, "只能修改自己上传的附件", () => Orders.SetAttachmentType(id, fEng, T("Sat"), chen));
        await Fails(40300, "只能修改自己上传的附件", () => Orders.SetAttachmentType(id, fEng, T("Sat"), U("孙磊")));
        // 工程师改一个对他不可见的受限附件
        await Fails(40400, "附件不存在", () => Orders.SetAttachmentType(id, fSecret, T("Other"), Wg));
        // 请求体：缺失 / 非法 → 同一文案，且在工单是否存在之前
        foreach (var bad in new[] { null, "", "  ", "Nope", "1" })
            await Fails(40000, "请选择附件类别", () => Orders.SetAttachmentType(999999, fEng, T(bad), Wg));
        await Fails(40400, "工单不存在", () => Orders.SetAttachmentType(999999, fEng, T("Sat"), Wg));
        await Fails(40300, "无权查看该工单", () => Orders.SetAttachmentType(id, fEng, T("Sat"), U("刘工")));
        await Fails(40400, "附件不存在", () => Orders.SetAttachmentType(id, 999999, T("Sat"), Sup));
        // 非附件来源的文件（打卡照片）不是附件
        await Arrive(Wg, id);
        var punchPhoto = Db.Files.Where(f => f.OrderId == id && f.Category == FileCategory.PunchPhoto).Select(f => f.Id).Single();
        await Fails(40400, "附件不存在", () => Orders.SetAttachmentType(id, punchPhoto, T("Sat"), Sup));
        Tick(10);
        await Leave(Wg, id);

        // canChangeType 与接口一致
        async Task Check(string who, bool eng, bool sales)
        {
            var x = await Orders.Detail(id, U(who));
            Assert.Equal(eng, x.Attachments.Single(a => a.Id == fEng).CanChangeType);
            Assert.Equal(sales, x.Attachments.Single(a => a.Id == fSales).CanChangeType);
        }
        await Check("王工", true, false);
        await Check("王敏", false, true);
        await Check("陈工", false, false);
        await Check("孙磊", false, false);
        await Check("赵主管", true, true);

        // 作废后：上传人不能改，主管仍可以
        var voided = await NewAccepted("改类别作废客户", "王工");
        var fv = await UpPdf(Wg, "作废单的附件.pdf");
        await Orders.AddAttachments(voided, Att(fv), Wg);
        await Orders.Void(voided, new ReasonRequest(), Sup);
        await Fails(40900, "工单已作废，不能修改附件类别", () => Orders.SetAttachmentType(voided, fv, T("Sat"), Wg));
        Assert.False((await Orders.Detail(voided, Wg)).Attachments.Single().CanChangeType);
        Assert.Equal(AttachmentType.Sat, (await Orders.SetAttachmentType(voided, fv, T("Sat"), Sup)).Attachments.Single().AttachmentType);
    }

    [Fact]
    public async Task RestrictingHidesEarlierTypeChangeLog()
    {
        // 改类别的记录带完整文件名：之后设为受限时，这条记录也要对工程师隐藏
        var id = await NewAccepted("改类别后受限", "王工");
        var f = await UpPdf(Wm, "内部成本测算表.pdf");
        await Orders.AddAttachments(id, Att(f), Wm);
        await Orders.SetAttachmentType(id, f, T("RepairForm"), Wm);
        Assert.Contains((await Orders.Detail(id, Wg)).Logs, l => l.Text.Contains("内部成本测算表.pdf 归为"));
        await Orders.SetAttachmentRestricted(id, f, new RestrictedRequest { Restricted = true }, Sup);
        var eng = await Orders.Detail(id, Wg);
        Assert.DoesNotContain(eng.Logs, l => l.Text.Contains("内部成本测算表"));
        Assert.Empty(eng.Attachments);
        Assert.DoesNotContain("内部成本测算表", Json(eng));
    }

    [Fact]
    public async Task FileCenterFiltersByAttachmentType()
    {
        var id = await NewAccepted("文件中心分类客户", "王工");
        var sat = await UpPdf(Wm, "中心-验收单.pdf");
        var form = await UpPdf(Wg, "中心-登记表.pdf");
        await Orders.AddAttachments(id, AttT("Sat", sat), Wm);
        await Orders.AddAttachments(id, AttT("RepairForm", form), Wg);
        await Arrive(Wg, id); // 打卡照片：非附件来源

        var all = await _center.Query(new FileCenterQuery { OrderId = id, PageSize = 200 }, Sup);
        Assert.Equal(3, all.Total);
        var detail = await Orders.Detail(id, Sup);
        foreach (var a in detail.Attachments)
        {
            var row = all.Items.Single(i => i.Id == a.Id);
            // 详情与文件中心一致
            Assert.Equal((a.AttachmentType, a.AttachmentTypeLabel, a.AttachmentTypeShort, a.CanChangeType),
                (row.AttachmentType, row.AttachmentTypeLabel, row.AttachmentTypeShort, row.CanChangeType));
        }
        var punch = all.Items.Single(i => i.Source == "PunchPhoto");
        Assert.Null(punch.AttachmentType);
        Assert.Equal("", punch.AttachmentTypeLabel);
        Assert.Equal("", punch.AttachmentTypeShort);
        Assert.False(punch.CanChangeType);

        var r = await _center.Query(new FileCenterQuery { AttachmentType = "Sat", PageSize = 200 }, Sup);
        Assert.Equal(sat, Assert.Single(r.Items).Id);
        Assert.Equal(1, r.Counts["all"]);
        Assert.Equal(1, r.Counts["Document"]);
        r = await _center.Query(new FileCenterQuery { AttachmentType = " repairform ", PageSize = 200 }, Sup);
        Assert.Equal(form, Assert.Single(r.Items).Id);
        // Other：只含附件（建单照片 + 新附件），不含打卡照片等其他来源
        r = await _center.Query(new FileCenterQuery { AttachmentType = "Other", PageSize = 200 }, Sup);
        Assert.True(r.Total > 0);
        Assert.All(r.Items, i => Assert.Contains(i.Source, new[] { "Attachment", "OrderPhoto" }));
        Assert.All(r.Items, i => Assert.Equal(AttachmentType.Other, i.AttachmentType));
        Assert.Equal(Db.Files.Count(f => f.OrderId != null && (f.Category == FileCategory.OrderPhoto || f.Category == FileCategory.Attachment)
                                         && f.AttachmentType == AttachmentType.Other), r.Total);
        // 与 source 同时给：取交集，不报错
        r = await _center.Query(new FileCenterQuery { Source = "PunchPhoto", AttachmentType = "Sat" }, Sup);
        Assert.Equal(0, r.Total);
        Assert.Empty(r.Items);
        r = await _center.Query(new FileCenterQuery { Source = "Attachment", AttachmentType = "Sat" }, Sup);
        Assert.Equal(1, r.Total);
        await Fails(40000, "附件类别不正确", () => _center.Query(new FileCenterQuery { AttachmentType = "Nope" }, Sup));
        await Fails(40000, "附件类别不正确", () => _center.Query(new FileCenterQuery { AttachmentType = "1" }, Sup));

        // 文件中心里改类别后的可见结果
        await Orders.SetAttachmentType(id, form, T("Sat"), Sup);
        Assert.Equal(2, (await _center.Query(new FileCenterQuery { AttachmentType = "Sat" }, Sup)).Total);
        Tick(10);
        await Leave(Wg, id);
    }
}
