using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Api.Services;

public class FileService
{
    public const long MaxBytes = 20L * 1024 * 1024;

    private static readonly Dictionary<string, string> ExtMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".heic"] = "image/heic", [".bmp"] = "image/bmp",
        [".mp4"] = "video/mp4", [".mov"] = "video/quicktime", [".m4v"] = "video/x-m4v", [".3gp"] = "video/3gpp",
    };

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

    public FileDto ToDto(StoredFile f) => new()
    {
        Id = f.Id, Url = Url(f), Category = f.Category, MimeType = f.MimeType, CreatedAt = f.CreatedAt
    };

    public async Task<FileDto> Upload(IFormFile? file, string? category, CurrentUser user)
    {
        if (file == null || file.Length == 0) throw BizException.BadRequest("请选择要上传的文件");
        if (file.Length > MaxBytes) throw BizException.BadRequest("文件不能超过 20MB");
        if (string.IsNullOrWhiteSpace(category) || !Enum.TryParse<FileCategory>(category, false, out var cat) || !Enum.IsDefined(cat))
            throw BizException.BadRequest("文件类别不正确");

        var ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
        var mime = (file.ContentType ?? "").ToLowerInvariant();
        if (!mime.StartsWith("image/") && !mime.StartsWith("video/"))
        {
            // 小程序上传可能是 application/octet-stream，按扩展名推断
            if (!ExtMime.TryGetValue(ext, out var guessed)) throw BizException.BadRequest("只能上传图片或视频");
            mime = guessed;
        }
        if (string.IsNullOrEmpty(ext) || ext.Length > 10)
            ext = ExtMime.FirstOrDefault(kv => kv.Value == mime).Key ?? (mime.StartsWith("video/") ? ".mp4" : ".jpg");

        var now = _clock.Now;
        var rel = $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{ext}";
        var full = Path.Combine(UploadRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var fs = File.Create(full))
            await file.CopyToAsync(fs);

        var entity = new StoredFile
        {
            Path = rel, OriginalName = Path.GetFileName(file.FileName ?? ""), Category = cat, MimeType = mime,
            Size = file.Length, UploaderId = user.Id, CreatedAt = now
        };
        _db.Files.Add(entity);
        await _db.SaveChangesAsync();
        return ToDto(entity);
    }

    private static bool IsCompletion(FileCategory c) => c is FileCategory.ProcessPhoto or FileCategory.Signature;

    /// <summary>
    /// 把已上传文件挂到工单上：必须存在、为本人上传（已挂在本单同类资料下的除外）、未挂到其他工单；类别统一修正为 category。
    /// </summary>
    public async Task<List<StoredFile>> Attach(IEnumerable<int>? ids, int orderId, FileCategory category, CurrentUser user, string label)
    {
        var list = (ids ?? Enumerable.Empty<int>()).Distinct().ToList();
        if (list.Count == 0) return new();
        var files = _db.Files.Where(f => list.Contains(f.Id)).ToList();
        if (files.Count != list.Count) throw BizException.BadRequest($"{label}不存在，请重新上传");
        foreach (var f in files)
        {
            // 已挂在本单同类资料下的文件（如退回/改派后保留的上一次完工照片）不再校验上传人
            var alreadyOnOrder = f.OrderId == orderId && (f.Category == category || (IsCompletion(f.Category) && IsCompletion(category)));
            if (!alreadyOnOrder && f.UploaderId != user.Id) throw BizException.BadRequest($"{label}不是本人上传，请重新上传");
            if (f.OrderId != null && f.OrderId != orderId) throw BizException.BadRequest($"{label}已被其他工单使用，请重新上传");
            f.OrderId = orderId;
            f.Category = category;
        }
        await Task.CompletedTask;
        return files;
    }
}
