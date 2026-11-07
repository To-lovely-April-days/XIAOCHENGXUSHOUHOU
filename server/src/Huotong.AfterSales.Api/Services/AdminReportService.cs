using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

public class AdminPunchQuery
{
    public string? From { get; set; }
    public string? To { get; set; }
    public int? EngineerId { get; set; }
    public string? Abnormal { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>PC 主管后台：打卡记录、工时统计（API.md §9）。配对与缺卡判定复用 PunchCalculator / HoursService。</summary>
public class AdminReportService
{
    private static readonly string[] AbnormalKinds = { "distance", "supplement", "missing" };

    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly OrderViewBuilder _view;
    private readonly HoursService _hours;

    public AdminReportService(AppDbContext db, IClock clock, OrderViewBuilder view, HoursService hours)
    {
        _db = db;
        _clock = clock;
        _view = view;
        _hours = hours;
    }

    public async Task<AdminPunchPageDto> Punches(AdminPunchQuery q)
    {
        var today = _clock.Now.Date;
        var to = OrderService.ParseDate(q.To, "结束日期") ?? DateOnly.FromDateTime(today);
        var from = OrderService.ParseDate(q.From, "开始日期") ?? to.AddDays(-6);
        if (from > to) throw BizException.BadRequest("开始日期不能晚于结束日期");
        var abnormal = string.IsNullOrWhiteSpace(q.Abnormal) ? null : q.Abnormal.Trim().ToLowerInvariant();
        if (abnormal != null && !AbnormalKinds.Contains(abnormal))
            throw BizException.BadRequest("异常类型应为 distance、supplement 或 missing");
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);

        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var baseQ = _db.Punches.Where(p => p.Time >= start && p.Time < end);
        if (q.EngineerId != null) baseQ = baseQ.Where(p => p.EngineerId == q.EngineerId);

        // 配对只在同一天内发生（PunchCalculator.Pair），按日期范围取数即可得到与全量一致的配对结果
        var infos = await baseQ
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement))
            .ToListAsync();
        var pairs = PunchCalculator.Pair(infos);
        var missingIds = pairs.Where(p => p.IsMissing(today)).Select(p => p.Arrive.Id).ToHashSet();
        var pairMin = PunchCalculator.LeavePairMinutes(pairs);

        bool Match(PunchInfo p, string? kind) => kind switch
        {
            "distance" => p.OutOfRange,
            "supplement" => p.IsSupplement,
            "missing" => missingIds.Contains(p.Id),
            _ => true
        };

        var counts = new Dictionary<string, int>
        {
            ["all"] = infos.Count,
            ["distance"] = infos.Count(p => Match(p, "distance")),
            ["supplement"] = infos.Count(p => Match(p, "supplement")),
            ["missing"] = infos.Count(p => Match(p, "missing"))
        };

        var matched = infos.Where(p => Match(p, abnormal))
            .OrderByDescending(p => p.Time).ThenByDescending(p => p.Id).ToList();
        var pageIds = matched.Skip((page - 1) * size).Take(size).Select(p => p.Id).ToList();

        var full = await _view.PunchesFull().Where(p => pageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var orderIds = full.Values.Select(p => p.OrderId).Distinct().ToList();
        var orders = await _db.Orders.Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.OrderNo, o.CustomerName }).ToDictionaryAsync(o => o.Id);

        var items = pageIds.Select(id =>
        {
            var p = full[id];
            var dto = new AdminPunchItemDto();
            Copy(_view.PunchToDto(p, pairMin.TryGetValue(id, out var m) ? m : null), dto);
            var o = orders.GetValueOrDefault(p.OrderId);
            dto.OrderNo = o?.OrderNo ?? "";
            dto.CustomerName = o?.CustomerName ?? "";
            dto.Missing = missingIds.Contains(id);
            return dto;
        }).ToList();

        return new AdminPunchPageDto { Items = items, Total = matched.Count, Page = page, PageSize = size, Counts = counts };
    }

    private static void Copy(PunchDto s, AdminPunchItemDto d)
    {
        d.Id = s.Id; d.OrderId = s.OrderId; d.Engineer = s.Engineer; d.Kind = s.Kind; d.Time = s.Time;
        d.Latitude = s.Latitude; d.Longitude = s.Longitude; d.Address = s.Address;
        d.DistanceMeters = s.DistanceMeters; d.OutOfRange = s.OutOfRange; d.Photo = s.Photo;
        d.IsSupplement = s.IsSupplement; d.SupplementBy = s.SupplementBy; d.SupplementReason = s.SupplementReason;
        d.SupplementAt = s.SupplementAt; d.PairMinutes = s.PairMinutes;
    }

    public async Task<AdminHoursDto> Hours(int year, int month)
    {
        if (year < 2000 || year > 2100 || month < 1 || month > 12) throw BizException.BadRequest("年月不正确");
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);

        // 启用的工程师 + 当月有打卡的人（含已停用/已改角色，保证合计与工时导出一致）
        var punchedIds = await _db.Punches.Where(p => p.Time >= start && p.Time < end)
            .Select(p => p.EngineerId).Distinct().ToListAsync();
        var people = await _db.Employees.Include(e => e.AvatarFile)
            .Where(e => (e.Role == Role.Engineer && e.Enabled) || punchedIds.Contains(e.Id))
            .OrderBy(e => e.Id).ToListAsync();

        var rows = new List<AdminHoursEngineerDto>();
        foreach (var e in people)
        {
            var m = await _hours.Month(e.Id, year, month);
            var entries = m.Days.SelectMany(d => d.Entries).ToList();
            rows.Add(new AdminHoursEngineerDto
            {
                Engineer = _view.Brief(e),
                TotalMinutes = m.TotalMinutes,
                TripDays = m.TripDays,
                OrderCount = entries.Select(x => x.OrderId).Distinct().Count(),
                SupplementCount = entries.Count(x => x.IsSupplement),
                MissingCount = entries.Count(x => x.Missing),
                RunningNow = m.Days.Any(d => d.Running),
                Days = m.Days.OrderBy(d => d.Date).Select(d => new AdminHoursDayDto { Date = d.Date, Minutes = d.Minutes }).ToList()
            });
        }

        var ordered = rows.OrderByDescending(r => r.TotalMinutes).ThenBy(r => r.Engineer.Id).ToList();
        return new AdminHoursDto { Year = year, Month = month, TotalMinutes = ordered.Sum(r => r.TotalMinutes), Engineers = ordered };
    }
}
