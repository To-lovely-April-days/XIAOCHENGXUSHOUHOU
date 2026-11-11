using System.Text;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.2 安全审查后补的回归测试（每个用例对应一个已修复的缺陷）</summary>
public sealed class V12ReviewTests : V12TestBase
{
    private const string Jiangsu = "江苏某化工研究院";
    // ---------- 受限附件的名称不再留在工程师可见的状态记录里 ----------

    [Fact]
    public async Task RestrictingHidesEarlierLogLinesThatNameTheFile()
    {
        var id = await NewAccepted("记录收紧", "王工");
        var wm = U("王敏");
        var wg = U("王工");
        var secret = await UpPdf(wm, "底价核算.pdf");
        var other = await UpPdf(wm, "现场照片说明.pdf");
        await Orders.AddAttachments(id, Att(secret), wm);
        Tick();
        await Orders.AddAttachments(id, Att(other), wm);
        var before = await Orders.Detail(id, wg);
        Assert.Contains(before.Logs, l => l.Text == "王敏 上传附件 底价核算.pdf");

        await Orders.SetAttachmentRestricted(id, secret, new RestrictedRequest { Restricted = true }, Sup);

        // 工程师（负责人）：整个详情里不再出现受限文件名；另一个公开附件的记录不受影响
        var eng = await Orders.Detail(id, wg);
        Assert.DoesNotContain("底价核算", Json(eng));
        Assert.Contains(eng.Logs, l => l.Text == "王敏 上传附件 现场照片说明.pdf");
        // 销售 / 主管仍能看到全部记录，原记录变为受限类别
        var sales = await Orders.Detail(id, wm);
        Assert.Equal(LogKind.AttachmentPrivate, sales.Logs.Single(l => l.Text == "王敏 上传附件 底价核算.pdf").Kind);
        Assert.Equal(LogKind.Attachment, sales.Logs.Single(l => l.Text == "王敏 上传附件 现场照片说明.pdf").Kind);

        // 取消受限 → 再设受限：「设为所有人可见」那条记录也带着文件名，同样被收起
        await Orders.SetAttachmentRestricted(id, secret, new RestrictedRequest { Restricted = false }, Sup);
        Assert.Contains((await Orders.Detail(id, wg)).Logs, l => l.Text == "赵主管 将附件 底价核算.pdf 设为所有人可见");
        await Orders.SetAttachmentRestricted(id, secret, new RestrictedRequest { Restricted = true }, Sup);
        Assert.DoesNotContain("底价核算", Json(await Orders.Detail(id, wg)));
    }

    [Fact]
    public async Task RestrictingHidesTruncatedNameInBatchLog()
    {
        var id = await NewAccepted("长名收紧", "王工");
        var wm = U("王敏");
        var longName = new string('价', 60) + ".pdf"; // 记录里被截成 39 个字符 + …
        var secret = await UpPdf(wm, longName);
        await Orders.AddAttachments(id, Att(secret), wm);
        Assert.Contains(new string('价', 39) + "…", Json(await Orders.Detail(id, U("王工"))));
        await Orders.SetAttachmentRestricted(id, secret, new RestrictedRequest { Restricted = true }, Sup);
        Assert.DoesNotContain("价价价", Json(await Orders.Detail(id, U("王工"))));
    }

    // ---------- 头像 ↔ 工单文件互相挪用 ----------

    [Fact]
    public async Task AttachedFileCannotBecomeAvatar()
    {
        var id = await NewAccepted("头像挪用", "王工");
        var wg = U("王工");
        var img = (await Up(wg, Jpg, "a.jpg")).Id;
        await Orders.AddAttachments(id, Att(img), wg);
        // 修复前：SetAvatar 把类别改成 Avatar，附件从工单里消失（等于绕过「闭环后不能删除」，受限图片还会变成公开头像）
        await Fails(40000, "头像文件已用于工单，请重新上传", () => Home.SetAvatar(wg, img));
        Db.ChangeTracker.Clear();
        Assert.Equal(FileCategory.Attachment, FileRow(img).Category);
        Assert.Contains((await Orders.Detail(id, wg)).Attachments, a => a.Id == img);

        // 打卡照片同理
        var punch = Db.Files.AsNoTracking().First(f => f.OrderId == OrderId(Jiangsu) && f.Category == FileCategory.PunchPhoto);
        await Fails(40000, "头像文件已用于工单，请重新上传", () => Home.SetAvatar(new CurrentUser(punch.UploaderId, Role.Engineer), punch.Id));
        Db.ChangeTracker.Clear();
        Assert.Equal(FileCategory.PunchPhoto, FileRow(punch.Id).Category);

        // 未挂载的图片仍可设为头像
        var loose = (await Up(wg, Png, "me.png", "Avatar")).Id;
        await Home.SetAvatar(wg, loose);
        Assert.Equal(loose, Emp("王工").AvatarFileId);
    }

    [Fact]
    public async Task AvatarFileCannotBeMountedOnOrder()
    {
        var id = await NewAccepted("头像当附件", "王工", ServiceType.Remote);
        var wg = U("王工");
        var avatar = (await Up(wg, Png, "me.png", "Avatar")).Id;
        await Home.SetAvatar(wg, avatar);
        await Fails(40000, "附件不存在，请重新上传", () => Orders.AddAttachments(id, Att(avatar), wg));
        Db.ChangeTracker.Clear();
        var create = NewOrder("头像建单");
        create.Attachments = new() { new AttachmentItem { FileId = avatar } };
        await Fails(40000, "附件不存在，请重新上传", () => Orders.Create(create, wg));
        Db.ChangeTracker.Clear();
        await Orders.Start(id, wg);
        var body = Done(wg);
        body.ProcessPhotoIds = new() { avatar };
        await Fails(40000, "处理照片不存在，请重新上传", () => Orders.Complete(id, body, wg));
        Db.ChangeTracker.Clear();
        Assert.Equal(FileCategory.Avatar, FileRow(avatar).Category);
        Assert.Null(FileRow(avatar).OrderId);
    }

    // ---------- 上传识别 ----------

    [Theory]
    [InlineData("The free trial ends tomorrow\n")]      // 偏移 4 = free
    [InlineData("Too wide for the cabinet door\n")]     // 偏移 4 = wide
    [InlineData("BMI,weight,height\n22.1,70,178\n")]    // 以 BM 开头
    [InlineData("row skip this line\n")]                // 偏移 4 = skip
    public async Task PlainTextIsNotMistakenForMedia(string text)
    {
        var u = U("王敏");
        var bytes = Encoding.ASCII.GetBytes(text);
        // 修复前：这些文本被嗅探成视频 / BMP，txt、csv 上传报「文件内容与扩展名不符」
        Assert.Null(FileService.SniffMime(bytes));
        Assert.Equal("text/plain", (await Up(u, bytes, "说明.txt", name: "说明.txt")).MimeType);
        Assert.Equal("text/csv", (await Up(u, bytes, "数据.csv", name: "数据.csv")).MimeType);
        // 同样的内容冒充图片 / 视频扩展名时被拒绝，而不是存成 .mov / .bmp
        await Fails(40000, Mismatch, () => Up(u, bytes, "a.mov", name: "a.mov"));
        await Fails(40000, Mismatch, () => Up(u, bytes, "a.bmp", name: "a.bmp"));
        await Fails(40000, "只能上传图片或视频", () => Up(u, bytes, "a.jpg", "OrderPhoto"));
    }

    [Fact]
    public async Task RealBmpAndVideoStillRecognised()
    {
        var bmp = Pad(new byte[] { 0x42, 0x4D, 0x36, 0x10, 0, 0, 0, 0, 0, 0, 0x36, 0, 0, 0, 0x28, 0 });
        Assert.Equal("image/bmp", FileService.SniffMime(bmp));
        Assert.Equal("image/bmp", (await Up(U("王敏"), bmp, "a.bmp")).MimeType);
        // mdat 在前、长度约 90MB（首字节 0x05）的 QuickTime
        Assert.Equal("video/quicktime", FileService.SniffMime(new byte[] { 0x05, 0x5D, 0x4A, 0x80, 0x6D, 0x64, 0x61, 0x74 }));
        // 64 位长度（size = 1）
        Assert.Equal("video/quicktime", FileService.SniffMime(new byte[] { 0, 0, 0, 1, 0x6D, 0x64, 0x61, 0x74 }));
    }

    [Theory]
    [InlineData("avif")]
    [InlineData("avis")]
    public async Task AvifIsRejectedInsteadOfStoredAsMp4(string brand)
    {
        var head = Pad(new byte[] { 0, 0, 0, 0x1C, 0x66, 0x74, 0x79, 0x70 }.Concat(Encoding.ASCII.GetBytes(brand)).ToArray());
        Assert.Null(FileService.SniffMime(head));
        await Fails(40000, Unsupported, () => Up(U("王敏"), head, "a.avif", name: "a.avif"));
        await Fails(40000, "只能上传图片或视频", () => Up(U("王敏"), head, "a.avif", "OrderPhoto"));
    }

    [Theory]
    [InlineData("8")]
    [InlineData("0")]
    [InlineData(" 4")]
    public async Task NumericCategoryIsRejected(string category) =>
        await Fails(40000, "文件类别不正确", () => Up(U("王敏"), Jpg, "a.jpg", category));

    // ---------- 文件名 ----------

    [Fact]
    public void CleanNameStripsInvisibleFormatCharacters()
    {
        // RLO（U+202E）会让「报价\u202Efdp.exe」显示成「报价exe.pdf」；零宽字符同理
        Assert.Equal("报价fdp.exe", FileService.CleanName("报价\u202Efdp.exe"));
        Assert.Equal("ab.pdf", FileService.CleanName("a\u200B\u200Db\uFEFF.pdf"));
        Assert.Equal("a b.pdf", FileService.CleanName("a" + (char)0x2028 + "b.pdf")); // 行分隔符按空白处理
        Assert.Equal("未命名.pdf", FileService.CleanName("\u202E.pdf"));
    }

    [Fact]
    public void CleanNameNeverSplitsSurrogatePair()
    {
        // 截断点正好落在 emoji（代理对）中间
        var name = new string('a', 95) + "😀😀😀.pdf";
        var clean = FileService.CleanName(name)!;
        Assert.True(clean.Length <= 100);
        Assert.EndsWith(".pdf", clean);
        for (var i = 0; i < clean.Length; i++)
            if (char.IsHighSurrogate(clean[i])) Assert.True(i + 1 < clean.Length && char.IsLowSurrogate(clean[i + 1]));
            else if (char.IsLowSurrogate(clean[i])) Assert.True(i > 0 && char.IsHighSurrogate(clean[i - 1]));
        Assert.Equal(clean, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(clean)));
    }

    [Fact]
    public async Task ExecutableNamePassedAsFileNameWithInvisibleCharIsRejected()
    {
        var u = U("王敏");
        // 扩展名取 fileName 的最后一段；不可见字符不能把 .exe「变」成 .pdf
        await Fails(40000, Unsupported, () => Up(u, Pdf, "x.pdf", name: "报价\u202Efdp.exe"));
        await Fails(40000, Unsupported, () => Up(u, Pdf, "x.pdf", name: "a.pdf\u200B"));
        await Fails(40000, Unsupported, () => Up(u, Pdf, "x.pdf", name: "a.pdf."));
        await Fails(40000, Unsupported, () => Up(u, Pdf, "x.pdf", name: "a.pdf/"));
        await Fails(40000, Unsupported, () => Up(u, Pdf, "x.pdf", name: "a.pdf\0.html"));
    }

    [Fact]
    public async Task OverlongMultipartNameIsBounded()
    {
        var d = await Up(U("王敏"), Jpg, new string('长', 5000) + ".jpg");
        Assert.True(FileRow(d.Id).OriginalName.Length <= 200);
    }

    // ---------- 下载 ----------

    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("image/png; charset=\"x", "application/octet-stream")]
    [InlineData("text/html\r\nSet-Cookie: a=b", "application/octet-stream")]
    [InlineData("图片/png", "application/octet-stream")]
    [InlineData("", "application/octet-stream")]
    [InlineData(null, "application/octet-stream")]
    public void SafeMimeFallsBackForLegacyGarbage(string? stored, string expected) =>
        Assert.Equal(expected, FileService.SafeMime(stored));

    [Fact]
    public async Task DownloadOfLegacyRowWithInvalidMimeDoesNotBreak()
    {
        // v1.1 存的是客户端声明的 Content-Type，可能不是合法的 media type
        var id = OrderId(Jiangsu);
        var punch = Db.Files.First(f => f.OrderId == id && f.Category == FileCategory.PunchPhoto);
        punch.MimeType = "image/png; name=\"a\nb\"";
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        Assert.Equal("application/octet-stream", (await Files.ResolveDownload(punch.Id, Sup)).MimeType);
    }

    // ---------- 静态地址 /uploads ----------

    [Fact]
    public async Task StaticUrlIsBlockedForRestrictedOrDeletedDocumentsOnly()
    {
        var id = await NewAccepted("静态地址", "王工");
        var wm = U("王敏");
        var open = await UpPdf(wm, "公开.pdf");
        var secret = await UpPdf(wm, "受限.pdf");
        var gone = await UpPdf(wm, "删除.pdf");
        var loose = await UpPdf(wm, "未挂载.pdf");
        var img = (await Up(wm, Jpg, "受限图.jpg")).Id;
        await Orders.AddAttachments(id, new AttachmentsRequest
        {
            Attachments = new()
            {
                new() { FileId = open }, new() { FileId = secret, Restricted = true }, new() { FileId = gone },
                new() { FileId = img, Restricted = true }
            }
        }, wm);
        await Orders.DeleteAttachment(id, gone, wm);
        Db.ChangeTracker.Clear();

        Assert.False(await Files.StaticBlocked(FileRow(open).Path));
        Assert.False(await Files.StaticBlocked("/" + FileRow(open).Path));
        Assert.True(await Files.StaticBlocked(FileRow(secret).Path));
        Assert.True(await Files.StaticBlocked("/" + FileRow(secret).Path));
        Assert.True(await Files.StaticBlocked(FileRow(gone).Path));
        Assert.False(await Files.StaticBlocked(FileRow(loose).Path));
        // 图片 / 视频的静态地址不动（预览、缩略图、v1.1 小程序都依赖）
        Assert.False(await Files.StaticBlocked(FileRow(img).Path));
        // 库里没有的路径、空路径：交给静态文件中间件自己处理
        Assert.False(await Files.StaticBlocked("2026/11/nope.pdf"));
        Assert.False(await Files.StaticBlocked(""));
        Assert.False(await Files.StaticBlocked(null));

        // 取消受限后恢复
        await Orders.SetAttachmentRestricted(id, secret, new RestrictedRequest { Restricted = false }, Sup);
        Assert.False(await Files.StaticBlocked(FileRow(secret).Path));
    }

    [Fact]
    public async Task EveryStoredExtensionHasAStaticContentType()
    {
        // 修复前：.heic 不在 ASP.NET 默认扩展名表里，/uploads 对它返回 404（上传成功却永远打不开）
        var types = FileService.StoredTypes;
        Assert.Equal("image/heic", types[".heic"]);
        Assert.Equal("image/heic", types[".heif"]);
        Assert.Equal("text/csv", types[".csv"]);
        var heic = Pad(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 });
        var u = U("王敏");
        foreach (var (content, name) in new[] { (heic, "a.heic"), (Jpg, "a.jpeg"), (Png, "a"), (Mp4, "a.3gp"), (Mp4, "a.m4v"), (Mp4, "a.mov"), (Pdf, "a.pdf"), (Ole, "a.xls") })
        {
            var d = await Up(u, content, name, name: name.Contains('.') ? name : null);
            Assert.Equal(d.MimeType, types["." + d.Ext]);
        }
    }
}
