using System.IO.Compression;
using System.Text;
using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Api.Services;

public class FileService
{
    /// <summary>图片、以及非附件类别的全部文件的单文件上限</summary>
    public const long MaxBytes = 20L * 1024 * 1024;
    /// <summary>附件里视频与文档的单文件上限（v1.2）</summary>
    public const long MaxAttachmentBytes = 100L * 1024 * 1024;

    private const string UnsupportedType = "不支持的文件类型，仅支持图片、视频、Word、Excel、PPT、PDF、TXT、CSV";
    private const string ContentMismatch = "文件内容与扩展名不符，请确认文件未损坏";
    private const string MediaOnly = "只能上传图片或视频";

    /// <summary>文档扩展名 → 存储 MIME（仅 category=Attachment 允许）</summary>
    private static readonly Dictionary<string, string> DocMime = new(StringComparer.Ordinal)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
    };

    /// <summary>图片 MIME → 允许保留的扩展名（第一个为规范扩展名）</summary>
    private static readonly Dictionary<string, string[]> ImageExts = new(StringComparer.Ordinal)
    {
        ["image/jpeg"] = new[] { ".jpg", ".jpeg" },
        ["image/png"] = new[] { ".png" },
        ["image/gif"] = new[] { ".gif" },
        ["image/bmp"] = new[] { ".bmp" },
        ["image/webp"] = new[] { ".webp" },
        ["image/heic"] = new[] { ".heic", ".heif" },
    };

    private static readonly Dictionary<string, string> VideoMime = new(StringComparer.Ordinal)
    {
        [".mp4"] = "video/mp4", [".m4v"] = "video/x-m4v", [".mov"] = "video/quicktime", [".3gp"] = "video/3gpp",
    };

    /// <summary>所有允许落盘的扩展名 → 输出用的 MIME（/uploads 静态输出按它映射，保证每种存下来的文件都取得到）</summary>
    public static IReadOnlyDictionary<string, string> StoredTypes { get; } =
        DocMime.Concat(VideoMime).Concat(ImageExts.SelectMany(kv => kv.Value.Select(e => new KeyValuePair<string, string>(e, kv.Key))))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> MediaExts =
        ImageExts.Values.SelectMany(x => x).Concat(VideoMime.Keys).ToHashSet(StringComparer.Ordinal);

    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly IWebHostEnvironment _env;
    private readonly IHttpContextAccessor _http;
    private readonly AppSettings _settings;

    public FileService(AppDbContext db, IClock clock, IWebHostEnvironment env, IHttpContextAccessor http, IOptions<AppSettings> settings)
    {
        _db = db;
        _clock = clock;
        _env = env;
        _http = http;
        _settings = settings.Value;
    }

    public string UploadRoot => Path.Combine(_env.ContentRootPath, "uploads");

    public string BaseUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_settings.PublicBaseUrl)) return _settings.PublicBaseUrl.TrimEnd('/');
            var req = _http.HttpContext?.Request;
            return req == null ? "" : $"{req.Scheme}://{req.Host}";
        }
    }

    public string Url(StoredFile f) => $"{BaseUrl}/uploads/{f.Path}";

    public string FullPath(StoredFile f) => Path.Combine(UploadRoot, f.Path.Replace('/', Path.DirectorySeparatorChar));

    // ---------- 展示字段（v1.2） ----------

    public static FileKind KindOf(string? mime)
    {
        var m = mime ?? "";
        if (m.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return FileKind.Image;
        if (m.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return FileKind.Video;
        return FileKind.Document;
    }

    public static FileKind KindOf(StoredFile f) => KindOf(f.MimeType);

    /// <summary>存储路径的扩展名（含点、小写）；没有则空串</summary>
    private static string PathExt(StoredFile f) => Path.GetExtension(f.Path ?? "").ToLowerInvariant();

    /// <summary>展示名与下载名：DisplayName 优先，否则 {前缀}_{yyyyMMdd-HHmmss}{扩展名}</summary>
    public static string NameOf(StoredFile f)
    {
        if (!string.IsNullOrEmpty(f.DisplayName)) return f.DisplayName;
        var kind = KindOf(f);
        var prefix = f.Category switch
        {
            FileCategory.Attachment => kind switch { FileKind.Image => "图片", FileKind.Video => "视频", _ => "文件" },
            FileCategory.OrderPhoto => kind == FileKind.Video ? "建单视频" : "建单照片",
            FileCategory.PunchPhoto => "打卡照片",
            FileCategory.ProcessPhoto => "完工照片",
            FileCategory.Signature => "签字件",
            FileCategory.FactoryReceivePhoto => "收货照片",
            FileCategory.FactoryShipPhoto => "寄回照片",
            FileCategory.FactorySignPhoto => "签收凭证",
            FileCategory.Avatar => "头像",
            _ => "文件"
        };
        return $"{prefix}_{f.CreatedAt:yyyyMMdd-HHmmss}{PathExt(f)}";
    }

    public T Fill<T>(T dto, StoredFile f) where T : FileDto
    {
        dto.Id = f.Id;
        dto.Url = Url(f);
        dto.Category = f.Category;
        dto.MimeType = f.MimeType;
        dto.CreatedAt = f.CreatedAt;
        dto.Name = NameOf(f);
        dto.Size = f.Size;
        dto.Kind = KindOf(f);
        dto.Ext = PathExt(f).TrimStart('.');
        return dto;
    }

    public FileDto ToDto(StoredFile f) => Fill(new FileDto(), f);

    // ---------- 类型识别 ----------

    private static bool At(ReadOnlySpan<byte> h, int i, params byte[] b) => h.Length >= i + b.Length && h.Slice(i, b.Length).SequenceEqual(b);
    private static bool At(ReadOnlySpan<byte> h, int i, string ascii) => At(h, i, Encoding.ASCII.GetBytes(ascii));

    /// <summary>按文件头识别常见图片/视频格式，识别不了返回 null。</summary>
    public static string? SniffMime(IFormFile file) => SniffMime(ReadHead(file, 16));

    public static string? SniffMime(byte[] h)
    {
        ReadOnlySpan<byte> s = h;
        if (At(s, 0, 0xFF, 0xD8, 0xFF)) return "image/jpeg";
        if (At(s, 0, 0x89, 0x50, 0x4E, 0x47)) return "image/png";
        if (At(s, 0, 0x47, 0x49, 0x46, 0x38)) return "image/gif";
        // BMP：除「BM」外还要求偏移 6–9 的保留字段为 0，避免把以「BM」开头的文本（如 CSV 表头）误判成图片
        if (At(s, 0, 0x42, 0x4D) && (s.Length < 10 || At(s, 6, 0, 0, 0, 0))) return "image/bmp";
        if (At(s, 0, "RIFF") && At(s, 8, "WEBP")) return "image/webp";
        // ISO-BMFF / QuickTime：前 4 字节是大端的 box 长度。合法文件（≤100MB，或长度写 0 / 1）首字节不超过 0x08；
        // 文本的前 4 字节都是可见字符，借此避免把「The free …」「xxx skip …」这类文本误判成视频
        if (s.Length < 1 || s[0] > 0x08) return null;
        if (At(s, 4, "ftyp")) // HEIC / MP4 / MOV
        {
            // AVIF 不支持（没有可用的预览 / 播放），不能落到下面当成 mp4
            if (At(s, 8, "avif") || At(s, 8, "avis")) return null;
            foreach (var brand in new[] { "heic", "heix", "mif1", "hevc", "heim", "heis", "msf1" })
                if (At(s, 8, brand)) return "image/heic";
            if (At(s, 8, "qt  ")) return "video/quicktime";
            return "video/mp4";
        }
        // QuickTime：不以 ftyp 开头的 atom
        foreach (var atom in new[] { "moov", "mdat", "wide", "free", "skip" })
            if (At(s, 4, atom)) return "video/quicktime";
        return null;
    }

    private static byte[] ReadHead(IFormFile file, int max)
    {
        var buf = new byte[max];
        var n = 0;
        using var st = file.OpenReadStream();
        while (n < max)
        {
            var r = st.Read(buf, n, max - n);
            if (r <= 0) break;
            n += r;
        }
        return buf[..n];
    }

    /// <summary>取最后一个扩展名（含点、小写）；没有则空串。只看最后一个路径段。</summary>
    public static string ExtOf(string? fileName)
    {
        var s = LastSegment(fileName).Trim();
        var i = s.LastIndexOf('.');
        return i < 0 || i == s.Length - 1 ? "" : s[i..].ToLowerInvariant();
    }

    private static string LastSegment(string? fileName)
    {
        var s = fileName ?? "";
        var i = s.LastIndexOfAny(new[] { '/', '\\' });
        return i < 0 ? s : s[(i + 1)..];
    }

    /// <summary>文件名清洗（CHANGES-v1.2 §3.3）：fileName → DisplayName；结果为空返回 null</summary>
    public static string? CleanName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var sb = new StringBuilder();
        var lastSpace = false;
        foreach (var c in LastSegment(fileName))
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            if (char.IsControl(c) || "<>:\"/\\|?*".IndexOf(c) >= 0) continue;
            // 不可见的格式控制符（零宽字符、RLO 等方向覆盖符）会让文件名「看起来」是另一个扩展名，一律去掉
            if (char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator) continue;
            sb.Append(c);
            lastSpace = false;
        }
        var s = sb.ToString().Trim(' ', '.');
        // 去掉非法字符后可能又出现相邻空格（如「a ? b」）
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        if (s.Length == 0) return null;
        // 主名为空（如「?.pdf」清洗后只剩扩展名）时主名用「未命名」：以清洗前是否带扩展名为准
        var rawExt = ExtOf(fileName);
        string stem, ext;
        var dot = s.LastIndexOf('.');
        if (dot > 0)
        {
            stem = s[..dot].TrimEnd(' ', '.');
            ext = s[dot..];
        }
        else if (rawExt.Length > 0 && string.Equals("." + s, rawExt, StringComparison.OrdinalIgnoreCase))
        {
            stem = "";
            ext = "." + s;
        }
        else
        {
            stem = s;
            ext = "";
        }
        if (stem.Length == 0) stem = "未命名";
        if (ext.Length > 20) { stem = (stem + ext); ext = ""; }
        if (stem.Length + ext.Length > 100)
        {
            var cut = Math.Max(1, 100 - ext.Length);
            if (char.IsHighSurrogate(stem[cut - 1])) cut--; // 不把代理对（emoji 等）截成半个
            stem = stem[..Math.Max(cut, 0)].TrimEnd(' ', '.');
            if (stem.Length == 0) stem = "未命名";
        }
        var result = stem + ext;
        return result.Length == 0 ? null : result;
    }

    private record Detected(string Mime, string Ext);

    /// <summary>图片 / 视频：以内容为准；扩展名缺失或不对时用规范扩展名</summary>
    private static Detected? DetectMedia(byte[] head, string ext)
    {
        var mime = SniffMime(head);
        if (mime == null) return null;
        if (mime.StartsWith("image/"))
        {
            var allowed = ImageExts[mime];
            return new Detected(mime, allowed.Contains(ext) ? ext : allowed[0]);
        }
        if (VideoMime.TryGetValue(ext, out var byExt)) return new Detected(byExt, ext);
        return mime == "video/quicktime" ? new Detected("video/quicktime", ".mov") : new Detected("video/mp4", ".mp4");
    }

    private static bool IsOle(ReadOnlySpan<byte> h) => At(h, 0, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1);
    private static bool IsZip(ReadOnlySpan<byte> h) => At(h, 0, 0x50, 0x4B, 0x03, 0x04);

    private static bool IsKnownBinary(byte[] head)
    {
        ReadOnlySpan<byte> h = head;
        return SniffMime(head) != null || At(h, 0, "%PDF-") || IsZip(h) || IsOle(h) || At(h, 0, "MZ") || At(h, 0, 0x7F, 0x45, 0x4C, 0x46)
               || At(h, 0, 0x1F, 0x8B) || At(h, 0, "Rar!") || At(h, 0, 0x37, 0x7A, 0xBC, 0xAF);
    }

    /// <summary>文档内容校验（CHANGES-v1.2 §3.2）</summary>
    private static bool DocContentOk(string ext, byte[] head, IFormFile file)
    {
        ReadOnlySpan<byte> h = head;
        switch (ext)
        {
            case ".pdf":
                return h[..Math.Min(h.Length, 1024)].IndexOf("%PDF-"u8) >= 0;
            case ".txt":
            case ".csv":
                if (IsKnownBinary(head)) return false;
                if (At(h, 0, 0xFF, 0xFE) || At(h, 0, 0xFE, 0xFF)) return true; // UTF-16 BOM
                return h.IndexOf((byte)0) < 0;
            default: // Office 六种扩展名共用：OLE 复合文档，或含 [Content_Types].xml 的 ZIP
                if (IsOle(h)) return true;
                if (!IsZip(h)) return false;
                try
                {
                    using var st = file.OpenReadStream();
                    using var zip = new ZipArchive(st, ZipArchiveMode.Read, leaveOpen: false);
                    return zip.Entries.Any(e => e.FullName == "[Content_Types].xml");
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
                {
                    return false;
                }
        }
    }

    public async Task<FileDto> Upload(IFormFile? file, string? category, CurrentUser user, string? fileName = null)
    {
        // 1. 文件 / 2. 类别
        if (file == null || file.Length == 0) throw BizException.BadRequest("请选择要上传的文件");
        // Enum.TryParse 也接受数字（"8"），这里只认枚举名
        if (string.IsNullOrWhiteSpace(category) || int.TryParse(category, out _)
            || !Enum.TryParse<FileCategory>(category, false, out var cat) || !Enum.IsDefined(cat))
            throw BizException.BadRequest("文件类别不正确");
        var isAttachment = cat == FileCategory.Attachment;

        // 3. 扩展名：fileName 优先，否则 multipart 文件名
        var named = !string.IsNullOrWhiteSpace(fileName);
        var ext = ExtOf(named ? fileName : file.FileName);

        // 4. 类型：扩展名 + 内容，两者都要过；不再信任客户端的 Content-Type
        var head = ReadHead(file, 8192);
        Detected type;
        if (DocMime.TryGetValue(ext, out var docMime))
        {
            if (!isAttachment) throw BizException.BadRequest(MediaOnly);
            if (!DocContentOk(ext, head, file)) throw BizException.BadRequest(ContentMismatch);
            type = new Detected(docMime, ext);
        }
        else
        {
            type = DetectMedia(head, ext)
                   ?? throw BizException.BadRequest(!isAttachment ? MediaOnly : MediaExts.Contains(ext) ? ContentMismatch : UnsupportedType);
        }

        // 5. 大小
        if (!isAttachment)
        {
            if (file.Length > MaxBytes) throw BizException.BadRequest("文件不能超过 20MB");
        }
        else if (KindOf(type.Mime) == FileKind.Image)
        {
            if (file.Length > MaxBytes) throw BizException.BadRequest("图片不能超过 20MB");
        }
        else if (file.Length > MaxAttachmentBytes) throw BizException.BadRequest("文件不能超过 100MB");

        // 展示名：清洗后的 fileName；图片 / 视频的名字若带着别的扩展名（内容与名字不一致），补上规范扩展名，避免下载名误导
        var display = named ? CleanName(fileName) : null;
        if (display != null && !DocMime.ContainsKey(ext) && ExtOf(display) != type.Ext && !SameMediaFamily(ExtOf(display), type))
            display = CleanName(display + type.Ext);

        var now = _clock.Now;
        var rel = $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{type.Ext}";
        var full = Path.Combine(UploadRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var fs = File.Create(full))
            await file.CopyToAsync(fs);

        var entity = new StoredFile
        {
            Path = rel, OriginalName = Bounded(Path.GetFileName(file.FileName ?? ""), 200), Category = cat, MimeType = type.Mime,
            Size = file.Length, UploaderId = user.Id, CreatedAt = now, DisplayName = display
        };
        _db.Files.Add(entity);
        await _db.SaveChangesAsync();
        return ToDto(entity);
    }

    /// <summary>按列长截断（不截断代理对）；OriginalName 只用于检索，不展示</summary>
    private static string Bounded(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }

    /// <summary>名字的扩展名与识别出的类型是否属于同一格式（.jpeg 之于 image/jpeg、.mov 之于视频等）</summary>
    private static bool SameMediaFamily(string nameExt, Detected type) =>
        ImageExts.TryGetValue(type.Mime, out var img) ? img.Contains(nameExt) : VideoMime.ContainsKey(nameExt);

    // ---------- 下载 ----------

    public record DownloadInfo(string FullPath, string MimeType, string Name);

    /// <summary>下载鉴权（CHANGES-v1.2 §3.5）：返回磁盘路径 / MIME / 下载名，控制器只做输出</summary>
    public async Task<DownloadInfo> ResolveDownload(int id, CurrentUser user)
    {
        // 已软删除的文件被全局过滤器排除
        var f = await _db.Files.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (f == null || f.Category == FileCategory.Avatar) throw BizException.NotFound("文件不存在");
        if (f.OrderId == null)
        {
            if (f.UploaderId != user.Id) throw BizException.NotFound("文件不存在");
        }
        else
        {
            // v1.3：查看权限多了「已退出」与「待确认转交的接手人」，需要 Exits
            var o = await _db.Orders.AsNoTracking().Include(x => x.Assistants).Include(x => x.Exits).FirstOrDefaultAsync(x => x.Id == f.OrderId)
                    ?? throw BizException.NotFound("文件不存在");
            OrderViewBuilder.EnsureCanView(o, user);
            if (!AttachmentRules.Visible(f, user)) throw BizException.NotFound("文件不存在");
        }
        var full = FullPath(f);
        if (!File.Exists(full)) throw BizException.NotFound("文件已丢失，请联系管理员");
        return new DownloadInfo(full, SafeMime(f.MimeType), NameOf(f));
    }

    /// <summary>
    /// 输出用的 Content-Type：v1.1 及更早的行存的是客户端声明的类型，可能不是合法的 media type（会让响应头写入失败）。
    /// 不合法一律按二进制流输出。
    /// </summary>
    public static string SafeMime(string? mime)
    {
        var m = (mime ?? "").Trim();
        return m.Length is > 0 and <= 100 && System.Text.RegularExpressions.Regex.IsMatch(m, @"^[A-Za-z0-9][A-Za-z0-9.+-]*/[A-Za-z0-9][A-Za-z0-9.+-]*$")
            ? m : "application/octet-stream";
    }

    // ---------- 静态地址 /uploads 的收紧（v1.2 审查补充） ----------

    /// <summary>
    /// /uploads 静态地址是否应当拒绝（404）。只针对「文档」扩展名：文档的打开 / 下载一律走鉴权下载接口，
    /// 所以受限或已删除的文档不再能通过静态地址取到（即使有人留着旧链接）。
    /// 图片 / 视频的静态地址保持匿名可访问（预览、缩略图以及 v1.1 小程序都依赖它）。
    /// relPath 为 uploads 下的相对路径（yyyy/MM/xxx.ext）。
    /// </summary>
    public async Task<bool> StaticBlocked(string? relPath)
    {
        var rel = (relPath ?? "").TrimStart('/');
        if (!DocMime.ContainsKey(Path.GetExtension(rel).ToLowerInvariant())) return false;
        return await _db.Files.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(f => f.Path == rel && (f.DeletedAt != null || (f.Restricted && f.OrderId != null)));
    }

    // ---------- 挂载 ----------

    private static bool IsCompletion(FileCategory c) => c is FileCategory.ProcessPhoto or FileCategory.Signature;

    /// <summary>
    /// 把已上传文件挂到工单上：必须存在（未删除）、为本人上传（已挂在本单同类资料下的除外）、未挂到其他工单、
    /// 未用于本单其他资料；非附件类别只能是图片 / 视频。类别统一修正为 category。
    /// </summary>
    public async Task<List<StoredFile>> Attach(IEnumerable<int>? ids, int orderId, FileCategory category, CurrentUser user, string label)
    {
        var list = (ids ?? Enumerable.Empty<int>()).Distinct().ToList();
        if (list.Count == 0) return new();
        var files = _db.Files.Where(f => list.Contains(f.Id)).ToList();
        if (files.Count != list.Count) throw BizException.BadRequest($"{label}不存在，请重新上传");
        foreach (var f in files)
        {
            // 头像文件不能再挂到工单上（否则同一个文件既是头像又是工单资料）
            if (f.Category == FileCategory.Avatar) throw BizException.BadRequest($"{label}不存在，请重新上传");
            if (category != FileCategory.Attachment && KindOf(f) == FileKind.Document)
                throw BizException.BadRequest($"{label}只能是图片或视频");
            // 已挂在本单同类资料下的文件（如退回/改派后保留的上一次完工照片）不再校验上传人
            var alreadyOnOrder = f.OrderId == orderId && (f.Category == category || (IsCompletion(f.Category) && IsCompletion(category)));
            if (!alreadyOnOrder && f.UploaderId != user.Id) throw BizException.BadRequest($"{label}不是本人上传，请重新上传");
            if (f.OrderId != null && f.OrderId != orderId) throw BizException.BadRequest($"{label}已被其他工单使用，请重新上传");
            // 已挂在本单其他资料下的文件不能挪用（附件不能被挪作完工照片，反之亦然）
            if (f.OrderId == orderId && !alreadyOnOrder) throw BizException.BadRequest($"{label}已用于本单其他资料，请重新上传");
            f.OrderId = orderId;
            f.Category = category;
            if (category != FileCategory.Attachment) f.Restricted = false;
        }
        await Task.CompletedTask;
        return files;
    }
}
