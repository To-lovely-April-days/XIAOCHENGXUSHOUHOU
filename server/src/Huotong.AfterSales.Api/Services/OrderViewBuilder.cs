using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

/// <summary>实体 → DTO 映射，以及工单计算字段（工时、异常、现场计时）</summary>
public class OrderViewBuilder
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly FileService _files;

    public OrderViewBuilder(AppDbContext db, IClock clock, FileService files)
    {
        _db = db;
        _clock = clock;
        _files = files;
    }

    public IQueryable<WorkOrder> Orders() => _db.Orders
        .Include(o => o.Creator).ThenInclude(e => e!.AvatarFile)
        .Include(o => o.Assignee).ThenInclude(e => e!.AvatarFile);

    public UserBrief Brief(Employee e) => new()
    {
        Id = e.Id, Name = e.Name, Surname = e.Surname, Role = e.Role,
        AvatarUrl = e.AvatarFile == null ? null : _files.Url(e.AvatarFile)
    };

    public UserDto User(Employee e, bool self = true) => new()
    {
        Id = e.Id, Name = e.Name, Surname = e.Surname, Role = e.Role,
        AvatarUrl = e.AvatarFile == null ? null : _files.Url(e.AvatarFile),
        RoleName = EnumText.RoleName(e.Role), Department = e.Department,
        PhoneMasked = MaskPhone(e.Phone), Phone = self ? e.Phone : null
    };

    public static string MaskPhone(string phone) =>
        phone.Length >= 11 ? phone[..3] + "****" + phone[^4..] : phone;

    public async Task<Employee> LoadEmployee(int id) =>
        await _db.Employees.Include(e => e.AvatarFile).FirstOrDefaultAsync(e => e.Id == id)
        ?? throw new BizException(40100, "账号不存在，请重新登录");

    /// <summary>批量加载工单的打卡（精简）</summary>
    public async Task<Dictionary<int, List<PunchInfo>>> PunchMap(IEnumerable<int> orderIds)
    {
        var ids = orderIds.Distinct().ToList();
        var rows = await _db.Punches.Where(p => ids.Contains(p.OrderId))
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement))
            .ToListAsync();
        return rows.GroupBy(r => r.OrderId).ToDictionary(g => g.Key, g => g.ToList());
    }

    public async Task<List<OrderListItem>> BuildList(IEnumerable<WorkOrder> orders)
    {
        var list = orders.ToList();
        var map = await PunchMap(list.Select(o => o.Id));
        return list.Select(o => FillList(new OrderListItem(), o, map.GetValueOrDefault(o.Id) ?? new())).ToList();
    }

    public async Task<OrderListItem> BuildOne(WorkOrder o) => (await BuildList(new[] { o }))[0];

    public T FillList<T>(T dto, WorkOrder o, List<PunchInfo> punches) where T : OrderListItem
    {
        var today = _clock.Now.Date;
        var pairs = PunchCalculator.Pair(punches);
        dto.Id = o.Id;
        dto.OrderNo = o.OrderNo;
        dto.Status = o.Status;
        dto.Urgency = o.Urgency;
        dto.ServiceType = o.ServiceType;
        dto.ProblemType = o.ProblemType;
        dto.CustomerName = o.CustomerName;
        dto.City = o.City;
        dto.Region = o.Region;
        dto.DeviceModel = o.DeviceModel;
        dto.DeviceSn = o.DeviceSn;
        dto.Symptom = o.Symptom;
        dto.Price = o.Price;
        dto.Assignee = o.Assignee == null ? null : Brief(o.Assignee);
        dto.Creator = o.Creator == null ? new UserBrief() : Brief(o.Creator);
        dto.CreatedAt = o.CreatedAt;
        dto.ClaimedAt = o.ClaimedAt;
        dto.AppointmentTime = o.AppointmentTime;
        dto.SubmittedAt = o.SubmittedAt;
        dto.ClosedAt = o.ClosedAt;
        dto.TotalMinutes = pairs.Sum(p => p.Minutes);
        dto.OnSiteSince = pairs.Where(p => p.IsRunning(today)).Select(p => (DateTime?)p.Arrive.Time).Max();
        dto.LastArriveTime = punches.Where(p => p.Kind == PunchKind.Arrive).Select(p => (DateTime?)p.Time).Max();
        dto.Issues = PunchCalculator.Issues(punches, today);
        return dto;
    }

    public PunchDto PunchToDto(Punch p, int? pairMinutes) => new()
    {
        Id = p.Id, OrderId = p.OrderId, Engineer = p.Engineer == null ? new UserBrief() : Brief(p.Engineer),
        Kind = p.Kind, Time = p.Time, Latitude = p.Latitude, Longitude = p.Longitude, Address = p.Address,
        DistanceMeters = p.DistanceMeters, OutOfRange = p.OutOfRange,
        Photo = p.PhotoFile == null ? null : _files.ToDto(p.PhotoFile),
        IsSupplement = p.IsSupplement, SupplementBy = p.SupplementBy == null ? null : Brief(p.SupplementBy),
        SupplementReason = p.SupplementReason, SupplementAt = p.SupplementAt,
        PairMinutes = p.Kind == PunchKind.Leave ? pairMinutes : null
    };

    public IQueryable<Punch> PunchesFull() => _db.Punches
        .Include(p => p.Engineer).ThenInclude(e => e!.AvatarFile)
        .Include(p => p.SupplementBy).ThenInclude(e => e!.AvatarFile)
        .Include(p => p.PhotoFile);

    /// <summary>把完整打卡实体列表映射为 DTO（计算 Leave 的 pairMinutes）</summary>
    public List<PunchDto> PunchDtos(List<Punch> punches)
    {
        var pairMin = PunchCalculator.LeavePairMinutes(PunchCalculator.Pair(punches.Select(PunchInfo.From)));
        return punches.OrderBy(p => p.Time).ThenBy(p => p.Id)
            .Select(p => PunchToDto(p, pairMin.TryGetValue(p.Id, out var m) ? m : null)).ToList();
    }

    public async Task<OrderDetail> BuildDetail(int id, CurrentUser user)
    {
        var o = await Orders().FirstOrDefaultAsync(x => x.Id == id) ?? throw BizException.NotFound("工单不存在");
        EnsureCanView(o, user);
        var now = _clock.Now;
        var punches = await PunchesFull().Where(p => p.OrderId == id).ToListAsync();
        var infos = punches.Select(PunchInfo.From).ToList();
        var d = FillList(new OrderDetail(), o, infos);

        d.Source = o.Source;
        d.ContactName = o.ContactName;
        d.ContactPhone = o.ContactPhone;
        d.Address = o.Address;
        d.Latitude = o.Latitude;
        d.Longitude = o.Longitude;
        d.SatDate = o.SatDate;
        d.SatSigned = o.SatSigned;
        d.ProductStage = o.SatSigned ? "老产品" : "新产品";
        d.ProjectNo = o.ProjectNo;
        d.FaultTime = o.FaultTime;
        d.CustomerTried = o.CustomerTried;
        d.SiteEnvironment = o.SiteEnvironment;
        d.CustomerRequest = o.CustomerRequest;
        d.HoldReason = o.HoldReason;
        d.Completion = o.RunStatus == null ? null : new CompletionDto
        {
            FaultCause = o.FaultCause ?? "", Solution = o.Solution ?? "", PartsReplaced = o.PartsReplaced ?? "",
            RunStatus = o.RunStatus.Value, SubmittedAt = o.SubmittedAt
        };

        var files = await _db.Files.Where(f => f.OrderId == id).OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).ToListAsync();
        d.Files = new OrderFilesDto
        {
            OrderPhotos = files.Where(f => f.Category == FileCategory.OrderPhoto).Select(_files.ToDto).ToList(),
            PunchPhotos = files.Where(f => f.Category == FileCategory.PunchPhoto).Select(_files.ToDto).ToList(),
            ProcessPhotos = files.Where(f => f.Category == FileCategory.ProcessPhoto).Select(_files.ToDto).ToList(),
            Signatures = files.Where(f => f.Category == FileCategory.Signature).Select(_files.ToDto).ToList(),
        };

        d.Punches = PunchDtos(punches);

        var logs = await _db.StatusLogs.Include(l => l.Actor).ThenInclude(e => e!.AvatarFile)
            .Where(l => l.OrderId == id).OrderBy(l => l.Time).ThenBy(l => l.Id).ToListAsync();
        d.Logs = logs.Select(l => new StatusLogDto
        {
            Id = l.Id, Text = l.Text, Time = l.Time, Actor = l.Actor == null ? new UserBrief() : Brief(l.Actor), ToStatus = l.ToStatus
        }).ToList();

        d.IssueDetails = IssueDetails(infos, punches, now);
        d.Actions = OrderStateMachine.Actions(user.Role, user.Id, o.Status, o.AssigneeId, o.ServiceType,
            d.Issues.Contains(IssueType.MissingPunch));
        return d;
    }

    public static List<IssueDetailDto> IssueDetails(List<PunchInfo> infos, List<Punch> punches, DateTime now)
    {
        var result = new List<IssueDetailDto>();
        foreach (var pair in PunchCalculator.Pair(infos).Where(p => p.IsMissing(now.Date)))
        {
            var t = pair.Arrive.Time;
            result.Add(new IssueDetailDto
            {
                Type = IssueType.MissingPunch, PunchId = pair.Arrive.Id, Date = DateOnly.FromDateTime(t),
                Text = $"{TimeText.MdHm(t)} 到场后未打离场卡，当天未计工时。补录后系统重新计算。"
            });
        }
        foreach (var p in punches.Where(p => p.OutOfRange).OrderBy(p => p.Time))
        {
            result.Add(new IssueDetailDto
            {
                Type = IssueType.DistanceAbnormal, PunchId = p.Id, Date = DateOnly.FromDateTime(p.Time),
                Text = $"{TimeText.MdHm(p.Time)} {EnumText.Kind(p.Kind)}打卡距客户 {GeoUtil.DistanceText(p.DistanceMeters ?? 0)}，超过 1 km。系统仅标记不拦截，请查看照片后判断。"
            });
        }
        return result;
    }

    /// <summary>详情权限：主管/销售任意；工程师只能看待认领、本人负责或本人创建的工单</summary>
    public static void EnsureCanView(WorkOrder o, CurrentUser user)
    {
        if (user.Role != Role.Engineer) return;
        if (o.Status == OrderStatus.Pending || o.AssigneeId == user.Id || o.CreatorId == user.Id) return;
        throw BizException.Forbidden("无权查看该工单");
    }
}
