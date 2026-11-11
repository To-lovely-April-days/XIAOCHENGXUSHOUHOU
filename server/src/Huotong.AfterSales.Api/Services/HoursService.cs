using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

public class HoursService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly OrderViewBuilder _view;

    public HoursService(AppDbContext db, IClock clock, OrderViewBuilder view)
    {
        _db = db;
        _clock = clock;
        _view = view;
    }

    /// <summary>工程师当前在各工单的角色：负责人 / 协助人 / 已退出</summary>
    private async Task<Dictionary<int, ParticipantRole>> Roles(int engineerId, List<int> orderIds)
    {
        var lead = (await _db.Orders.Where(o => orderIds.Contains(o.Id) && o.AssigneeId == engineerId).Select(o => o.Id).ToListAsync()).ToHashSet();
        var assist = (await _db.OrderAssistants.Where(a => orderIds.Contains(a.OrderId) && a.EngineerId == engineerId)
            .Select(a => a.OrderId).ToListAsync()).ToHashSet();
        return orderIds.ToDictionary(id => id, id => lead.Contains(id) ? ParticipantRole.Lead
            : assist.Contains(id) ? ParticipantRole.Assistant : ParticipantRole.Former);
    }

    public async Task<TodayHoursDto> Today(int engineerId)
    {
        var now = _clock.Now;
        var today = now.Date;
        var all = await _db.Punches.Where(p => p.EngineerId == engineerId)
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement)).ToListAsync();
        var pairs = PunchCalculator.Pair(all).Where(p => p.Date == today).ToList();

        var ids = pairs.SelectMany(p => new[] { p.Arrive.Id, p.Leave?.Id ?? 0 }).Where(i => i > 0).ToList();
        var full = await _view.PunchesFull().Where(p => ids.Contains(p.Id)).ToListAsync();
        var orderIds = pairs.Select(p => p.Arrive.OrderId).Distinct().ToList();
        var orders = await _db.Orders.Where(o => orderIds.Contains(o.Id)).Select(o => new { o.Id, o.OrderNo, o.CustomerName }).ToDictionaryAsync(o => o.Id);
        var pairMin = PunchCalculator.LeavePairMinutes(pairs);
        var roles = await Roles(engineerId, orderIds);

        var running = pairs.Where(p => p.IsRunning(today)).OrderByDescending(p => p.Arrive.Time).FirstOrDefault();
        return new TodayHoursDto
        {
            Date = DateOnly.FromDateTime(today),
            ClosedMinutes = PunchCalculator.ClosedMinutesOn(pairs, today),
            Running = running != null,
            RunningSince = running?.Arrive.Time,
            ServerTime = now,
            FirstArriveTime = pairs.Select(p => (DateTime?)p.Arrive.Time).Min(),
            TripDay = PunchCalculator.TripDay(all, today),
            Entries = pairs.Select(p => new TodayEntryDto
            {
                OrderId = p.Arrive.OrderId,
                OrderNo = orders.GetValueOrDefault(p.Arrive.OrderId)?.OrderNo ?? "",
                CustomerName = orders.GetValueOrDefault(p.Arrive.OrderId)?.CustomerName ?? "",
                Arrive = _view.PunchToDto(full.First(f => f.Id == p.Arrive.Id), null),
                Leave = p.Leave == null ? null : _view.PunchToDto(full.First(f => f.Id == p.Leave.Id), pairMin[p.Leave.Id]),
                Minutes = p.Minutes,
                IsSupplement = p.IsSupplement,
                Role = roles.GetValueOrDefault(p.Arrive.OrderId, ParticipantRole.Former)
            }).ToList()
        };
    }

    public async Task<MonthHoursDto> Month(int engineerId, int year, int month)
    {
        if (year < 2000 || year > 2100 || month < 1 || month > 12) throw BizException.BadRequest("年月不正确");
        var today = _clock.Now.Date;
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);
        // 多取一天，避免边界配对丢失
        var punches = await _db.Punches.Where(p => p.EngineerId == engineerId && p.Time >= start.AddDays(-1) && p.Time < end.AddDays(1)).ToListAsync();
        var infos = punches.Select(PunchInfo.From).ToList();
        var pairs = PunchCalculator.Pair(infos).Where(p => p.Date >= start && p.Date < end).ToList();
        var orderIds = pairs.Select(p => p.Arrive.OrderId).Distinct().ToList();
        var orders = await _db.Orders.Where(o => orderIds.Contains(o.Id)).Select(o => new { o.Id, o.OrderNo, o.CustomerName }).ToDictionaryAsync(o => o.Id);
        var byId = punches.ToDictionary(p => p.Id);
        var roles = await Roles(engineerId, orderIds);

        var days = pairs.GroupBy(p => p.Date).OrderByDescending(g => g.Key).Select(g => new MonthDayDto
        {
            Date = DateOnly.FromDateTime(g.Key),
            Minutes = g.Sum(p => p.Minutes),
            Running = g.Any(p => p.IsRunning(today)),
            HasSupplement = g.Any(p => p.IsSupplement),
            Entries = g.OrderBy(p => p.Arrive.Time).Select(p => new MonthEntryDto
            {
                OrderId = p.Arrive.OrderId,
                OrderNo = orders.GetValueOrDefault(p.Arrive.OrderId)?.OrderNo ?? "",
                CustomerName = orders.GetValueOrDefault(p.Arrive.OrderId)?.CustomerName ?? "",
                ArriveTime = p.Arrive.Time,
                LeaveTime = p.Leave?.Time,
                Minutes = p.Minutes,
                IsSupplement = p.IsSupplement,
                SupplementReason = p.Leave != null && p.Leave.IsSupplement ? byId[p.Leave.Id].SupplementReason
                    : p.Arrive.IsSupplement ? byId[p.Arrive.Id].SupplementReason : null,
                Missing = p.IsMissing(today),
                Role = roles.GetValueOrDefault(p.Arrive.OrderId, ParticipantRole.Former)
            }).ToList()
        }).ToList();

        return new MonthHoursDto
        {
            Year = year, Month = month,
            TotalMinutes = pairs.Sum(p => p.Minutes),
            TripDays = PunchCalculator.TripDaysInMonth(infos, year, month),
            Days = days
        };
    }

    /// <summary>本月已配对分钟</summary>
    public async Task<(int Minutes, int TripDays)> MonthSummary(int engineerId)
    {
        var now = _clock.Now;
        var m = await Month(engineerId, now.Year, now.Month);
        return (m.TotalMinutes, m.TripDays);
    }
}
