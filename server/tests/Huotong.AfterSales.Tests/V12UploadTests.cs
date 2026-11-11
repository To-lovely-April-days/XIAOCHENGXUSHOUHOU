using System.IO.Compression;
using System.Text;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.2 测试公共夹具：真实文件头的内存文件、上传与附件请求的便捷方法</summary>
public abstract class V12TestBase : V11TestBase
{
    protected const string Unsupported = "不支持的文件类型，仅支持图片、视频、Word、Excel、PPT、PDF、TXT、CSV";
    protected const string Mismatch = "文件内容与扩展名不符，请确认文件未损坏";
    protected const string RestrictDenied = "只有销售和主管可以设置「仅销售和主管可见」";

    /// <summary>可声明任意长度的内存文件：Length 用声明值，内容只有文件头（不真的分配大内存）</summary>
    protected sealed class MemFile : IFormFile
    {
        private readonly byte[] _content;
        public MemFile(byte[] content, string fileName, long? declaredLength = null, string contentType = "application/octet-stream")
        {
            _content = content;
            FileName = fileName;
            Length = declaredLength ?? content.Length;
            ContentType = contentType;
        }
        public string ContentType { get; }
        public string ContentDisposition => "";
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length { get; }
        public string Name => "file";
        public string FileName { get; }
        public Stream OpenReadStream() => new MemoryStream(_content, false);
        public void CopyTo(Stream target) => target.Write(_content);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => target.WriteAsync(_content, cancellationToken).AsTask();
    }

    protected static byte[] Pad(byte[] head, int size = 64)
    {
        var b = Enumerable.Repeat((byte)0x20, Math.Max(size, head.Length)).ToArray();
        head.CopyTo(b, 0);
        return b;
    }

    protected static readonly byte[] Jpg = Pad(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46 });
    protected static readonly byte[] Png = Pad(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    protected static readonly byte[] Mp4 = Pad(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D });
    protected static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    protected static readonly byte[] Ole = Pad(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, 512);
    protected static readonly byte[] Exe = Pad(new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
    protected static readonly byte[] Txt = Encoding.UTF8.GetBytes("现场情况：升温到 180℃ 报 E03\n");
    protected static readonly byte[] Csv = Encoding.UTF8.GetBytes("时间,温度\n10:00,180\n");

    protected static byte[] Zip(params string[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var name in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write("<x/>");
            }
        return ms.ToArray();
    }

    /// <summary>含 [Content_Types].xml 的 OOXML 包</summary>
    protected static byte[] Ooxml(string part = "word/document.xml") => Zip("[Content_Types].xml", part);

    protected Task<FileDto> Up(CurrentUser u, byte[] content, string fileName, string category = "Attachment",
        string? name = null, long? declared = null, string contentType = "application/octet-stream") =>
        Files.Upload(new MemFile(content, fileName, declared, contentType), category, u, name);

    /// <summary>上传一个带原始文件名的 PDF 附件，返回 fileId</summary>
    protected async Task<int> UpPdf(CurrentUser u, string name) => (await Up(u, Pdf, name, name: name)).Id;

    protected static AttachmentsRequest Att(params int[] ids) =>
        new() { Attachments = ids.Select(i => new AttachmentItem { FileId = i }).ToList() };

    protected static AttachmentsRequest AttR(params int[] ids) =>
        new() { Attachments = ids.Select(i => new AttachmentItem { FileId = i, Restricted = true }).ToList() };

    protected StoredFile FileRow(int id) => Db.Files.IgnoreQueryFilters().AsNoTracking().Single(f => f.Id == id);

    /// <summary>远程单：建单 → 认领 → 开始 → 完工 → 定价 → 闭环</summary>
    protected async Task<int> NewClosed(string customer, string lead = "王工", string creator = "王敏")
    {
        var id = await NewAccepted(customer, lead, ServiceType.Remote, creator);
        var u = U(lead);
        await Orders.Start(id, u);
        await Orders.Complete(id, Done(u), u);
        await Orders.SetPrice(id, new PriceRequest { Price = 0 }, Sup);
        await Orders.Close(id, Sup);
        return id;
    }
}

/// <summary>v1.2 上传校验：扩展名 + 内容嗅探、大小、文件名清洗（CHANGES-v1.2 §3、§9.2 第 1–7 项）</summary>
public sealed class V12UploadTests : V12TestBase
{
    [Fact]
    public async Task AttachmentAcceptsEveryWhitelistedType()
    {
        var u = U("王敏");
        async Task Check(byte[] content, string fileName, string mime, FileKind kind, string ext)
        {
            var d = await Up(u, content, fileName, name: fileName);
            Assert.Equal(mime, d.MimeType);
            Assert.Equal(kind, d.Kind);
            Assert.Equal(ext, d.Ext);
            Assert.Equal(content.Length, d.Size);
            Assert.Equal(fileName, d.Name);
            Assert.Equal(FileCategory.Attachment, d.Category);
            Assert.EndsWith("." + ext, d.Url);
            var row = FileRow(d.Id);
            Assert.Null(row.OrderId);
            Assert.False(row.Restricted);
            // 存储文件名不可猜：32 位 GUID，不含原始文件名
            Assert.Matches(@"^\d{4}/\d{2}/[0-9a-f]{32}\." + ext + "$", row.Path);
            Assert.Equal(content, File.ReadAllBytes(Files.FullPath(row)));
        }
        await Check(Pdf, "报价单.pdf", "application/pdf", FileKind.Document, "pdf");
        await Check(Ooxml(), "验收报告.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", FileKind.Document, "docx");
        await Check(Ooxml("xl/workbook.xml"), "备件清单.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", FileKind.Document, "xlsx");
        await Check(Ooxml("ppt/presentation.xml"), "培训.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", FileKind.Document, "pptx");
        await Check(Ole, "旧版说明.doc", "application/msword", FileKind.Document, "doc");
        await Check(Ole, "旧表.xls", "application/vnd.ms-excel", FileKind.Document, "xls");
        await Check(Ole, "旧稿.ppt", "application/vnd.ms-powerpoint", FileKind.Document, "ppt");
        await Check(Txt, "说明.txt", "text/plain", FileKind.Document, "txt");
        await Check(Csv, "数据.csv", "text/csv", FileKind.Document, "csv");
        await Check(Jpg, "铭牌.jpg", "image/jpeg", FileKind.Image, "jpg");
        await Check(Png, "面板.png", "image/png", FileKind.Image, "png");
        await Check(Mp4, "故障视频.mp4", "video/mp4", FileKind.Video, "mp4");
    }

    [Fact]
    public async Task NamesAreCleanedOrGenerated()
    {
        var u = U("王敏");
        var d = await Up(u, Pdf, "blob", name: "..\\..\\报价 单?.PDF");
        Assert.Equal("报价 单.PDF", d.Name);
        Assert.Equal("pdf", d.Ext);
        Assert.EndsWith(".pdf", FileRow(d.Id).Path);
        Assert.Equal("报价 单.PDF", FileRow(d.Id).DisplayName);
        Assert.Equal("blob", FileRow(d.Id).OriginalName);

        // 不传 fileName：名称由服务端生成（multipart 文件名不用于展示）
        var img = await Up(u, Jpg, "tmp_abc.jpg");
        Assert.Null(FileRow(img.Id).DisplayName);
        Assert.Equal($"图片_{Clock.Now:yyyyMMdd-HHmmss}.jpg", img.Name);
        Assert.Matches(@"^图片_\d{8}-\d{6}\.jpg$", img.Name);
        Assert.Equal($"视频_{Clock.Now:yyyyMMdd-HHmmss}.mp4", (await Up(u, Mp4, "v.mp4")).Name);
        Assert.Equal($"打卡照片_{Clock.Now:yyyyMMdd-HHmmss}.jpg", (await Up(u, Jpg, "p.jpg", "PunchPhoto")).Name);
        Assert.Equal($"建单视频_{Clock.Now:yyyyMMdd-HHmmss}.mp4", (await Up(u, Mp4, "p.mp4", "OrderPhoto")).Name);
        Assert.Equal($"文件_{Clock.Now:yyyyMMdd-HHmmss}.pdf", (await Up(u, Pdf, "x.pdf")).Name);

        // 以内容为准：扩展名缺失或不对时用规范扩展名（小程序临时文件）
        var noExt = await Up(u, Png, "tmp_noext", "Avatar", contentType: "application/octet-stream");
        Assert.Equal("png", noExt.Ext);
        Assert.Equal("image/png", noExt.MimeType);
        var wrong = await Up(u, Png, "a.jpg", contentType: "image/jpeg");
        Assert.Equal("png", wrong.Ext);
        Assert.Equal("image/png", wrong.MimeType); // 不信任客户端声明的类型
        Assert.Equal("video/quicktime", (await Up(u, Mp4, "clip.MOV")).MimeType);
        Assert.Equal("mov", (await Up(u, Mp4, "clip.MOV")).Ext);
        Assert.Equal("jpeg", (await Up(u, Jpg, "a.JPEG")).Ext);
    }

    [Theory]
    [InlineData("a/b\\c:d*.pdf", "cd.pdf")]
    [InlineData("  报价   单 .pdf  ", "报价 单.pdf")]
    [InlineData("?.pdf", "未命名.pdf")]
    [InlineData("...", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    [InlineData("C:\\Users\\x\\桌面\\表<1>|\"2\".xlsx", "表12.xlsx")]
    [InlineData("a\tb\r\n.txt", "a b.txt")]
    [InlineData("无扩展名", "无扩展名")]
    public void CleanNameRules(string? input, string? expected) => Assert.Equal(expected, FileService.CleanName(input));

    [Fact]
    public void CleanNameTruncatesStemAndKeepsExtension()
    {
        var name = FileService.CleanName(new string('长', 150) + ".docx")!;
        Assert.Equal(100, name.Length);
        Assert.EndsWith(".docx", name);
        Assert.StartsWith("长长长", name);
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("page.html")]
    [InlineData("page.htm")]
    [InlineData("logo.svg")]
    [InlineData("pack.zip")]
    [InlineData("pack.rar")]
    [InlineData("pack.7z")]
    [InlineData("run.js")]
    [InlineData("run.bat")]
    [InlineData("run.sh")]
    [InlineData("app.apk")]
    [InlineData("data.xml")]
    [InlineData("报价单.pdf.exe")]
    [InlineData("noext")]
    public async Task AttachmentRejectsOtherTypes(string fileName)
    {
        foreach (var content in new[] { Exe, Zip("a.txt"), Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>") })
            await Fails(40000, Unsupported, () => Up(U("王敏"), content, fileName, name: fileName, contentType: "image/png"));
        Assert.Equal(0, Db.Files.Count(f => f.UploaderId == U("王敏").Id && f.OrderId == null));
    }

    [Fact]
    public async Task AttachmentRejectsDisguisedContent()
    {
        var u = U("王敏");
        await Fails(40000, Mismatch, () => Up(u, Exe, "报价单.pdf", name: "报价单.pdf", contentType: "application/pdf"));
        await Fails(40000, Mismatch, () => Up(u, Zip("a.txt", "b.exe"), "报告.docx", name: "报告.docx"));
        await Fails(40000, Mismatch, () => Up(u, Zip("a.txt"), "表.xlsx"));
        await Fails(40000, Mismatch, () => Up(u, Pdf, "稿.pptx"));
        await Fails(40000, Mismatch, () => Up(u, Encoding.ASCII.GetBytes("plain text"), "旧.doc"));
        await Fails(40000, Mismatch, () => Up(u, new byte[] { 0x61, 0x62, 0x00, 0x63 }, "说明.txt"));
        await Fails(40000, Mismatch, () => Up(u, Exe, "数据.csv"));
        await Fails(40000, Mismatch, () => Up(u, Pdf, "数据.csv"));
        await Fails(40000, Mismatch, () => Up(u, Jpg, "数据.txt"));
        await Fails(40000, Mismatch, () => Up(u, new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6 }, "坏包.docx")); // 打不开的 ZIP
        // 图片 / 视频扩展名但内容识别不出
        await Fails(40000, Mismatch, () => Up(u, Exe, "照片.jpg", contentType: "image/jpeg"));
        await Fails(40000, Mismatch, () => Up(u, Pdf, "视频.mp4", contentType: "video/mp4"));
        // UTF-16（带 BOM）的文本含 0x00 也接受
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("现场说明")).ToArray();
        Assert.Equal("text/plain", (await Up(u, utf16, "说明.txt")).MimeType);
        // PDF 头允许出现在前 1024 字节内
        Assert.Equal("application/pdf", (await Up(u, Encoding.ASCII.GetBytes("\r\n\r\n").Concat(Pdf).ToArray(), "a.pdf")).MimeType);
    }

    [Fact]
    public async Task OtherCategoriesOnlyAcceptImagesAndVideo()
    {
        var u = U("王工");
        const string msg = "只能上传图片或视频";
        foreach (var cat in new[] { "OrderPhoto", "PunchPhoto", "ProcessPhoto", "Signature", "Avatar", "FactoryReceivePhoto" })
        {
            await Fails(40000, msg, () => Up(u, Pdf, "a.pdf", cat));
            await Fails(40000, msg, () => Up(u, Ooxml(), "a.docx", cat));
            await Fails(40000, msg, () => Up(u, Txt, "a.txt", cat));
        }
        // 声明为 image/svg+xml 的 SVG：按内容识别不出，拒绝
        var svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>");
        await Fails(40000, msg, () => Up(u, svg, "a.svg", "OrderPhoto", contentType: "image/svg+xml"));
        await Fails(40000, msg, () => Up(u, svg, "a.png", "OrderPhoto", contentType: "image/png"));
        await Fails(40000, msg, () => Up(u, Exe, "a.jpg", "ProcessPhoto", contentType: "image/jpeg"));
        Assert.Equal("image/jpeg", (await Up(u, Jpg, "a.jpg", "ProcessPhoto")).MimeType);

        await Fails(40000, "请选择要上传的文件", () => Files.Upload(null, "Attachment", u));
        await Fails(40000, "请选择要上传的文件", () => Up(u, Array.Empty<byte>(), "a.pdf"));
        await Fails(40000, "文件类别不正确", () => Up(u, Pdf, "a.pdf", "Nope"));
        await Fails(40000, "文件类别不正确", () => Files.Upload(new MemFile(Pdf, "a.pdf"), null, u));
    }

    [Fact]
    public async Task SizeLimits()
    {
        var u = U("王敏");
        const long mb = 1024 * 1024;
        Assert.Equal(20 * mb, FileService.MaxBytes);
        Assert.Equal(100 * mb, FileService.MaxAttachmentBytes);
        // 用声明长度的假流：校验在落盘之前
        await Fails(40000, "图片不能超过 20MB", () => Up(u, Jpg, "a.jpg", declared: 20 * mb + 1));
        await Fails(40000, "文件不能超过 100MB", () => Up(u, Mp4, "a.mp4", declared: 100 * mb + 1));
        await Fails(40000, "文件不能超过 100MB", () => Up(u, Pdf, "a.pdf", declared: 100 * mb + 1));
        await Fails(40000, "文件不能超过 20MB", () => Up(u, Mp4, "a.mp4", "OrderPhoto", declared: 20 * mb + 1));
        await Fails(40000, "文件不能超过 20MB", () => Up(u, Jpg, "a.jpg", "ProcessPhoto", declared: 20 * mb + 1));
        // 边界内成功
        Assert.Equal(20 * mb, (await Up(u, Jpg, "a.jpg", declared: 20 * mb)).Size);
        Assert.Equal(100 * mb, (await Up(u, Mp4, "a.mp4", declared: 100 * mb)).Size);
        Assert.Equal(21 * mb, (await Up(u, Pdf, "a.pdf", declared: 21 * mb)).Size);
        Assert.Equal(20 * mb, (await Up(u, Mp4, "a.mp4", "OrderPhoto", declared: 20 * mb)).Size);
    }

    [Fact]
    public async Task EncryptedOoxmlWithOleHeaderIsAccepted()
    {
        var d = await Up(U("王敏"), Ole, "加密报告.docx", name: "加密报告.docx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", d.MimeType);
        Assert.Equal("docx", d.Ext);
        Assert.Equal(FileKind.Document, d.Kind);
    }
}
