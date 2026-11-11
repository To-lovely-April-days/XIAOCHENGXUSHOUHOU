using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.2 工单附件：建单、添加、删除、受限可见性、下载（CHANGES-v1.2 §4、§3.5、§9.2 第 8–20 项）</summary>
public sealed class V12AttachmentTests : V12TestBase
{
    private const string Jiangsu = "江苏某化工研究院"; // 种子：王敏建单、李工负责；3 张建单照片 + 现场情况说明.pdf + 报价单.pdf（受限）

    // ---------- 建单 ----------

    [Fact]
    public async Task CreateWithAttachments()
    {
        var wm = U("王敏");
        var open = await UpPdf(wm, "现场说明.pdf");
        var secret = await UpPdf(wm, "合同价.pdf");
        var img = (await Up(wm, Jpg, "tmp.jpg")).Id;
        var req = NewOrder("带附件的新单");
        req.Attachments = new()
        {
            new AttachmentItem { FileId = open }, new AttachmentItem { FileId = secret, Restricted = true },
            new AttachmentItem { FileId = img, Restricted = false }, new AttachmentItem { FileId = open, Restricted = true } // 重复：取第一次
        };
        var d = await Orders.Create(req, wm);

        Assert.Equal(3, d.Attachments.Count);
        Assert.Equal(new[] { "现场说明.pdf", "合同价.pdf" }, d.Attachments.Where(a => a.Kind == FileKind.Document).Select(a => a.Name));
        Assert.Equal(new[] { false, true, false }, d.Attachments.Select(a => a.Restricted));
        Assert.All(d.Attachments, a =>
        {
            Assert.Equal("Attachment", a.Source);
            Assert.Equal("附件", a.SourceLabel);
            Assert.Equal(FileCategory.Attachment, a.Category);
            Assert.Equal("王敏", a.Uploader!.Name);
            Assert.True(a.CanDelete);
            Assert.False(a.CanToggleRestricted);
        });
        Assert.Equal(17, d.AttachmentRemaining);
        Assert.Equal(20, d.AttachmentLimit);
        Assert.True(d.CanAddAttachment);
        Assert.True(d.CanRestrictAttachment);
        // 旧字段：只含图片 / 视频
        Assert.Equal(new[] { img }, d.Files.OrderPhotos.Select(f => f.Id));
        foreach (var id in new[] { open, secret, img })
        {
            Assert.Equal(d.Id, FileRow(id).OrderId);
            Assert.Equal(FileCategory.Attachment, FileRow(id).Category);
        }
        Assert.True(FileRow(secret).Restricted);
        Assert.False(FileRow(open).Restricted);
        // 建单时的附件不另写状态记录
        Assert.Single(d.Logs);
        Assert.Equal("王敏 建单", d.Logs[0].Text);
    }

    [Fact]
    public async Task OldCreatePayloadStillWorks()
    {
        var wm = U("王敏");
        var photo = (await Up(wm, Png, "p.png", "OrderPhoto", contentType: "image/png")).Id;
        var req = NewOrder("旧版小程序建单");
        req.PhotoFileIds = new() { photo, photo };
        var d = await Orders.Create(req, wm);

        Assert.Equal(FileCategory.OrderPhoto, FileRow(photo).Category);
        Assert.Equal(d.Id, FileRow(photo).OrderId);
        var a = Assert.Single(d.Attachments);
        Assert.Equal(photo, a.Id);
        Assert.Equal("OrderPhoto", a.Source);
        Assert.Equal("建单照片", a.SourceLabel);
        Assert.False(a.Restricted);
        Assert.StartsWith("建单照片_", a.Name);
        Assert.Equal(new[] { photo }, d.Files.OrderPhotos.Select(f => f.Id));
        Assert.Equal(FileCategory.OrderPhoto, d.Files.OrderPhotos[0].Category);
        Assert.Equal(19, d.AttachmentRemaining);

        // 旧校验不变
        var ten = NewOrder("十张照片");
        ten.PhotoFileIds = Enumerable.Range(0, 10).Select(_ => NewFile(wm, FileCategory.OrderPhoto)).ToList();
        await Fails(40000, "现场照片/视频最多 9 个", () => Orders.Create(ten, wm));

        // 同一个 fileId 同时出现在两处：按 attachments 处理
        var both = (await Up(wm, Png, "b.png", "OrderPhoto")).Id;
        var mixed = NewOrder("两处都传");
        mixed.PhotoFileIds = new() { both };
        mixed.Attachments = new() { new AttachmentItem { FileId = both, Restricted = true } };
        var m = await Orders.Create(mixed, wm);
        Assert.Equal(FileCategory.Attachment, FileRow(both).Category);
        Assert.True(Assert.Single(m.Attachments).Restricted);
    }

    [Fact]
    public async Task CreateValidation()
    {
        var wm = U("王敏");
        var li = U("李工");
        // attachments + photoFileIds 共 21 个
        var req = NewOrder("超限");
        req.PhotoFileIds = Enumerable.Range(0, 9).Select(_ => NewFile(wm, FileCategory.OrderPhoto)).ToList();
        req.Attachments = Enumerable.Range(0, 12).Select(_ => new AttachmentItem { FileId = NewFile(wm, FileCategory.Attachment) }).ToList();
        await Fails(40000, "附件最多 20 个", () => Orders.Create(req, wm));
        req.Attachments.RemoveAt(0);
        Assert.Equal(20, (await Orders.Create(req, wm)).Attachments.Count);

        var only21 = NewOrder("超限2");
        only21.Attachments = Enumerable.Range(0, 21).Select(_ => new AttachmentItem { FileId = NewFile(wm, FileCategory.Attachment) }).ToList();
        await Fails(40000, "附件最多 20 个", () => Orders.Create(only21, wm));

        // 工程师建单不能设受限
        var eng = NewOrder("工程师建单");
        eng.Attachments = new() { new AttachmentItem { FileId = NewFile(li, FileCategory.Attachment), Restricted = true } };
        await Fails(40300, RestrictDenied, () => Orders.Create(eng, li));
        eng.Attachments[0].Restricted = false;
        var ed = await Orders.Create(eng, li);
        Assert.Single(ed.Attachments);
        Assert.False(ed.CanRestrictAttachment);

        async Task Bad(string msg, params AttachmentItem[] items)
        {
            var r = NewOrder("坏附件");
            r.Attachments = items.ToList();
            var before = Db.Orders.Count();
            await Fails(40000, msg, () => Orders.Create(r, wm));
            Db.ChangeTracker.Clear();
            Assert.Equal(before, Db.Orders.Count()); // 整单回滚
        }
        await Bad("附件不存在，请重新上传", new AttachmentItem { FileId = null });
        await Bad("附件不存在，请重新上传", new AttachmentItem { FileId = 999999 });
        await Bad("附件不是本人上传，请重新上传", new AttachmentItem { FileId = NewFile(li, FileCategory.Attachment) });
        var used = Db.Files.AsNoTracking().First(f => f.OrderId == OrderId(Jiangsu) && f.UploaderId == wm.Id && f.Category == FileCategory.Attachment).Id;
        await Bad("附件已被使用，请重新上传", new AttachmentItem { FileId = used });
    }

    // ---------- 添加 ----------

    [Fact]
    public async Task AddPermissionMatrix()
    {
        var id = await NewAccepted("权限矩阵", "王工");
        await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { Emp("陈工").Id } }, Sup);
        var n = 0;
        async Task Ok(string who)
        {
            var u = U(who);
            var d = await Orders.AddAttachments(id, Att(await UpPdf(u, $"{who}.pdf")), u);
            Assert.Equal(++n, d.Attachments.Count);
            Assert.True(d.CanAddAttachment);
        }
        await Ok("王敏");   // 建单人
        await Ok("王工");   // 负责人
        await Ok("陈工");   // 协助人
        await Ok("赵主管"); // 主管

        const string notParty = "只有建单人、负责工程师、协助人或主管可以添加附件";
        var sl = U("孙磊");
        await Fails(40300, notParty, async () => await Orders.AddAttachments(id, Att(await UpPdf(sl, "x.pdf")), sl));
        Assert.False((await Orders.Detail(id, sl)).CanAddAttachment);
        var liu = U("刘工"); // 无关工程师：看不了工单
        await Fails(40300, "无权查看该工单", async () => await Orders.AddAttachments(id, Att(await UpPdf(liu, "x.pdf")), liu));
        await Fails(40400, "工单不存在", async () => await Orders.AddAttachments(999999, Att(await UpPdf(sl, "x.pdf")), sl));

        // 待认领：任何工程师都能看，但不是参与方
        var pending = await NewPending("待认领单");
        await Fails(40300, notParty, async () => await Orders.AddAttachments(pending, Att(await UpPdf(liu, "x.pdf")), liu));
        Assert.False((await Orders.Detail(pending, liu)).CanAddAttachment);

        // 工程师建的单：建单人（工程师）可以加
        var own = (await Orders.Create(NewOrder("工程师自建"), liu)).Id;
        Assert.Single((await Orders.AddAttachments(own, Att(await UpPdf(liu, "x.pdf")), liu)).Attachments);
    }

    [Fact]
    public async Task AddOnClosedAndVoided()
    {
        var closed = await NewClosed("闭环单");
        var wg = U("王工");
        var wm = U("王敏");
        await Fails(40900, "工单已闭环，只有主管可以添加附件", async () => await Orders.AddAttachments(closed, Att(await UpPdf(wg, "a.pdf")), wg));
        await Fails(40900, "工单已闭环，只有主管可以添加附件", async () => await Orders.AddAttachments(closed, Att(await UpPdf(wm, "a.pdf")), wm));
        // 闭环 + 非参与方销售：先报状态（第 5 步在第 6 步之前）
        var sl = U("孙磊");
        await Fails(40900, "工单已闭环，只有主管可以添加附件", async () => await Orders.AddAttachments(closed, Att(await UpPdf(sl, "a.pdf")), sl));
        var d = await Orders.AddAttachments(closed, Att(await UpPdf(Sup, "结算单.pdf")), Sup);
        Assert.Contains(d.Attachments, a => a.Name == "结算单.pdf");
        Assert.True(d.CanAddAttachment);
        Assert.False((await Orders.Detail(closed, wg)).CanAddAttachment);
        Assert.False((await Orders.Detail(closed, wm)).CanAddAttachment);

        var voided = await NewPending("作废单");
        await Orders.Void(voided, new ReasonRequest(), Sup);
        await Fails(40900, "工单已作废，不能添加附件", async () => await Orders.AddAttachments(voided, Att(await UpPdf(wm, "a.pdf")), wm));
        await Fails(40900, "工单已作废，不能添加附件", async () => await Orders.AddAttachments(voided, Att(await UpPdf(Sup, "a.pdf")), Sup));
        Assert.False((await Orders.Detail(voided, Sup)).CanAddAttachment);
        Assert.False((await Orders.Detail(voided, wm)).CanAddAttachment);
    }

    [Fact]
    public async Task AddValidation()
    {
        var id = await NewAccepted("添加校验", "王工");
        var wm = U("王敏");
        var wg = U("王工");
        await Fails(40000, "请选择要添加的附件", () => Orders.AddAttachments(id, new AttachmentsRequest(), wm));
        await Fails(40000, "请选择要添加的附件", () => Orders.AddAttachments(id, new AttachmentsRequest { Attachments = new() }, wm));
        await Fails(40000, "请选择要添加的附件", () => Orders.AddAttachments(id, new AttachmentsRequest { Attachments = new() { new AttachmentItem() } }, wm));
        // 请求体校验在工单是否存在之前
        await Fails(40000, "请选择要添加的附件", () => Orders.AddAttachments(999999, new AttachmentsRequest(), wm));

        await Fails(40000, "附件不存在，请重新上传", () => Orders.AddAttachments(id, Att(999999), wm));
        var others = await UpPdf(wg, "王工的.pdf");
        await Fails(40000, "附件不是本人上传，请重新上传", () => Orders.AddAttachments(id, Att(others), wm));
        var mine = await UpPdf(wm, "我的.pdf");
        var d = await Orders.AddAttachments(id, Att(mine, mine), wm); // 重复只算一次
        Assert.Single(d.Attachments);
        await Fails(40000, "附件已被使用，请重新上传", () => Orders.AddAttachments(id, Att(mine), wm)); // 已挂本单
        var otherOrder = await NewPending("另一单");
        await Fails(40000, "附件已被使用，请重新上传", () => Orders.AddAttachments(otherOrder, Att(mine), wm));
        // 已删除的文件
        await Orders.DeleteAttachment(id, mine, wm);
        await Fails(40000, "附件不存在，请重新上传", () => Orders.AddAttachments(id, Att(mine), wm));
        // 一批里有一个不合格：全部失败
        var good = await UpPdf(wm, "好的.pdf");
        await Fails(40000, "附件不是本人上传，请重新上传", () => Orders.AddAttachments(id, Att(good, others), wm));
        Db.ChangeTracker.Clear();
        Assert.Null(FileRow(good).OrderId);
        Assert.Empty((await Orders.Detail(id, wm)).Attachments);

        // 工程师不能设受限（第 7 步在文件校验之前）
        await Fails(40300, RestrictDenied, () => Orders.AddAttachments(id, AttR(999999), wg));
        await Fails(40300, RestrictDenied, () => Orders.AddAttachments(id, AttR(others), wg));
    }

    [Fact]
    public async Task AddRespectsLimitOfTwenty()
    {
        var wm = U("王敏");
        var req = NewOrder("上限");
        req.PhotoFileIds = Enumerable.Range(0, 9).Select(_ => NewFile(wm, FileCategory.OrderPhoto)).ToList();
        req.Attachments = Enumerable.Range(0, 9).Select(i => new AttachmentItem { FileId = NewFile(wm, FileCategory.Attachment), Restricted = i == 0 }).ToList();
        var d = await Orders.Create(req, wm);
        Assert.Equal(2, d.AttachmentRemaining);
        int[] Fresh(int n) => Enumerable.Range(0, n).Select(_ => NewFile(wm, FileCategory.Attachment)).ToArray();

        await Fails(40000, "附件最多 20 个，本单还可添加 2 个", () => Orders.AddAttachments(d.Id, Att(Fresh(3)), wm));
        d = await Orders.AddAttachments(d.Id, Att(Fresh(2)), wm);
        Assert.Equal(20, d.Attachments.Count);
        Assert.Equal(0, d.AttachmentRemaining);
        await Fails(40000, "附件最多 20 个，本单已达上限", () => Orders.AddAttachments(d.Id, Att(Fresh(1)), wm));
        await Fails(40000, "附件最多 20 个，本单已达上限", () => Orders.AddAttachments(d.Id, Att(NewFile(Sup, FileCategory.Attachment)), Sup));

        // 名额含工程师看不到的受限附件
        await Orders.Claim(d.Id, U("王工"));
        var eng = await Orders.Detail(d.Id, U("王工"));
        Assert.Equal(19, eng.Attachments.Count);
        Assert.Equal(0, eng.AttachmentRemaining);
        var wg = U("王工");
        await Fails(40000, "附件最多 20 个，本单已达上限", () => Orders.AddAttachments(d.Id, Att(NewFile(wg, FileCategory.Attachment)), wg));

        // 删除后名额释放
        d = await Orders.DeleteAttachment(d.Id, d.Attachments[0].Id, wm);
        Assert.Equal(1, d.AttachmentRemaining);
        d = await Orders.AddAttachments(d.Id, Att(Fresh(1)), wm);
        Assert.Equal(0, d.AttachmentRemaining);
    }

    [Fact]
    public async Task UploadLogs()
    {
        var id = await NewAccepted("状态记录", "王工");
        var wm = U("王敏");
        var d = await Orders.AddAttachments(id, Att(await UpPdf(wm, "现场铭牌.pdf"), await UpPdf(wm, "故障说明.pdf")), wm);
        var log = d.Logs[^1];
        Assert.Equal("王敏 上传附件 现场铭牌.pdf、故障说明.pdf", log.Text);
        Assert.Equal(LogKind.Attachment, log.Kind);
        Assert.Equal(OrderStatus.Accepted, log.ToStatus);
        Assert.Equal("王敏", log.Actor.Name);

        // > 3 个：前 3 个 + 等 n 个；超过 40 个字符的名字截为 39 + …
        var longName = new string('长', 50) + ".pdf";
        var count = d.Logs.Count;
        d = await Orders.AddAttachments(id, Att(await UpPdf(wm, longName), await UpPdf(wm, "b.pdf"), await UpPdf(wm, "c.pdf"), await UpPdf(wm, "d.pdf")), wm);
        Assert.Equal(count + 1, d.Logs.Count);
        Assert.Equal($"王敏 上传附件 {new string('长', 39)}…、b.pdf、c.pdf 等 4 个", d.Logs[^1].Text);
        Assert.Equal(54, d.Attachments.Single(a => a.Name.StartsWith("长")).Name.Length); // 附件本身的名字不截

        // 受限批次
        d = await Orders.AddAttachments(id, AttR(await UpPdf(wm, "报价单.pdf")), wm);
        Assert.Equal("王敏 上传附件 报价单.pdf（仅销售和主管可见）", d.Logs[^1].Text);
        Assert.Equal(LogKind.AttachmentPrivate, d.Logs[^1].Kind);

        // 混合批次：两条，先非受限、后受限
        count = d.Logs.Count;
        var req = new AttachmentsRequest
        {
            Attachments = new()
            {
                new AttachmentItem { FileId = await UpPdf(wm, "s1.pdf"), Restricted = true },
                new AttachmentItem { FileId = await UpPdf(wm, "o1.pdf") },
                new AttachmentItem { FileId = await UpPdf(wm, "s2.pdf"), Restricted = true },
                new AttachmentItem { FileId = await UpPdf(wm, "s3.pdf"), Restricted = true },
                new AttachmentItem { FileId = await UpPdf(wm, "s4.pdf"), Restricted = true },
            }
        };
        d = await Orders.AddAttachments(id, req, wm);
        Assert.Equal(count + 2, d.Logs.Count);
        Assert.Equal("王敏 上传附件 o1.pdf", d.Logs[^2].Text);
        Assert.Equal(LogKind.Attachment, d.Logs[^2].Kind);
        Assert.Equal("王敏 上传附件 s1.pdf、s2.pdf、s3.pdf 等 4 个（仅销售和主管可见）", d.Logs[^1].Text);
        Assert.Equal(LogKind.AttachmentPrivate, d.Logs[^1].Kind);

        // 没有原始文件名的图片：用生成名
        var wg = U("王工");
        d = await Orders.AddAttachments(id, Att((await Up(wg, Jpg, "tmp.jpg")).Id), wg);
        Assert.Matches(@"^王工 上传附件 图片_\d{8}-\d{6}\.jpg$", d.Logs[^1].Text);
        // 添加附件不发通知
        Assert.DoesNotContain(Notifier.Sent, x => x.Text.Contains("附件") || x.Title.Contains("附件"));
    }

    // ---------- 删除 ----------

    [Fact]
    public async Task DeleteRules()
    {
        var id = await NewAccepted("删除规则", "王工");
        var wm = U("王敏");
        var wg = U("王工");
        var mine = await UpPdf(wg, "工程师的.pdf");
        var sales = await UpPdf(wm, "销售的.pdf");
        var secret = await UpPdf(wm, "底价.pdf");
        await Orders.AddAttachments(id, Att(mine), wg);
        await Orders.AddAttachments(id, Att(sales), wm);
        await Orders.AddAttachments(id, AttR(secret), wm);

        await Fails(40300, "只能删除自己上传的附件", () => Orders.DeleteAttachment(id, sales, wg));
        await Fails(40300, "只能删除自己上传的附件", () => Orders.DeleteAttachment(id, mine, wm));
        await Fails(40400, "附件不存在", () => Orders.DeleteAttachment(id, 999999, wm));
        await Fails(40400, "附件不存在", () => Orders.DeleteAttachment(id, secret, wg)); // 受限：对工程师不暴露存在性
        await Fails(40400, "工单不存在", () => Orders.DeleteAttachment(999999, mine, wg));
        await Fails(40300, "无权查看该工单", () => Orders.DeleteAttachment(id, mine, U("刘工")));
        var other = await NewPending("别的单");
        await Fails(40400, "附件不存在", () => Orders.DeleteAttachment(other, mine, wg)); // fileId 不属于该单

        var path = Files.FullPath(FileRow(mine));
        Tick();
        var d = await Orders.DeleteAttachment(id, mine, wg);
        Assert.DoesNotContain(d.Attachments, a => a.Id == mine);
        Assert.Equal("王工 删除附件 工程师的.pdf", d.Logs[^1].Text);
        Assert.Equal(LogKind.Attachment, d.Logs[^1].Kind);
        var row = FileRow(mine);
        Assert.Equal(Clock.Now, row.DeletedAt);
        Assert.Equal(wg.Id, row.DeletedById);
        Assert.Equal(id, row.OrderId);       // OrderId 保留
        Assert.True(File.Exists(path));       // 磁盘文件保留
        Assert.Null(Db.Files.AsNoTracking().FirstOrDefault(f => f.Id == mine)); // 全局过滤：对所有读取路径不存在
        await Fails(40400, "附件不存在", () => Orders.DeleteAttachment(id, mine, wg));
        await Fails(40400, "附件不存在", () => Orders.DeleteAttachment(id, mine, Sup));
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(mine, Sup));

        // 受限附件的删除记录对工程师不可见
        d = await Orders.DeleteAttachment(id, secret, wm);
        Assert.Equal("王敏 删除附件 底价.pdf", d.Logs[^1].Text);
        Assert.Equal(LogKind.AttachmentPrivate, d.Logs[^1].Kind);
        Assert.DoesNotContain((await Orders.Detail(id, wg)).Logs, l => l.Text.Contains("底价"));

        // 主管可以删任何人的
        d = await Orders.DeleteAttachment(id, sales, Sup);
        Assert.Empty(d.Attachments);
        Assert.Equal(20, d.AttachmentRemaining);
        Assert.Equal("赵主管 删除附件 销售的.pdf", d.Logs[^1].Text);
    }

    [Fact]
    public async Task DeleteOnClosedAndVoided()
    {
        var wg = U("王工");
        var wm = U("王敏");
        // 闭环前各传一个
        var id = await NewAccepted("闭环后删除", "王工", ServiceType.Remote);
        var engFile = await UpPdf(wg, "工程师.pdf");
        var salesFile = await UpPdf(wm, "销售.pdf");
        await Orders.AddAttachments(id, Att(engFile), wg);
        await Orders.AddAttachments(id, Att(salesFile), wm);
        Assert.True((await Orders.Detail(id, wg)).Attachments.Single(a => a.Id == engFile).CanDelete);
        await Orders.Start(id, wg);
        await Orders.Complete(id, Done(wg), wg);
        Assert.True((await Orders.Detail(id, wg)).Attachments.Single(a => a.Id == engFile).CanDelete); // 待验收仍可删
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        await Orders.Close(id, Sup);

        await Fails(40900, "工单已闭环，只有主管可以删除附件", () => Orders.DeleteAttachment(id, engFile, wg));
        await Fails(40900, "工单已闭环，只有主管可以删除附件", () => Orders.DeleteAttachment(id, salesFile, wm));
        await Fails(40300, "只能删除自己上传的附件", () => Orders.DeleteAttachment(id, salesFile, wg)); // 先判上传人
        Assert.All((await Orders.Detail(id, wg)).Attachments, a => Assert.False(a.CanDelete));
        Assert.All((await Orders.Detail(id, wm)).Attachments, a => Assert.False(a.CanDelete));
        Assert.All((await Orders.Detail(id, Sup)).Attachments, a => Assert.True(a.CanDelete));
        var d = await Orders.DeleteAttachment(id, engFile, Sup);
        Assert.Single(d.Attachments);
        Assert.Equal(OrderStatus.Closed, d.Logs[^1].ToStatus);

        // 作废：上传人不能删，主管可以删、可以切换
        var voided = await NewPending("作废后删除");
        var f1 = await UpPdf(wm, "v1.pdf");
        var f2 = await UpPdf(wm, "v2.pdf");
        await Orders.AddAttachments(voided, Att(f1, f2), wm);
        await Orders.Void(voided, new ReasonRequest(), Sup);
        await Fails(40900, "工单已作废，不能删除附件", () => Orders.DeleteAttachment(voided, f1, wm));
        Assert.All((await Orders.Detail(voided, wm)).Attachments, a => Assert.False(a.CanDelete));
        d = await Orders.SetAttachmentRestricted(voided, f2, new RestrictedRequest { Restricted = true }, Sup);
        Assert.True(d.Attachments.Single(a => a.Id == f2).Restricted);
        Assert.Single((await Orders.DeleteAttachment(voided, f1, Sup)).Attachments);
    }

    // ---------- 切换受限 ----------

    [Fact]
    public async Task ToggleRestricted()
    {
        var id = await NewAccepted("切换受限", "王工");
        var wm = U("王敏");
        var wg = U("王工");
        var f = await UpPdf(wm, "方案.pdf");
        var d = await Orders.AddAttachments(id, Att(f), wm);
        var count = d.Logs.Count;
        RestrictedRequest Body(bool? v) => new() { Restricted = v };

        await Fails(40300, "只有主管可以修改附件可见范围", () => Orders.SetAttachmentRestricted(id, f, Body(true), wm));
        await Fails(40300, "只有主管可以修改附件可见范围", () => Orders.SetAttachmentRestricted(id, f, Body(true), wg));
        await Fails(40000, "请选择可见范围", () => Orders.SetAttachmentRestricted(id, f, Body(null), Sup));
        await Fails(40400, "工单不存在", () => Orders.SetAttachmentRestricted(999999, f, Body(true), Sup));
        await Fails(40400, "附件不存在", () => Orders.SetAttachmentRestricted(id, 999999, Body(true), Sup));

        // 值未变化：不写记录
        d = await Orders.SetAttachmentRestricted(id, f, Body(false), Sup);
        Assert.Equal(count, d.Logs.Count);

        d = await Orders.SetAttachmentRestricted(id, f, Body(true), Sup);
        Assert.True(d.Attachments.Single().Restricted);
        Assert.True(d.Attachments.Single().CanToggleRestricted);
        Assert.Equal("赵主管 将附件 方案.pdf 设为仅销售和主管可见", d.Logs[^1].Text);
        Assert.Equal(LogKind.AttachmentPrivate, d.Logs[^1].Kind);
        Assert.True(FileRow(f).Restricted);
        d = await Orders.SetAttachmentRestricted(id, f, Body(true), Sup);
        Assert.Equal(count + 1, d.Logs.Count);

        d = await Orders.SetAttachmentRestricted(id, f, Body(false), Sup);
        Assert.False(d.Attachments.Single().Restricted);
        Assert.Equal("赵主管 将附件 方案.pdf 设为所有人可见", d.Logs[^1].Text);
        Assert.Equal(LogKind.Attachment, d.Logs[^1].Kind);
        Assert.Equal(count + 2, d.Logs.Count);

        // 建单照片（旧数据）同样可以设受限
        var photo = Db.Files.AsNoTracking().First(x => x.OrderId == OrderId(Jiangsu) && x.Category == FileCategory.OrderPhoto).Id;
        d = await Orders.SetAttachmentRestricted(OrderId(Jiangsu), photo, Body(true), Sup);
        Assert.True(d.Attachments.Single(a => a.Id == photo).Restricted);
        Assert.DoesNotContain((await Orders.Detail(OrderId(Jiangsu), U("李工"))).Files.OrderPhotos, p => p.Id == photo);
        // 非附件（打卡照片）不能切换
        var punch = Db.Files.AsNoTracking().First(x => x.OrderId == OrderId(Jiangsu) && x.Category == FileCategory.PunchPhoto).Id;
        await Fails(40400, "附件不存在", () => Orders.SetAttachmentRestricted(OrderId(Jiangsu), punch, Body(true), Sup));
        await Fails(40400, "附件不存在", () => Orders.DeleteAttachment(OrderId(Jiangsu), punch, Sup));
    }

    // ---------- 工程师可见性 ----------

    [Fact]
    public async Task EngineerNeverReceivesRestrictedAttachments()
    {
        var id = OrderId(Jiangsu);
        var li = U("李工"); // 负责人
        var secret = Db.Files.AsNoTracking().Single(f => f.OrderId == id && f.Restricted);
        Assert.Equal("报价单.pdf", secret.DisplayName);
        // 让受限附件也留下状态记录
        var wm = U("王敏");
        var more = await UpPdf(wm, "合同底价.pdf");
        var img = (await Up(wm, Jpg, "机密照片.jpg", name: "机密照片.jpg")).Id;
        await Orders.AddAttachments(id, AttR(more, img), wm);
        var moreRow = FileRow(more);
        var imgRow = FileRow(img);

        var d = await Orders.Detail(id, li);
        Assert.Equal(4, d.Attachments.Count); // 3 张建单照片 + 现场情况说明.pdf
        Assert.All(d.Attachments, a => Assert.False(a.Restricted));
        Assert.DoesNotContain(d.Attachments, a => a.Id == secret.Id || a.Id == more || a.Id == img);
        Assert.Equal(3, d.Files.OrderPhotos.Count);
        Assert.DoesNotContain(d.Files.OrderPhotos, p => p.Id == img);
        Assert.DoesNotContain(d.Logs, l => l.Kind == LogKind.AttachmentPrivate);
        Assert.Equal(13, d.AttachmentRemaining); // 20 − 7：名额含受限附件
        Assert.False(d.CanRestrictAttachment);
        Assert.True(d.CanAddAttachment);
        // 序列化后的整个详情不含受限文件的任何信息
        var json = Json(d);
        foreach (var f in new[] { secret, moreRow, imgRow })
        {
            Assert.DoesNotContain(f.Path, json);
            Assert.DoesNotContain(Path.GetFileNameWithoutExtension(f.Path), json);
        }
        Assert.DoesNotContain("报价单", json);
        Assert.DoesNotContain("合同底价", json);
        Assert.DoesNotContain("机密照片", json);
        Assert.DoesNotContain("AttachmentPrivate", json);
        Assert.DoesNotContain("仅销售和主管可见", json);
        Assert.Contains("现场情况说明.pdf", json);

        // 列表接口不带附件信息
        var listJson = Json(await Orders.Mine(li, null));
        Assert.DoesNotContain("报价单", listJson);
        Assert.DoesNotContain("attachments", listJson);

        // 下载、删除、切换：一律 40400
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(secret.Id, li));
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(img, li));
        await Fails(40400, "附件不存在", () => Orders.DeleteAttachment(id, secret.Id, li));
        await Fails(40300, "只有主管可以修改附件可见范围", () => Orders.SetAttachmentRestricted(id, secret.Id, new RestrictedRequest { Restricted = false }, li));

        // 销售、主管能看到
        foreach (var u in new[] { wm, U("孙磊"), Sup })
        {
            var full = await Orders.Detail(id, u);
            Assert.Equal(7, full.Attachments.Count);
            Assert.Equal(3, full.Attachments.Count(a => a.Restricted));
            Assert.Contains(full.Files.OrderPhotos, p => p.Id == img);
            Assert.Contains(full.Logs, l => l.Kind == LogKind.AttachmentPrivate && l.Text == "王敏 上传附件 合同底价.pdf、机密照片.jpg（仅销售和主管可见）");
            Assert.Equal(13, full.AttachmentRemaining);
            Assert.Equal("报价单.pdf", (await Files.ResolveDownload(secret.Id, u)).Name);
        }
    }

    [Fact]
    public async Task EngineerLosesOwnAttachmentOnceRestricted()
    {
        var id = await NewAccepted("自己的也看不到", "王工");
        var wg = U("王工");
        var f = await UpPdf(wg, "现场记录.pdf");
        var d = await Orders.AddAttachments(id, Att(f), wg);
        Assert.True(d.Attachments.Single().CanDelete);
        Assert.Equal("现场记录.pdf", (await Files.ResolveDownload(f, wg)).Name);

        await Orders.SetAttachmentRestricted(id, f, new RestrictedRequest { Restricted = true }, Sup);
        d = await Orders.Detail(id, wg);
        Assert.Empty(d.Attachments);
        Assert.Equal(19, d.AttachmentRemaining);
        await Fails(40400, "附件不存在", () => Orders.DeleteAttachment(id, f, wg));
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(f, wg));
        Assert.DoesNotContain(d.Logs, l => l.Text.Contains("设为仅销售和主管可见"));

        await Orders.SetAttachmentRestricted(id, f, new RestrictedRequest { Restricted = false }, Sup);
        Assert.Single((await Orders.Detail(id, wg)).Attachments);
    }

    [Fact]
    public async Task CapabilityFlagsFollowRules()
    {
        var id = await NewAccepted("能力标记", "王工");
        await Orders.SetAssistants(id, new AssistantsRequest { EngineerIds = new() { Emp("陈工").Id } }, Sup);
        var wm = U("王敏");
        var wg = U("王工");
        var fSales = await UpPdf(wm, "s.pdf");
        var fEng = await UpPdf(wg, "e.pdf");
        await Orders.AddAttachments(id, Att(fSales), wm);
        await Orders.AddAttachments(id, Att(fEng), wg);

        async Task Check(string who, bool canAdd, bool canRestrict, bool delSales, bool delEng, bool toggle)
        {
            var d = await Orders.Detail(id, U(who));
            Assert.Equal(canAdd, d.CanAddAttachment);
            Assert.Equal(canRestrict, d.CanRestrictAttachment);
            Assert.Equal(delSales, d.Attachments.Single(a => a.Id == fSales).CanDelete);
            Assert.Equal(delEng, d.Attachments.Single(a => a.Id == fEng).CanDelete);
            Assert.All(d.Attachments, a => Assert.Equal(toggle, a.CanToggleRestricted));
            Assert.Equal(18, d.AttachmentRemaining);
            Assert.Equal(20, d.AttachmentLimit);
        }
        await Check("王敏", true, true, true, false, false);    // 建单销售
        await Check("孙磊", false, true, false, false, false);  // 其他销售：不能加，但角色上可设受限
        await Check("王工", true, false, false, true, false);   // 负责人
        await Check("陈工", true, false, false, false, false);  // 协助人
        await Check("赵主管", true, true, true, true, true);

        // 暂停搁置：仍可添加
        await Orders.Hold(id, HoldBody("WaitParts"), Sup);
        await Check("王工", true, false, false, true, false);
        Assert.Equal(3, (await Orders.AddAttachments(id, Att(await UpPdf(wg, "hold.pdf")), wg)).Attachments.Count);
    }

    // ---------- FileService.Attach 加固 ----------

    [Fact]
    public async Task AttachHardening()
    {
        var id = await NewAccepted("挂载加固", "王工", ServiceType.Remote);
        var wg = U("王工");
        await Orders.Start(id, wg);
        // 文档不能作为完工照片
        var doc = await UpPdf(wg, "报告.pdf");
        var body = Done(wg);
        body.ProcessPhotoIds = new() { doc };
        await Fails(40000, "处理照片只能是图片或视频", () => Orders.Complete(id, body, wg));
        Db.ChangeTracker.Clear();
        body = Done(wg);
        body.SignaturePhotoIds = new() { doc };
        await Fails(40000, "客户签字件只能是图片或视频", () => Orders.Complete(id, body, wg));
        Db.ChangeTracker.Clear();

        // 本单附件（图片）不能被挪作完工照片
        var img = (await Up(wg, Jpg, "a.jpg")).Id;
        await Orders.AddAttachments(id, Att(img), wg);
        body = Done(wg);
        body.ProcessPhotoIds = new() { img };
        await Fails(40000, "处理照片已用于本单其他资料，请重新上传", () => Orders.Complete(id, body, wg));
        Db.ChangeTracker.Clear();
        Assert.Equal(FileCategory.Attachment, FileRow(img).Category);

        // 已删除的文件不能再挂载
        var gone = (await Up(wg, Jpg, "b.jpg")).Id;
        await Orders.AddAttachments(id, Att(gone), wg);
        await Orders.DeleteAttachment(id, gone, wg);
        body = Done(wg);
        body.ProcessPhotoIds = new() { gone };
        await Fails(40000, "处理照片不存在，请重新上传", () => Orders.Complete(id, body, wg));
        Db.ChangeTracker.Clear();

        // 反过来：完工照片不能再当附件添加
        var done = await Orders.Complete(id, Done(wg), wg);
        var process = done.Files.ProcessPhotos[0].Id;
        await Fails(40000, "附件已被使用，请重新上传", () => Orders.AddAttachments(id, Att(process), wg));
        // 文档不能当打卡照片
        var onsite = await NewAccepted("现场单", "王工");
        var punchDoc = await UpPdf(wg, "p.pdf");
        await Fails(40000, "打卡照片只能是图片或视频", () => Punches.Create(wg, new PunchRequest
        {
            OrderId = onsite, Kind = PunchKind.Arrive, Latitude = 31.30, Longitude = 120.72, Address = "x", PhotoFileId = punchDoc
        }));
    }

    // ---------- 下载 ----------

    [Fact]
    public async Task DownloadRules()
    {
        var id = OrderId(Jiangsu);
        var open = Db.Files.AsNoTracking().Single(f => f.OrderId == id && f.DisplayName == "现场情况说明.pdf");
        var secret = Db.Files.AsNoTracking().Single(f => f.OrderId == id && f.Restricted);
        var punch = Db.Files.AsNoTracking().First(f => f.OrderId == id && f.Category == FileCategory.PunchPhoto);

        foreach (var who in new[] { "赵主管", "王敏", "孙磊", "李工" })
        {
            var info = await Files.ResolveDownload(open.Id, U(who));
            Assert.Equal("现场情况说明.pdf", info.Name);
            Assert.Equal("application/pdf", info.MimeType);
            Assert.Equal(Files.FullPath(open), info.FullPath);
            Assert.True(File.Exists(info.FullPath));
        }
        // 没有原始名的文件：下载名为生成名
        var p = await Files.ResolveDownload(punch.Id, U("李工"));
        Assert.Equal($"打卡照片_{punch.CreatedAt:yyyyMMdd-HHmmss}.png", p.Name);
        Assert.Equal("image/png", p.MimeType);

        await Fails(40300, "无权查看该工单", () => Files.ResolveDownload(open.Id, U("陈工")));   // 无关工程师
        await Fails(40300, "无权查看该工单", () => Files.ResolveDownload(punch.Id, U("陈工")));
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(secret.Id, U("李工")));      // 受限 + 工程师
        await Fails(40300, "无权查看该工单", () => Files.ResolveDownload(secret.Id, U("陈工")));  // 先判工单
        Assert.Equal("报价单.pdf", (await Files.ResolveDownload(secret.Id, U("王敏"))).Name);
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(999999, Sup));

        // 头像
        var li = U("李工");
        var avatar = (await Up(li, Png, "me.png", "Avatar")).Id;
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(avatar, li));
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(avatar, Sup));

        // 未挂载：仅上传人
        var loose = await UpPdf(li, "草稿.pdf");
        Assert.Equal("草稿.pdf", (await Files.ResolveDownload(loose, li)).Name);
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(loose, Sup));
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(loose, U("王敏")));

        // 已删除
        var wm = U("王敏");
        var del = await UpPdf(wm, "删掉的.pdf");
        await Orders.AddAttachments(id, Att(del), wm);
        Assert.Equal("删掉的.pdf", (await Files.ResolveDownload(del, li)).Name);
        await Orders.DeleteAttachment(id, del, wm);
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(del, wm));
        await Fails(40400, "文件不存在", () => Files.ResolveDownload(del, Sup));

        // 磁盘文件缺失
        File.Delete(Files.FullPath(open));
        await Fails(40400, "文件已丢失，请联系管理员", () => Files.ResolveDownload(open.Id, Sup));

        // 待认领工单：任何工程师都能看，也就能下载公开附件
        var pending = await NewPending("待认领下载");
        var pf = await UpPdf(wm, "公开.pdf");
        await Orders.AddAttachments(pending, Att(pf), wm);
        Assert.Equal("公开.pdf", (await Files.ResolveDownload(pf, U("刘工"))).Name);
    }

    [Fact]
    public async Task SoftDeletedFilesVanishFromEveryReadPath()
    {
        var id = await NewAccepted("软删除", "王工");
        var wm = U("王敏");
        var img = (await Up(wm, Jpg, "x.jpg")).Id;
        var d = await Orders.AddAttachments(id, Att(img), wm);
        Assert.Single(d.Files.OrderPhotos);
        d = await Orders.DeleteAttachment(id, img, wm);
        Assert.Empty(d.Attachments);
        Assert.Empty(d.Files.OrderPhotos);
        Assert.Equal(20, d.AttachmentRemaining);
        var center = new FileCenterService(Db, View);
        Assert.Equal(0, (await center.Query(new FileCenterQuery { OrderId = id }, Sup)).Total);
        Assert.Equal(1, Db.Files.IgnoreQueryFilters().Count(f => f.OrderId == id && f.DeletedAt != null));
    }
}
