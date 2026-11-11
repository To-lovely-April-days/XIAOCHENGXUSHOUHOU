using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

public class FileCenterQuery
{
    public string? Keyword { get; set; }
    /// <summary>Image | Video | Document</summary>
    public string? Type { get; set; }
    /// <summary>Attachment | OrderPhoto | ProcessPhoto | Signature | PunchPhoto | Factory</summary>
    public string? Source { get; set; }
    public int? UploaderId { get; set; }
    public int? OrderId { get; set; }
    /// <summary>true / false</summary>
    public string? Restricted { get; set; }
    /// <summary>v1.3 附件类别：Sat | RepairForm | Other；给了就只返回该类别的附件</summary>
    public string? AttachmentType { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 30;
}

/// <summary>
/// v1.2 工单文件中心（CHANGES-v1.2 §5）：所有挂在工单上的文件（附件、建单照片、完工照片、签字件、打卡照片、返厂照片）的统一查询。
/// 本期控制器只对 Supervisor 开放，但查询按用户处理可见性：Engineer 只含其能查看的工单的文件，且不含受限附件。
/// 「必须挂工单」只在这里限定（为以后的公司资料库留口子）。
/// </summary>
public class FileCenterService
{
    private readonly AppDbContext _db;
    private readonly OrderViewBuilder _view;

    public FileCenterService(AppDbContext db, OrderViewBuilder view)
    {
        _db = db;
        _view = view;
    }

    private class Row
    {
        public StoredFile File { get; set; } = null!;
        public WorkOrder Order { get; set; } = null!;
    }

    public async Task<FileCenterPage> Query(FileCenterQuery p, CurrentUser user)
    {
        var type = OrderService.ParseEnum<FileKind>(p.Type, "文件类型");
        string? source = null;
        if (!string.IsNullOrWhiteSpace(p.Source))
            source = AttachmentRules.Sources.FirstOrDefault(s => s.Equals(p.Source.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? throw BizException.BadRequest("文件来源不正确");
        bool? restricted = null;
        if (!string.IsNullOrWhiteSpace(p.Restricted))
        {
            if (!bool.TryParse(p.Restricted.Trim(), out var rv)) throw BizException.BadRequest("可见范围筛选不正确");
            restricted = rv;
        }
        if (!AttachmentRules.TryParseType(p.AttachmentType, out var attType)) throw BizException.BadRequest("附件类别不正确");
        var from = OrderService.ParseDate(p.From, "开始日期");
        var to = OrderService.ParseDate(p.To, "结束日期");
        if (from != null && to != null && from > to) throw BizException.BadRequest("开始日期不能晚于结束日期");
        var page = Math.Max(1, p.Page);
        var size = Math.Clamp(p.PageSize, 1, 200);

        // 范围：挂在工单上（含已作废工单）、未删除（全局过滤器）、非头像
        var q = from f in _db.Files
            join o in _db.Orders on f.OrderId equals o.Id
            where f.Category != FileCategory.Avatar
            select new Row { File = f, Order = o };

        // 可见性：Engineer 只看能查看的工单（待认领、本人负责 / 协助 / 建单；v1.3：本人已退出、待本人确认转交），且不含受限附件
        if (user.Role == Role.Engineer)
        {
            var uid = user.Id;
            q = q.Where(x => !x.File.Restricted
                             && (x.Order.Status == OrderStatus.Pending || x.Order.AssigneeId == uid || x.Order.CreatorId == uid
                                 || x.Order.Assistants.Any(a => a.EngineerId == uid)
                                 || x.Order.Exits.Any(e => e.EngineerId == uid) || x.Order.PendingTransferToId == uid));
        }

        if (!string.IsNullOrWhiteSpace(p.Keyword))
        {
            var k = p.Keyword.Trim();
            q = q.Where(x => (x.File.DisplayName != null && x.File.DisplayName.Contains(k)) || x.File.OriginalName.Contains(k)
                             || x.Order.OrderNo.Contains(k) || x.Order.CustomerName.Contains(k)
                             || (x.Order.ProjectNo != null && x.Order.ProjectNo.Contains(k)));
        }
        if (source != null)
        {
            var cats = AttachmentRules.CategoriesOf(source);
            q = q.Where(x => cats.Contains(x.File.Category));
        }
        if (attType != null)
        {
            // 只返回附件（建单照片 / 新附件）且类别等于该值的行；与 source 同时给时取交集
            var at = attType.Value;
            q = q.Where(x => (x.File.Category == FileCategory.OrderPhoto || x.File.Category == FileCategory.Attachment)
                             && x.File.AttachmentType == at);
        }
        if (p.UploaderId != null) q = q.Where(x => x.File.UploaderId == p.UploaderId);
        if (p.OrderId != null) q = q.Where(x => x.File.OrderId == p.OrderId);
        if (restricted != null) q = q.Where(x => x.File.Restricted == restricted);
        if (from != null) { var f0 = from.Value.ToDateTime(TimeOnly.MinValue); q = q.Where(x => x.File.CreatedAt >= f0); }
        if (to != null) { var t0 = to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(x => x.File.CreatedAt < t0); }

        // counts：受除 type 以外的全部筛选影响
        var byMime = await q.GroupBy(x => x.File.MimeType).Select(g => new { Mime = g.Key, Count = g.Count() }).ToListAsync();
        var counts = new Dictionary<string, int> { ["all"] = byMime.Sum(x => x.Count) };
        foreach (var kind in Enum.GetValues<FileKind>())
            counts[kind.ToString()] = byMime.Where(x => FileService.KindOf(x.Mime) == kind).Sum(x => x.Count);

        if (type == FileKind.Image) q = q.Where(x => x.File.MimeType.StartsWith("image/"));
        else if (type == FileKind.Video) q = q.Where(x => x.File.MimeType.StartsWith("video/"));
        else if (type == FileKind.Document) q = q.Where(x => !x.File.MimeType.StartsWith("image/") && !x.File.MimeType.StartsWith("video/"));

        var total = await q.CountAsync();
        var totalSize = await q.SumAsync(x => (long?)x.File.Size) ?? 0;

        var files = await q.OrderByDescending(x => x.File.CreatedAt).ThenByDescending(x => x.File.Id)
            .Skip((page - 1) * size).Take(size).Select(x => x.File).AsNoTracking().ToListAsync();
        var orderIds = files.Select(f => f.OrderId!.Value).Distinct().ToList();
        // Exits：canDelete / canChangeType 要判断「已退出」
        var orders = await _db.Orders.AsNoTracking().Include(o => o.Assistants).Include(o => o.Exits)
            .Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
        var uploaders = await _view.UploaderMap(files.Select(f => f.UploaderId));

        var items = files.Select(f =>
        {
            var o = orders[f.OrderId!.Value];
            var item = _view.FillAttachment(new FileCenterItem(), f, o, uploaders.GetValueOrDefault(f.UploaderId), user);
            item.OrderId = o.Id;
            item.OrderNo = o.OrderNo;
            item.CustomerName = o.CustomerName;
            item.OrderStatus = o.Status;
            return item;
        }).ToList();

        return new FileCenterPage { Items = items, Total = total, TotalSize = totalSize, Page = page, PageSize = size, Counts = counts };
    }
}
