using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

public class HomeService
{
    private static readonly OrderStatus[] Doing = { OrderStatus.Accepted, OrderStatus.InProgress, OrderStatus.OnHold };

    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly OrderViewBuilder _view;
    private readonly HoursService _hours;
    private readonly OrderService _orders;

    public HomeService(AppDbContext db, IClock clock, OrderViewBuilder view, HoursService hours, OrderService orders)
    {
        _db = db;
        _clock = clock;
        _view = view;
        _hours = hours;
        _orders = orders;
    }

    public async Task<MeDto> Me(CurrentUser user)
    {
        var me = await _view.LoadEmployee(user.Id);
        var dto = new MeDto { User = _view.User(me) };
        switch (me.Role)
        {
            case Role.Engineer:
                var (min, days) = await _hours.MonthSummary(me.Id);
                dto.MonthMinutes = min;
                dto.MonthTripDays = days;
                dto.ActiveOrderCount = await _db.Orders.CountAsync(o => o.AssigneeId == me.Id && Doing.Contains(o.Status));
                break;
            case Role.Sales:
                dto.ActiveOrderCount = await _db.Orders.CountAsync(o => o.CreatorId == me.Id && o.Status != OrderStatus.Closed && o.Status != OrderStatus.Voided);
                break;
            case Role.Supervisor:
                dto.ActiveOrderCount = await _db.Orders.CountAsync(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Submitted);
                break;
        }
        return dto;
    }

    public async Task<UserDto> SetAvatar(CurrentUser user, int fileId)
    {
        var me = await _db.Employees.FirstAsync(e => e.Id == user.Id);
        var file = await _db.Files.FirstOrDefaultAsync(f => f.Id == fileId) ?? throw BizException.BadRequest("头像文件不存在，请重新上传");
        if (file.UploaderId != user.Id) throw BizException.BadRequest("头像文件不是本人上传");
        if (!file.MimeType.StartsWith("image/")) throw BizException.BadRequest("头像必须是图片");
        file.Category = FileCategory.Avatar;
        me.AvatarFileId = file.Id;
        await _db.SaveChangesAsync();
        return _view.User(await _view.LoadEmployee(user.Id));
    }

    public async Task<EngineerHomeDto> Engineer(CurrentUser user)
    {
        var me = await _view.LoadEmployee(user.Id);
        var today = await _hours.Today(me.Id);
        var mine = await _view.Orders().Where(o => o.AssigneeId == me.Id && o.Status != OrderStatus.Voided && o.Status != OrderStatus.Pending).ToListAsync();
        var items = (await _view.BuildList(mine)).ToDictionary(i => i.Id);

        var inProgress = mine.Where(o => o.Status == OrderStatus.InProgress).Select(o => items[o.Id])
            .OrderByDescending(i => i.OnSiteSince != null)
            .ThenByDescending(i => i.LastArriveTime ?? DateTime.MinValue)
            .ThenByDescending(i => i.ClaimedAt ?? i.CreatedAt).ToList();
        var accepted = PunchService.OrderAcceptedForNext(mine.Where(o => o.Status == OrderStatus.Accepted)).ToList();

        var dto = new EngineerHomeDto { User = _view.User(me), Today = today };
        if (inProgress.Count > 0)
        {
            dto.State = "working";
            dto.Current = inProgress[0];
        }
        else if (accepted.Count > 0)
        {
            dto.State = "next";
            var n = accepted[0];
            var next = _view.FillList(new NextOrderDto(), n, (await _view.PunchMap(new[] { n.Id })).GetValueOrDefault(n.Id) ?? new());
            next.Address = n.Address;
            next.ContactName = n.ContactName;
            next.ContactPhone = n.ContactPhone;
            next.Latitude = n.Latitude;
            next.Longitude = n.Longitude;
            dto.Next = next;
        }
        else dto.State = "idle";

        dto.Upcoming = accepted.Where(o => o.Id != dto.Next?.Id && o.Id != dto.Current?.Id).Select(o => items[o.Id]).ToList();

        var hall = await _orders.Hall("all");
        dto.Hall = new HallSummaryDto { Count = hall.Counts["all"], UrgentCount = hall.Counts["urgent"], Items = hall.Items.Take(2).ToList() };

        dto.RecentDone = mine.Where(o => o.Status is OrderStatus.Submitted or OrderStatus.Closed)
            .OrderByDescending(o => o.ClosedAt ?? o.SubmittedAt ?? o.CreatedAt).Take(2).Select(o => items[o.Id]).ToList();
        dto.MonthMinutes = (await _hours.MonthSummary(me.Id)).Minutes;
        return dto;
    }

    private async Task<List<PunchInfo>> AllPunchesSince(DateTime since) =>
        await _db.Punches.Where(p => p.Time >= since)
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement)).ToListAsync();

    public async Task<SupervisorHomeDto> Supervisor(CurrentUser user)
    {
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var today = now.Date;
        var engineers = await _db.Employees.Include(e => e.AvatarFile).Where(e => e.Role == Role.Engineer && e.Enabled).OrderBy(e => e.Id).ToListAsync();
        var todayPunches = await AllPunchesSince(today);
        var todayPairs = PunchCalculator.Pair(todayPunches).Where(p => p.Date == today).ToList();
        var orderNames = await _db.Orders.Select(o => new { o.Id, o.CustomerName }).ToDictionaryAsync(o => o.Id, o => o.CustomerName);

        var dto = new SupervisorHomeDto { User = _view.User(me), Now = now };
        dto.OnSiteCount = todayPairs.Where(p => p.IsRunning(today)).Select(p => p.Arrive.EngineerId).Distinct().Count();

        var accepted = await _db.Orders.Where(o => o.Status == OrderStatus.Accepted && o.AssigneeId != null)
            .OrderByDescending(o => o.ClaimedAt).ToListAsync();
        foreach (var e in engineers)
        {
            var segs = todayPairs.Where(p => p.Arrive.EngineerId == e.Id).OrderBy(p => p.Arrive.Time).Select(p => new TimelineSegmentDto
            {
                Start = p.Arrive.Time, End = p.Leave?.Time, OrderId = p.Arrive.OrderId,
                CustomerName = orderNames.GetValueOrDefault(p.Arrive.OrderId) ?? ""
            }).ToList();
            var note = "";
            if (segs.Count == 0)
            {
                var a = accepted.FirstOrDefault(o => o.AssigneeId == e.Id);
                note = a != null ? $"已指派{a.CustomerName} · 待启动" : "今日无打卡";
            }
            dto.Timeline.Add(new TimelineRowDto { Engineer = _view.Brief(e), Segments = segs, Note = note });
        }

        // 待办
        var pending = await _db.Orders.Where(o => o.Status == OrderStatus.Pending).ToListAsync();
        foreach (var o in OrderService.HallOrder(pending.Where(o => o.Urgency == Urgency.Urgent || (now - o.CreatedAt).TotalHours > 2)))
        {
            dto.Todos.Add(new TodoDto
            {
                Type = "Unassigned", OrderId = o.Id, Title = o.CustomerName, Urgent = o.Urgency == Urgency.Urgent,
                Subtitle = $"待认领 · 已发布 {TimeText.Ago(o.CreatedAt, now)}，暂无人认领"
            });
        }
        var submitted = await _db.Orders.Include(o => o.Assignee).Where(o => o.Status == OrderStatus.Submitted).OrderBy(o => o.SubmittedAt).ToListAsync();
        foreach (var o in submitted)
        {
            var when = o.SubmittedAt == null ? "" : o.SubmittedAt.Value.Date == today ? "今天" : TimeText.Md(o.SubmittedAt.Value);
            dto.Todos.Add(new TodoDto
            {
                Type = "Submitted", OrderId = o.Id, Title = o.CustomerName, Urgent = o.Urgency == Urgency.Urgent,
                Subtitle = $"{o.Assignee?.Name} {when}提交完工，资料已上传"
            });
        }

        var active = await _db.Orders.Where(o => o.Status != OrderStatus.Voided).Select(o => o.Id).ToListAsync();
        var allPunches = await _db.Punches.Where(p => active.Contains(p.OrderId))
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement)).ToListAsync();
        var names = engineers.ToDictionary(e => e.Id, e => e.Name);
        var allEmpNames = await _db.Employees.ToDictionaryAsync(e => e.Id, e => e.Name);
        foreach (var p in PunchCalculator.Pair(allPunches).Where(p => p.IsMissing(today)).OrderBy(p => p.Arrive.Time))
        {
            dto.Todos.Add(new TodoDto
            {
                Type = "MissingPunch", OrderId = p.Arrive.OrderId,
                Title = $"{allEmpNames.GetValueOrDefault(p.Arrive.EngineerId)} · 缺离场卡",
                Subtitle = $"{TimeText.Md(p.Arrive.Time)} {orderNames.GetValueOrDefault(p.Arrive.OrderId)}，当天未计工时"
            });
        }
        var since = today.AddDays(-2);
        var far = await _db.Punches.Where(p => p.OutOfRange && p.Time >= since && active.Contains(p.OrderId)).OrderByDescending(p => p.Time).ToListAsync();
        foreach (var p in far)
        {
            dto.Todos.Add(new TodoDto
            {
                Type = "DistanceAbnormal", OrderId = p.OrderId,
                Title = $"{allEmpNames.GetValueOrDefault(p.EngineerId)} · 打卡距离异常",
                Subtitle = $"{orderNames.GetValueOrDefault(p.OrderId)} {TimeText.Smart(p.Time, now)} {EnumText.Kind(p.Kind)}，距客户 {GeoUtil.DistanceText(p.DistanceMeters ?? 0)}"
            });
        }
        return dto;
    }

    public async Task<List<EngineerLoadDto>> Engineers()
    {
        var now = _clock.Now;
        var today = now.Date;
        var engineers = await _db.Employees.Include(e => e.AvatarFile).Where(e => e.Role == Role.Engineer && e.Enabled).OrderBy(e => e.Id).ToListAsync();
        var orders = await _db.Orders.Where(o => o.AssigneeId != null &&
            (o.Status == OrderStatus.Accepted || o.Status == OrderStatus.InProgress || o.Status == OrderStatus.OnHold || o.Status == OrderStatus.Submitted))
            .Select(o => new { o.AssigneeId, o.Status }).ToListAsync();
        var todayPunches = await AllPunchesSince(today);
        var pairs = PunchCalculator.Pair(todayPunches);
        var cities = await _db.Orders.Select(o => new { o.Id, o.City }).ToDictionaryAsync(o => o.Id, o => o.City);

        return engineers.Select(e =>
        {
            var mine = orders.Where(o => o.AssigneeId == e.Id).ToList();
            var doing = mine.Count(o => o.Status is OrderStatus.InProgress or OrderStatus.OnHold);
            var acc = mine.Count(o => o.Status == OrderStatus.Accepted);
            var sub = mine.Count(o => o.Status == OrderStatus.Submitted);
            var lastArrive = todayPunches.Where(p => p.EngineerId == e.Id && p.Kind == PunchKind.Arrive).OrderByDescending(p => p.Time).FirstOrDefault();
            return new EngineerLoadDto
            {
                User = _view.Brief(e), DoingCount = doing, AcceptedCount = acc, SubmittedCount = sub,
                OnSiteNow = pairs.Any(p => p.Arrive.EngineerId == e.Id && p.IsRunning(today)),
                LoadText = LoadText(doing, acc, sub, lastArrive == null ? null : cities.GetValueOrDefault(lastArrive.OrderId) ?? "")
            };
        }).ToList();
    }

    /// <summary>
    /// loadText（API.md §7）：无任何进行中 → 「今日空闲」；否则拼接各状态数量；
    /// 今天有到场 → 追加「 · 城市」；否则若无处理中 → 追加「 · 今日未出差」。
    /// </summary>
    public static string LoadText(int doing, int accepted, int submitted, string? todayArriveCity)
    {
        var parts = new List<string>();
        if (doing > 0) parts.Add($"处理中 {doing} 单");
        if (accepted > 0) parts.Add($"待启动 {accepted} 单");
        if (submitted > 0) parts.Add($"待验收 {submitted} 单");
        var text = parts.Count == 0 ? "今日空闲" : string.Join("、", parts);
        if (todayArriveCity != null)
        {
            if (todayArriveCity.Length > 0) text += $" · {todayArriveCity}";
        }
        else if (doing == 0) text += " · 今日未出差";
        return text;
    }
}
