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

    /// <summary>
    /// 详情与所有列表的工单查询：带建单人、负责人、协助人、退出记录（v1.3，权限判断与参与人列表都要用）。
    /// 不 Include 设备（列表只用 DeviceText；详情单独查）。两个集合都很小，保持单条查询，不用 AsSplitQuery。
    /// </summary>
    public IQueryable<WorkOrder> Orders() => _db.Orders
        .Include(o => o.Creator).ThenInclude(e => e!.AvatarFile)
        .Include(o => o.Assignee).ThenInclude(e => e!.AvatarFile)
        .Include(o => o.Assistants).ThenInclude(a => a.Engineer).ThenInclude(e => e!.AvatarFile)
        .Include(o => o.Exits).ThenInclude(x => x.Engineer).ThenInclude(e => e!.AvatarFile);

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

    /// <summary>列表计算字段所需的批量数据：待审批申请数、返厂已记录节点、待确认的转交（v1.3，每单最多一条）</summary>
    public record ListExtras(Dictionary<int, int> PendingRequests, Dictionary<int, List<FactoryNode>> FactoryNodes,
        Dictionary<int, OrderTransfer> PendingTransfers);

    /// <summary>转交查询：带发起人 / 接手人 / 结束人及头像（TransferToDto 需要）</summary>
    public IQueryable<OrderTransfer> Transfers() => _db.OrderTransfers
        .Include(t => t.From).ThenInclude(e => e!.AvatarFile)
        .Include(t => t.To).ThenInclude(e => e!.AvatarFile)
        .Include(t => t.DecidedBy).ThenInclude(e => e!.AvatarFile);

    public async Task<ListExtras> Extras(IEnumerable<int> orderIds)
    {
        var ids = orderIds.Distinct().ToList();
        var pending = (await _db.OrderRequests.Where(r => ids.Contains(r.OrderId) && r.Status == RequestStatus.Pending)
                .Select(r => r.OrderId).ToListAsync())
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var nodes = (await _db.FactoryRecords.Where(r => ids.Contains(r.OrderId)).Select(r => new { r.OrderId, r.Node }).ToListAsync())
            .GroupBy(x => x.OrderId).ToDictionary(g => g.Key, g => g.Select(x => x.Node).ToList());
        // 待确认的转交：批量查一次（不变量 I1 保证每单最多一行；防御性地取最新一行）
        var transfers = (await Transfers().Where(t => ids.Contains(t.OrderId) && t.Status == TransferStatus.Pending).ToListAsync())
            .GroupBy(t => t.OrderId).ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Id).First());
        return new ListExtras(pending, nodes, transfers);
    }

    /// <summary>orders 必须来自 Orders()（带负责人、协助人、退出记录导航）</summary>
    public async Task<List<OrderListItem>> BuildList(IEnumerable<WorkOrder> orders, CurrentUser user)
    {
        var list = orders.ToList();
        var ids = list.Select(o => o.Id).ToList();
        var map = await PunchMap(ids);
        var extras = await Extras(ids);
        return list.Select(o => FillList(new OrderListItem(), o, map.GetValueOrDefault(o.Id) ?? new(), user,
            extras.PendingRequests.GetValueOrDefault(o.Id), extras.FactoryNodes.GetValueOrDefault(o.Id),
            extras.PendingTransfers.GetValueOrDefault(o.Id))).ToList();
    }

    public async Task<OrderListItem> BuildOne(WorkOrder o, CurrentUser user) => (await BuildList(new[] { o }, user))[0];

    /// <summary>单个工单填充到 OrderListItem 的子类（如首页 next）</summary>
    public async Task<T> BuildOne<T>(T dto, WorkOrder o, CurrentUser user) where T : OrderListItem
    {
        var map = await PunchMap(new[] { o.Id });
        var extras = await Extras(new[] { o.Id });
        return FillList(dto, o, map.GetValueOrDefault(o.Id) ?? new(), user,
            extras.PendingRequests.GetValueOrDefault(o.Id), extras.FactoryNodes.GetValueOrDefault(o.Id),
            extras.PendingTransfers.GetValueOrDefault(o.Id));
    }

    /// <summary>
    /// 当前用户在工单中的角色（非参与人为 null）。只返回 Lead / Assistant / null，绝不返回 Former：
    /// 打卡权限、PunchContextDto.myRole、详情里的 IsAssistant 都靠它判断。
    /// </summary>
    public static ParticipantRole? RoleIn(WorkOrder o, int userId) =>
        o.AssigneeId == userId ? ParticipantRole.Lead
        : o.Assistants.Any(a => a.EngineerId == userId) ? ParticipantRole.Assistant : null;

    /// <summary>已退出的人做任何写操作时的统一文案（40300）</summary>
    public const string FormerDenied = "你已退出本单，只能查看";

    /// <summary>
    /// 当前用户是否为本单「已退出」的人（v1.3）：工程师且有退出记录。全系统判断「当前用户是否已退出」只用这个方法；
    /// 销售、主管永远不是（曾是工程师、后来改了角色的人按新角色对待）。o 须带 Exits。
    /// </summary>
    public static bool IsFormer(WorkOrder o, CurrentUser user) =>
        user.Role == Role.Engineer && o.Exits.Any(x => x.EngineerId == user.Id);

    /// <summary>转交 → TransferDto（详情的 transfers、列表的 pendingTransfer 共用）。调用方负责 Include From / To / DecidedBy（各带头像）</summary>
    public TransferDto TransferToDto(OrderTransfer t) => new()
    {
        Id = t.Id, OrderId = t.OrderId, Status = t.Status, StatusText = EnumText.TransferStatusName(t.Status),
        From = t.From == null ? new UserBrief() : Brief(t.From), To = t.To == null ? new UserBrief() : Brief(t.To),
        Reason = t.Reason, CreatedAt = t.CreatedAt, DecidedAt = t.DecidedAt,
        DecidedBy = t.DecidedBy == null ? null : Brief(t.DecidedBy), Reply = t.Reply,
        Text = TransferText(t)
    };

    /// <summary>转交的一句话描述（CHANGES-v1.3 §2.2 固定规则）</summary>
    public static string TransferText(OrderTransfer t)
    {
        var from = t.From?.Name;
        var to = t.To?.Name;
        var head = $"{from} 转交给 {to}，";
        return t.Status switch
        {
            TransferStatus.Pending => head + $"待 {to} 确认",
            TransferStatus.Accepted => head + $"{to} 已接手",
            TransferStatus.Rejected => head + $"{to} 已拒绝" + (string.IsNullOrEmpty(t.Reply) ? "" : $"：{t.Reply}"),
            TransferStatus.Cancelled => t.DecidedById == t.FromEngineerId ? head + "已撤回" : head + $"已由 {t.DecidedBy?.Name} 撤销",
            TransferStatus.Expired => head + $"已失效：{t.Reply}",
            _ => head.TrimEnd('，')
        };
    }

    /// <summary>
    /// 实体 → OrderListItem。价格可见性在此统一处理：Engineer 永远拿不到 price / priceSet（CHANGES-v1.1 §2.1 硬规则）。
    /// o 必须来自 Orders()（读 o.Exits）。v1.3 的 projectNo / deviceText / myRole(Former) / myExitText / pendingTransfer 都在这里统一写，
    /// 详情与列表走同一处。
    /// </summary>
    public T FillList<T>(T dto, WorkOrder o, List<PunchInfo> punches, CurrentUser user,
        int pendingRequests = 0, IEnumerable<FactoryNode>? factoryNodes = null, OrderTransfer? pendingTransfer = null) where T : OrderListItem
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
        dto.ProjectNo = o.ProjectNo;
        // 设备汇总文字：永不为空；列为空白时（防御）退回兼容列，仍为空则「未填设备」
        dto.DeviceText = !string.IsNullOrWhiteSpace(o.DeviceText) ? o.DeviceText
            : !string.IsNullOrWhiteSpace(o.DeviceModel) ? o.DeviceModel : DeviceRules.None;
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
        if (dto.MyRole == null && IsFormer(o, user))
        {
            dto.MyRole = ParticipantRole.Former;
            dto.MyExitText = EnumText.ExitText(o.Exits.First(x => x.EngineerId == user.Id).Via);
        }
        dto.PendingTransfer = pendingTransfer == null ? null : TransferToDto(pendingTransfer);
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
        // 本单全部转交记录（含已结束的）；其中 Pending 的那一条同时作为列表字段 pendingTransfer
        var transfers = await Transfers().Where(t => t.OrderId == id).OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToListAsync();
        var d = FillList(new OrderDetail(), o, infos, user,
            requests.Count(r => r.Status == RequestStatus.Pending), factoryRecords.Select(r => r.Node),
            transfers.LastOrDefault(t => t.Status == TransferStatus.Pending));

        d.Source = o.Source;
        d.ContactName = o.ContactName;
        d.ContactPhone = o.ContactPhone;
        d.Address = o.Address;
        d.Latitude = o.Latitude;
        d.Longitude = o.Longitude;
        d.SatDate = o.SatDate;
        d.SatSigned = o.SatSigned;
        d.SatText = DeviceRules.SatText(o.SatSigned);
        d.SatDateLabel = DeviceRules.SatDateLabel(o.SatSigned);
        d.ProductStage = o.SatSigned ? "老产品" : "新产品";
        d.CanEditSat = DeviceRules.SatEditDenied(o, user) == null;
        d.Devices = (await _db.OrderDevices.AsNoTracking().Where(x => x.OrderId == id).OrderBy(x => x.Seq).ThenBy(x => x.Id).ToListAsync())
            .Select(x => new DeviceDto { Seq = x.Seq, Name = x.Name, Sn = x.Sn, Model = x.Model, Title = DeviceRules.Title(x) }).ToList();
        d.Transfers = transfers.Select(TransferToDto).ToList();
        var isFormer = IsFormer(o, user);
        if (isFormer)
            d.ReadOnlyText = $"你已退出本单（{EnumText.ExitText(o.Exits.First(x => x.EngineerId == user.Id).Via)}），只能查看；你的打卡和工时记录已保留。";
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

        // 已软删除的文件被全局查询过滤器排除
        var files = await _db.Files.Where(f => f.OrderId == id).OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).ToListAsync();

        // v1.2 附件 = 建单照片 + 新附件。可见性在此统一处理：Engineer 永远拿不到受限附件（CHANGES-v1.2 §4.2 硬规则）
        var allAttachments = files.Where(f => AttachmentRules.IsAttachment(f.Category)).ToList();
        var visible = allAttachments.Where(f => AttachmentRules.Visible(f, user)).ToList();
        var uploaders = await UploaderMap(visible.Select(f => f.UploaderId));
        d.Attachments = visible.Select(f => FillAttachment(new AttachmentDto(), f, o, uploaders.GetValueOrDefault(f.UploaderId), user)).ToList();
        d.CanAddAttachment = AttachmentRules.CanAdd(o, user);
        d.CanRestrictAttachment = AttachmentRules.CanRestrict(user);
        d.AttachmentLimit = AttachmentRules.Limit;
        // 唯一包含受限附件的量：剩余名额
        d.AttachmentRemaining = Math.Max(0, AttachmentRules.Limit - allAttachments.Count);

        d.Files = new OrderFilesDto
        {
            // 旧字段（v1.1 小程序）：可见附件里的图片 / 视频
            OrderPhotos = visible.Where(f => FileService.KindOf(f) != FileKind.Document).Select(_files.ToDto).ToList(),
            PunchPhotos = files.Where(f => f.Category == FileCategory.PunchPhoto).Select(_files.ToDto).ToList(),
            ProcessPhotos = files.Where(f => f.Category == FileCategory.ProcessPhoto).Select(_files.ToDto).ToList(),
            Signatures = files.Where(f => f.Category == FileCategory.Signature).Select(_files.ToDto).ToList(),
            FactoryPhotos = files.Where(f => FactoryRules.IsFactoryPhoto(f.Category)).Select(_files.ToDto).ToList(),
        };

        d.Punches = PunchDtos(punches);

        var logs = await _db.StatusLogs.Include(l => l.Actor).ThenInclude(e => e!.AvatarFile)
            .Where(l => l.OrderId == id).OrderBy(l => l.Time).ThenBy(l => l.Id).ToListAsync();
        d.Logs = logs.Where(l => !isEngineer || (l.Kind != LogKind.Price && l.Kind != LogKind.AttachmentPrivate)).Select(l => new StatusLogDto
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
            d.Factory?.Nodes.Any(n => n.CanRecord) ?? false, recorded.Contains(FactoryNode.ShippedBack),
            IsFormer: isFormer, HasPendingTransfer: o.PendingTransferToId != null,
            PendingTransferToMe: isEngineer && o.PendingTransferToId == user.Id));

        if (user.Role == Role.Supervisor && o.Status == OrderStatus.Submitted)
        {
            d.CloseBlockedReason = hasMissing ? "请先补录缺卡" : !o.PriceSet ? "请先设置服务价格（保内填 0）" : null;
            if (o.ServiceType == ServiceType.Factory && !recorded.Contains(FactoryNode.CustomerSigned))
                d.CloseHints.Add("客户签收尚未记录");
        }
        return d;
    }

    /// <summary>批量加载上传人（带头像）</summary>
    public async Task<Dictionary<int, Employee>> UploaderMap(IEnumerable<int> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new();
        return await Employees().Where(e => list.Contains(e.Id)).ToDictionaryAsync(e => e.Id);
    }

    /// <summary>
    /// 文件 → AttachmentDto（工单详情与文件中心共用）。调用方负责只传当前用户可见的文件；
    /// can* 只对附件来源按 AttachmentRules 计算，其余来源恒为 false。
    /// </summary>
    public T FillAttachment<T>(T dto, StoredFile f, WorkOrder o, Employee? uploader, CurrentUser user) where T : AttachmentDto
    {
        _files.Fill(dto, f);
        dto.Source = AttachmentRules.SourceOf(f.Category) ?? "";
        dto.SourceLabel = AttachmentRules.SourceLabel(f.Category);
        dto.Restricted = AttachmentRules.IsAttachment(f.Category) && f.Restricted;
        dto.Uploader = uploader == null ? null : Brief(uploader);
        dto.CanDelete = AttachmentRules.CanDelete(f, o, user);
        dto.CanToggleRestricted = AttachmentRules.CanToggleRestricted(f, user);
        // v1.3 附件类别：只对附件来源有值
        if (AttachmentRules.IsAttachment(f.Category))
        {
            dto.AttachmentType = f.AttachmentType;
            dto.AttachmentTypeLabel = EnumText.AttachmentLabel(f.AttachmentType);
            dto.AttachmentTypeShort = EnumText.AttachmentShort(f.AttachmentType);
            dto.CanChangeType = AttachmentRules.CanChangeType(f, o, user);
        }
        return dto;
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

    /// <summary>
    /// 参与人：负责人 → 协助人（按添加时间）→ 已退出。
    /// 已退出 = 「本单有退出记录的人」∪「在本单有打卡的人」，去掉当前负责人与协助人；同一个人只出现一次（有退出记录的以退出记录为准）。
    /// 排序：有退出记录的按 ExitedAt、没有的按首次打卡时间，混在一起正序，时间相同按员工 id。
    /// 有退出记录的人即使一次卡都没打过也要出现（远程 / 返厂单）。
    /// </summary>
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
        var former = new List<(DateTime At, int EngineerId, ParticipantDto Dto)>();
        var exited = new HashSet<int>();
        foreach (var x in o.Exits.Where(x => !current.Contains(x.EngineerId) && x.Engineer != null))
        {
            if (!exited.Add(x.EngineerId)) continue;
            var dto = Make(x.Engineer!, ParticipantRole.Former);
            dto.ExitText = EnumText.ExitText(x.Via);
            dto.ExitedAt = x.ExitedAt;
            former.Add((x.ExitedAt, x.EngineerId, dto));
        }
        foreach (var g in punches.Where(p => !current.Contains(p.EngineerId) && !exited.Contains(p.EngineerId) && p.Engineer != null)
                     .GroupBy(p => p.EngineerId))
            former.Add((g.Min(p => p.Time), g.Key, Make(g.First().Engineer!, ParticipantRole.Former)));
        result.AddRange(former.OrderBy(f => f.At).ThenBy(f => f.EngineerId).Select(f => f.Dto));
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

    /// <summary>
    /// 详情权限：主管 / 销售任意；工程师只能看待认领、本人负责、本人创建、本人协助的工单，
    /// 以及（v1.3）本人「已退出」的工单、待本人确认转交的工单（后者在转交被拒绝 / 撤回 / 失效的那一刻自动消失）。
    /// o 须带 Assistants 与 Exits。
    /// </summary>
    public static bool CanView(WorkOrder o, CurrentUser user)
    {
        if (user.Role != Role.Engineer) return true;
        return o.Status == OrderStatus.Pending || o.AssigneeId == user.Id || o.CreatorId == user.Id
               || o.Assistants.Any(a => a.EngineerId == user.Id)
               || o.Exits.Any(x => x.EngineerId == user.Id)
               || o.PendingTransferToId == user.Id;
    }

    public static void EnsureCanView(WorkOrder o, CurrentUser user)
    {
        if (!CanView(o, user)) throw BizException.Forbidden("无权查看该工单");
    }
}
