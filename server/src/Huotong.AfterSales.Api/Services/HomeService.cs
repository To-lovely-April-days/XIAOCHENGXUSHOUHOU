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
                // 本人参与（负责人或协助人）的待启动 + 处理中 + 暂停搁置
                dto.ActiveOrderCount = await _db.Orders.CountAsync(o =>
                    (o.AssigneeId == me.Id || o.Assistants.Any(a => a.EngineerId == me.Id)) && Doing.Contains(o.Status));
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
        // 「我的」= 本人参与（负责人或协助人）
        var mine = await _view.Orders().Where(o => (o.AssigneeId == me.Id || o.Assistants.Any(a => a.EngineerId == me.Id))
                                                   && o.Status != OrderStatus.Voided && o.Status != OrderStatus.Pending).ToListAsync();
        var items = (await _view.BuildList(mine, user)).ToDictionary(i => i.Id);

        var inProgress = mine.Where(o => o.Status == OrderStatus.InProgress).Select(o => items[o.Id])
            .OrderByDescending(i => i.MyOnSiteSince != null)
            .ThenByDescending(i => i.OnSiteSince != null)
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
            var next = await _view.BuildOne(new NextOrderDto(), n, user);
            next.Address = n.Address;
            next.ContactName = n.ContactName;
            next.ContactPhone = n.ContactPhone;
            next.Latitude = n.Latitude;
            next.Longitude = n.Longitude;
            dto.Next = next;
        }
        else dto.State = "idle";

        dto.Upcoming = accepted.Where(o => o.Id != dto.Next?.Id && o.Id != dto.Current?.Id).Select(o => items[o.Id]).ToList();

        var hall = await _orders.Hall("all", user);
        dto.Hall = new HallSummaryDto { Count = hall.Counts["all"], UrgentCount = hall.Counts["urgent"], Items = hall.Items.Take(2).ToList() };

        dto.RecentDone = mine.Where(o => o.Status is OrderStatus.Submitted or OrderStatus.Closed)
            .OrderByDescending(o => o.ClosedAt ?? o.SubmittedAt ?? o.CreatedAt).Take(2).Select(o => items[o.Id]).ToList();
        dto.OnHold = mine.Where(o => o.Status == OrderStatus.OnHold)
            .OrderByDescending(o => o.HoldAt ?? DateTime.MinValue).ThenByDescending(o => o.Id).Select(o => items[o.Id]).ToList();
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

        // 待办：固定顺序 HoldRequest → AssistantRequest → Unassigned → Submitted → PriceSuggestion → MissingPunch → DistanceAbnormal
        var pendingRequests = await _db.OrderRequests.Include(r => r.Order).Include(r => r.Requester).Include(r => r.TargetEngineer)
            .Where(r => r.Status == RequestStatus.Pending).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync();
        foreach (var r in pendingRequests.Where(r => r.Type == OrderRequestType.Hold))
        {
            dto.Todos.Add(new TodoDto
            {
                Type = "HoldRequest", OrderId = r.OrderId, RequestId = r.Id, Title = r.Order?.CustomerName ?? "",
                Urgent = r.Order?.Urgency == Urgency.Urgent,
                Subtitle = $"{r.Requester?.Name} 申请暂停 · {EnumText.HoldText(r.HoldReasonType, r.Note)}"
            });
        }
        foreach (var r in pendingRequests.Where(r => r.Type == OrderRequestType.AddAssistant))
        {
            dto.Todos.Add(new TodoDto
            {
                Type = "AssistantRequest", OrderId = r.OrderId, RequestId = r.Id, Title = r.Order?.CustomerName ?? "",
                Urgent = r.Order?.Urgency == Urgency.Urgent,
                Subtitle = $"{r.Requester?.Name} 申请添加协助人 {r.TargetEngineer?.Name}" + (string.IsNullOrEmpty(r.Note) ? "" : $"：{r.Note}")
            });
        }
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
                Subtitle = $"{o.Assignee?.Name} {when}提交完工，资料已上传" + (o.PriceSet ? "" : "，尚未定价")
            });
        }
        foreach (var x in await PendingPriceSuggestions(_db))
        {
            dto.Todos.Add(new TodoDto
            {
                Type = "PriceSuggestion", OrderId = x.OrderId, Title = x.Order?.CustomerName ?? "", Urgent = x.Order?.Urgency == Urgency.Urgent,
                Subtitle = $"{x.Sales?.Name} 建议价格 {EnumText.Money(x.Amount)}" + (string.IsNullOrEmpty(x.Note) ? "" : $"（{x.Note}）")
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

    /// <summary>
    /// 待主管处理的价格建议：每单最新一条建议且 pending（主管尚未在其后定价），工单未闭环未作废；按建议时间正序。
    /// 主管待办与概况 priceSuggestions 共用。
    /// </summary>
    public static async Task<List<PriceSuggestion>> PendingPriceSuggestions(AppDbContext db)
    {
        var all = await db.PriceSuggestions.Include(x => x.Order).Include(x => x.Sales)
            .Where(x => x.Order!.Status != OrderStatus.Closed && x.Order.Status != OrderStatus.Voided).ToListAsync();
        return all.GroupBy(x => x.OrderId)
            .Select(g => g.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First())
            .Where(x => OrderViewBuilder.SuggestionPending(x.Order!.PriceSetAt, x.CreatedAt))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
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
        // 作为协助人参与的 待启动 / 处理中 / 暂停搁置 工单数
        var assisting = (await _db.OrderAssistants.Where(a => a.Order!.Status == OrderStatus.Accepted
                || a.Order.Status == OrderStatus.InProgress || a.Order.Status == OrderStatus.OnHold)
            .Select(a => a.EngineerId).ToListAsync()).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

        return engineers.Select(e =>
        {
            var mine = orders.Where(o => o.AssigneeId == e.Id).ToList();
            var doing = mine.Count(o => o.Status is OrderStatus.InProgress or OrderStatus.OnHold);
            var acc = mine.Count(o => o.Status == OrderStatus.Accepted);
            var sub = mine.Count(o => o.Status == OrderStatus.Submitted);
            var lastArrive = todayPunches.Where(p => p.EngineerId == e.Id && p.Kind == PunchKind.Arrive).OrderByDescending(p => p.Time).FirstOrDefault();
            var assist = assisting.GetValueOrDefault(e.Id);
            return new EngineerLoadDto
            {
                User = _view.Brief(e), DoingCount = doing, AcceptedCount = acc, SubmittedCount = sub, AssistCount = assist,
                OnSiteNow = pairs.Any(p => p.Arrive.EngineerId == e.Id && p.IsRunning(today)),
                LoadText = LoadText(doing, acc, sub, lastArrive == null ? null : cities.GetValueOrDefault(lastArrive.OrderId) ?? "", assist)
            };
        }).ToList();
    }

    /// <summary>
    /// loadText（API.md §7）：无任何进行中 → 「今日空闲」；否则拼接各状态数量；
    /// 今天有到场 → 追加「 · 城市」；否则若无处理中 → 追加「 · 今日未出差」。
    /// </summary>
    public static string LoadText(int doing, int accepted, int submitted, string? todayArriveCity, int assisting = 0)
    {
        var parts = new List<string>();
        if (doing > 0) parts.Add($"处理中 {doing} 单");
        if (accepted > 0) parts.Add($"待启动 {accepted} 单");
        if (submitted > 0) parts.Add($"待验收 {submitted} 单");
        if (assisting > 0) parts.Add($"协助 {assisting} 单");
        var text = parts.Count == 0 ? "今日空闲" : string.Join("、", parts);
        if (todayArriveCity != null)
        {
            if (todayArriveCity.Length > 0) text += $" · {todayArriveCity}";
        }
        else if (doing == 0) text += " · 今日未出差";
        return text;
    }
}
