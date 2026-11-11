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
        .Include(o => o.Assignee).ThenInclude(e => e!.AvatarFile)
        .Include(o => o.Assistants).ThenInclude(a => a.Engineer).ThenInclude(e => e!.AvatarFile);

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

    /// <summary>列表计算字段所需的批量数据：待审批申请数、返厂已记录节点</summary>
    public record ListExtras(Dictionary<int, int> PendingRequests, Dictionary<int, List<FactoryNode>> FactoryNodes);

    public async Task<ListExtras> Extras(IEnumerable<int> orderIds)
    {
        var ids = orderIds.Distinct().ToList();
        var pending = (await _db.OrderRequests.Where(r => ids.Contains(r.OrderId) && r.Status == RequestStatus.Pending)
                .Select(r => r.OrderId).ToListAsync())
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var nodes = (await _db.FactoryRecords.Where(r => ids.Contains(r.OrderId)).Select(r => new { r.OrderId, r.Node }).ToListAsync())
            .GroupBy(x => x.OrderId).ToDictionary(g => g.Key, g => g.Select(x => x.Node).ToList());
        return new ListExtras(pending, nodes);
    }

    /// <summary>orders 必须来自 Orders()（带负责人、协助人导航）</summary>
    public async Task<List<OrderListItem>> BuildList(IEnumerable<WorkOrder> orders, CurrentUser user)
    {
        var list = orders.ToList();
        var ids = list.Select(o => o.Id).ToList();
        var map = await PunchMap(ids);
        var extras = await Extras(ids);
        return list.Select(o => FillList(new OrderListItem(), o, map.GetValueOrDefault(o.Id) ?? new(), user,
            extras.PendingRequests.GetValueOrDefault(o.Id), extras.FactoryNodes.GetValueOrDefault(o.Id))).ToList();
    }

    public async Task<OrderListItem> BuildOne(WorkOrder o, CurrentUser user) => (await BuildList(new[] { o }, user))[0];

    /// <summary>单个工单填充到 OrderListItem 的子类（如首页 next）</summary>
    public async Task<T> BuildOne<T>(T dto, WorkOrder o, CurrentUser user) where T : OrderListItem
    {
        var map = await PunchMap(new[] { o.Id });
        var extras = await Extras(new[] { o.Id });
        return FillList(dto, o, map.GetValueOrDefault(o.Id) ?? new(), user,
            extras.PendingRequests.GetValueOrDefault(o.Id), extras.FactoryNodes.GetValueOrDefault(o.Id));
    }

    /// <summary>当前用户在工单中的角色（非参与人为 null）</summary>
    public static ParticipantRole? RoleIn(WorkOrder o, int userId) =>
        o.AssigneeId == userId ? ParticipantRole.Lead
        : o.Assistants.Any(a => a.EngineerId == userId) ? ParticipantRole.Assistant : null;

    /// <summary>
    /// 实体 → OrderListItem。价格可见性在此统一处理：Engineer 永远拿不到 price / priceSet（CHANGES-v1.1 §2.1 硬规则）。
    /// </summary>
    public T FillList<T>(T dto, WorkOrder o, List<PunchInfo> punches, CurrentUser user,
        int pendingRequests = 0, IEnumerable<FactoryNode>? factoryNodes = null) where T : OrderListItem
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
        if (user.Role == Role.Engineer)
        {
            dto.Price = null;
            dto.PriceSet = null;
        }
        else
        {
            dto.Price = o.PriceSet ? o.Price : null;
            dto.PriceSet = o.PriceSet;
        }
        dto.Assignee = o.Assignee == null ? null : Brief(o.Assignee);
        dto.Assistants = o.Assistants.OrderBy(a => a.AddedAt).ThenBy(a => a.Id)
            .Where(a => a.Engineer != null).Select(a => Brief(a.Engineer!)).ToList();
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

        dto.MyRole = RoleIn(o, user.Id);
        if (dto.MyRole != null)
        {
            var minePairs = pairs.Where(p => p.Arrive.EngineerId == user.Id).ToList();
            dto.MyMinutes = minePairs.Sum(p => p.Minutes);
            dto.MyOnSiteSince = minePairs.Where(p => p.IsRunning(today)).Select(p => (DateTime?)p.Arrive.Time).Max();
        }
        dto.ProgressStep = OrderStateMachine.ProgressStep(o.Status, o.HoldFromStatus);
        if (o.Status == OrderStatus.OnHold)
        {
            // 旧数据（无原因类型）：视为 Other + 说明，来源状态 InProgress
            dto.HoldReasonType = o.HoldReasonType ?? HoldReasonType.Other;
            dto.HoldText = EnumText.HoldText(o.HoldReasonType, o.HoldReason);
            dto.HoldFromStatus = OrderStateMachine.ResumeTarget(o.HoldFromStatus);
        }
        dto.PendingRequestCount = pendingRequests;
        dto.FactoryText = o.ServiceType == ServiceType.Factory ? EnumText.FactoryText(factoryNodes ?? Enumerable.Empty<FactoryNode>()) : null;
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

    private IQueryable<Employee> Employees() => _db.Employees.Include(e => e.AvatarFile);

    public RequestDto RequestToDto(OrderRequest r, CurrentUser user) => new()
    {
        Id = r.Id, OrderId = r.OrderId, Type = r.Type, Status = r.Status,
        Requester = r.Requester == null ? new UserBrief() : Brief(r.Requester), CreatedAt = r.CreatedAt,
        ReasonType = r.Type == OrderRequestType.Hold ? r.HoldReasonType ?? HoldReasonType.Other : null,
        ReasonText = r.Type == OrderRequestType.Hold ? EnumText.HoldReason(r.HoldReasonType ?? HoldReasonType.Other) : null,
        Note = r.Note,
        Text = RequestText(r),
        Engineer = r.TargetEngineer == null ? null : Brief(r.TargetEngineer),
        Decider = r.Decider == null ? null : Brief(r.Decider), DecidedAt = r.DecidedAt, Reply = r.Reply,
        CanDecide = user.Role == Role.Supervisor && r.Status == RequestStatus.Pending,
        CanCancel = r.RequesterId == user.Id && r.Status == RequestStatus.Pending
    };

    /// <summary>申请的一句话描述：暂停 = holdText；协助人 = 「添加协助人 王工[：说明]」</summary>
    public static string RequestText(OrderRequest r) => r.Type == OrderRequestType.Hold
        ? EnumText.HoldText(r.HoldReasonType, r.Note)
        : $"添加协助人 {r.TargetEngineer?.Name}" + (string.IsNullOrEmpty(r.Note) ? "" : $"：{r.Note}");

    /// <summary>价格建议是否待处理：主管尚未在此建议之后定价</summary>
    public static bool SuggestionPending(DateTime? priceSetAt, DateTime createdAt) => priceSetAt == null || priceSetAt < createdAt;

    public async Task<OrderDetail> BuildDetail(int id, CurrentUser user)
    {
        var o = await Orders().FirstOrDefaultAsync(x => x.Id == id) ?? throw BizException.NotFound("工单不存在");
        EnsureCanView(o, user);
        var now = _clock.Now;
        var today = now.Date;
        var isEngineer = user.Role == Role.Engineer;
        var punches = await PunchesFull().Where(p => p.OrderId == id).ToListAsync();
        var infos = punches.Select(PunchInfo.From).ToList();
        var factoryRecords = o.ServiceType != ServiceType.Factory ? new List<FactoryRecord>()
            : await _db.FactoryRecords.Include(r => r.Operator).ThenInclude(e => e!.AvatarFile).Where(r => r.OrderId == id).ToListAsync();
        var requests = await _db.OrderRequests
            .Include(r => r.Requester).ThenInclude(e => e!.AvatarFile)
            .Include(r => r.TargetEngineer).ThenInclude(e => e!.AvatarFile)
            .Include(r => r.Decider).ThenInclude(e => e!.AvatarFile)
            .Where(r => r.OrderId == id).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync();
        var d = FillList(new OrderDetail(), o, infos, user,
            requests.Count(r => r.Status == RequestStatus.Pending), factoryRecords.Select(r => r.Node));

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
        d.Completion = o.RunStatus == null ? null : new CompletionDto
        {
            FaultCause = o.FaultCause ?? "", Solution = o.Solution ?? "", PartsReplaced = o.PartsReplaced ?? "",
            RunStatus = o.RunStatus.Value, SubmittedAt = o.SubmittedAt
        };

        // 价格：工程师一律不返回
        if (!isEngineer)
        {
            d.PriceSetAt = o.PriceSetAt;
            var sug = await _db.PriceSuggestions.Include(x => x.Sales).ThenInclude(e => e!.AvatarFile)
                .Where(x => x.OrderId == id).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
            if (sug != null)
                d.PriceSuggestion = new PriceSuggestionDto
                {
                    Id = sug.Id, Amount = sug.Amount, Note = sug.Note, By = sug.Sales == null ? new UserBrief() : Brief(sug.Sales),
                    CreatedAt = sug.CreatedAt, Pending = SuggestionPending(o.PriceSetAt, sug.CreatedAt)
                };
        }

        // 暂停
        d.HoldReason = d.HoldText;
        if (o.Status == OrderStatus.OnHold)
        {
            var type = o.HoldReasonType ?? HoldReasonType.Other;
            var by = o.HoldById == null ? null : await Employees().FirstOrDefaultAsync(e => e.Id == o.HoldById);
            d.Hold = new HoldDto
            {
                ReasonType = type, ReasonText = EnumText.HoldReason(type),
                Note = string.IsNullOrWhiteSpace(o.HoldReason) ? null : o.HoldReason,
                Text = d.HoldText ?? "", FromStatus = OrderStateMachine.ResumeTarget(o.HoldFromStatus),
                At = o.HoldAt, By = by == null ? null : Brief(by)
            };
        }

        d.Requests = requests.Select(r => RequestToDto(r, user)).ToList();

        var files = await _db.Files.Where(f => f.OrderId == id).OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).ToListAsync();
        d.Files = new OrderFilesDto
        {
            OrderPhotos = files.Where(f => f.Category == FileCategory.OrderPhoto).Select(_files.ToDto).ToList(),
            PunchPhotos = files.Where(f => f.Category == FileCategory.PunchPhoto).Select(_files.ToDto).ToList(),
            ProcessPhotos = files.Where(f => f.Category == FileCategory.ProcessPhoto).Select(_files.ToDto).ToList(),
            Signatures = files.Where(f => f.Category == FileCategory.Signature).Select(_files.ToDto).ToList(),
            FactoryPhotos = files.Where(f => FactoryRules.IsFactoryPhoto(f.Category)).Select(_files.ToDto).ToList(),
        };

        d.Punches = PunchDtos(punches);

        var logs = await _db.StatusLogs.Include(l => l.Actor).ThenInclude(e => e!.AvatarFile)
            .Where(l => l.OrderId == id).OrderBy(l => l.Time).ThenBy(l => l.Id).ToListAsync();
        d.Logs = logs.Where(l => !isEngineer || l.Kind != LogKind.Price).Select(l => new StatusLogDto
        {
            Id = l.Id, Text = l.Text, Time = l.Time, Actor = l.Actor == null ? new UserBrief() : Brief(l.Actor), ToStatus = l.ToStatus,
            Kind = l.Kind ?? LogKind.Status
        }).ToList();

        d.IssueDetails = IssueDetails(infos, punches, now);
        d.Participants = Participants(o, infos, punches, today);

        // 返厂时间线
        var recorded = factoryRecords.Select(r => r.Node).ToHashSet();
        if (o.ServiceType == ServiceType.Factory)
            d.Factory = BuildFactory(o, factoryRecords, files, user);

        var hasMissing = d.Issues.Contains(IssueType.MissingPunch);
        d.Actions = OrderStateMachine.Actions(new OrderStateMachine.ActionContext(user.Role, user.Id, o.Status, o.AssigneeId, o.ServiceType,
            hasMissing, d.MyRole == ParticipantRole.Assistant, o.PriceSet,
            requests.Any(r => r.Type == OrderRequestType.Hold && r.Status == RequestStatus.Pending),
            d.Factory?.Nodes.Any(n => n.CanRecord) ?? false, recorded.Contains(FactoryNode.ShippedBack)));

        if (user.Role == Role.Supervisor && o.Status == OrderStatus.Submitted)
        {
            d.CloseBlockedReason = hasMissing ? "请先补录缺卡" : !o.PriceSet ? "请先设置服务价格（保内填 0）" : null;
            if (o.ServiceType == ServiceType.Factory && !recorded.Contains(FactoryNode.CustomerSigned))
                d.CloseHints.Add("客户签收尚未记录");
        }
        return d;
    }

    private FactoryDto BuildFactory(WorkOrder o, List<FactoryRecord> records, List<StoredFile> files, CurrentUser user)
    {
        var byNode = records.ToDictionary(r => r.Node);
        var recorded = byNode.Keys.ToHashSet();
        var states = FactoryRules.States(recorded, o.Status);
        var dto = new FactoryDto();
        foreach (var n in FactoryRules.All)
        {
            var node = new FactoryNodeDto { Node = n, Label = EnumText.Factory(n), State = states[n] };
            if (n == FactoryNode.Repairing)
            {
                node.Time = byNode.GetValueOrDefault(FactoryNode.FactoryReceived)?.Time;
            }
            else
            {
                if (byNode.TryGetValue(n, out var r))
                {
                    node.Recorded = true;
                    node.Time = r.Time;
                    node.Operator = r.Operator == null ? null : Brief(r.Operator);
                    node.Courier = r.Courier;
                    node.TrackingNo = r.TrackingNo;
                    node.Note = r.Note;
                    var cat = FactoryRules.PhotoCategory(n);
                    if (cat != null) node.Photos = files.Where(f => f.Category == cat).Select(_files.ToDto).ToList();
                }
                node.CanRecord = FactoryRules.CanRecord(n, o, recorded, user);
                node.CanEdit = FactoryRules.CanEdit(n, o, recorded, user);
            }
            dto.Nodes.Add(node);
        }
        return dto;
    }

    /// <summary>参与人：负责人 → 协助人（按添加时间）→ 已退出（在本单有打卡但已不是负责人/协助人，按首次打卡时间）</summary>
    private List<ParticipantDto> Participants(WorkOrder o, List<PunchInfo> infos, List<Punch> punches, DateTime today)
    {
        var result = new List<ParticipantDto>();
        if (o.Assignee == null) return result;
        var pairs = PunchCalculator.Pair(infos);
        ParticipantDto Make(Employee e, ParticipantRole role)
        {
            var mine = pairs.Where(p => p.Arrive.EngineerId == e.Id).ToList();
            return new ParticipantDto
            {
                User = Brief(e), Role = role, TotalMinutes = mine.Sum(p => p.Minutes),
                OnSiteSince = mine.Where(p => p.IsRunning(today)).Select(p => (DateTime?)p.Arrive.Time).Max(),
                MissingPunch = mine.Any(p => p.IsMissing(today))
            };
        }
        result.Add(Make(o.Assignee, ParticipantRole.Lead));
        var current = new HashSet<int> { o.Assignee.Id };
        foreach (var a in o.Assistants.OrderBy(a => a.AddedAt).ThenBy(a => a.Id).Where(a => a.Engineer != null))
        {
            result.Add(Make(a.Engineer!, ParticipantRole.Assistant));
            current.Add(a.EngineerId);
        }
        foreach (var g in punches.Where(p => !current.Contains(p.EngineerId) && p.Engineer != null)
                     .GroupBy(p => p.EngineerId).OrderBy(g => g.Min(p => p.Time)).ThenBy(g => g.Key))
            result.Add(Make(g.First().Engineer!, ParticipantRole.Former));
        return result;
    }

    /// <summary>缺卡 / 距离异常明细，覆盖本单所有打卡人；文字最前面带打卡人姓名</summary>
    public List<IssueDetailDto> IssueDetails(List<PunchInfo> infos, List<Punch> punches, DateTime now)
    {
        var result = new List<IssueDetailDto>();
        var engineers = punches.Where(p => p.Engineer != null).GroupBy(p => p.EngineerId).ToDictionary(g => g.Key, g => g.First().Engineer!);
        foreach (var pair in PunchCalculator.Pair(infos).Where(p => p.IsMissing(now.Date)))
        {
            var t = pair.Arrive.Time;
            var e = engineers.GetValueOrDefault(pair.Arrive.EngineerId);
            result.Add(new IssueDetailDto
            {
                Type = IssueType.MissingPunch, PunchId = pair.Arrive.Id, Date = DateOnly.FromDateTime(t),
                Engineer = e == null ? null : Brief(e),
                Text = $"{e?.Name} {TimeText.MdHm(t)} 到场后未打离场卡，当天未计工时。补录后系统重新计算。".TrimStart()
            });
        }
        foreach (var p in punches.Where(p => p.OutOfRange).OrderBy(p => p.Time))
        {
            result.Add(new IssueDetailDto
            {
                Type = IssueType.DistanceAbnormal, PunchId = p.Id, Date = DateOnly.FromDateTime(p.Time),
                Engineer = p.Engineer == null ? null : Brief(p.Engineer),
                Text = $"{p.Engineer?.Name} {TimeText.MdHm(p.Time)} {EnumText.Kind(p.Kind)}打卡距客户 {GeoUtil.DistanceText(p.DistanceMeters ?? 0)}，超过 1 km。系统仅标记不拦截，请查看照片后判断。".TrimStart()
            });
        }
        return result;
    }

    /// <summary>详情权限：主管/销售任意；工程师只能看待认领、本人负责、本人协助或本人创建的工单（o 须带 Assistants）</summary>
    public static void EnsureCanView(WorkOrder o, CurrentUser user)
    {
        if (user.Role != Role.Engineer) return;
        if (o.Status == OrderStatus.Pending || o.AssigneeId == user.Id || o.CreatorId == user.Id
            || o.Assistants.Any(a => a.EngineerId == user.Id)) return;
        throw BizException.Forbidden("无权查看该工单");
    }
}
