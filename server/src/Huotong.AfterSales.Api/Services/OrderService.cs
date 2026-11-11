using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

public class OrderQuery
{
    public string? Status { get; set; }
    public string? Keyword { get; set; }
    public string? Issue { get; set; }
    public int? AssigneeId { get; set; }
    public string? ServiceType { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    /// <summary>true / false：按是否已定价过滤</summary>
    public string? PriceSet { get; set; }
    /// <summary>true：只返回有待审批申请的工单</summary>
    public bool? PendingRequest { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public partial class OrderService
{
    private static readonly OrderStatus[] Doing = { OrderStatus.Accepted, OrderStatus.InProgress, OrderStatus.OnHold };

    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly OrderViewBuilder _view;
    private readonly FileService _files;
    private readonly INotifier _notifier;
    private readonly ILogger<OrderService> _logger;

    public OrderService(AppDbContext db, IClock clock, OrderViewBuilder view, FileService files, INotifier notifier, ILogger<OrderService> logger)
    {
        _db = db;
        _clock = clock;
        _view = view;
        _files = files;
        _notifier = notifier;
        _logger = logger;
    }

    // ---------- 列表 ----------

    public async Task<ListResult<OrderListItem>> Hall(string? filter, CurrentUser user)
    {
        var orders = await _view.Orders().Where(o => o.Status == OrderStatus.Pending).ToListAsync();
        var counts = new Dictionary<string, int>
        {
            ["all"] = orders.Count,
            ["urgent"] = orders.Count(o => o.Urgency == Urgency.Urgent),
            ["onsite"] = orders.Count(o => o.ServiceType == ServiceType.Onsite),
            ["remote"] = orders.Count(o => o.ServiceType != ServiceType.Onsite),
        };
        IEnumerable<WorkOrder> q = (filter ?? "all").ToLowerInvariant() switch
        {
            "urgent" => orders.Where(o => o.Urgency == Urgency.Urgent),
            "onsite" => orders.Where(o => o.ServiceType == ServiceType.Onsite),
            "remote" => orders.Where(o => o.ServiceType != ServiceType.Onsite),
            "all" or "" => orders,
            _ => throw BizException.BadRequest("筛选条件不正确")
        };
        q = HallOrder(q);
        return new ListResult<OrderListItem> { Items = await _view.BuildList(q, user), Counts = counts };
    }

    public static IEnumerable<WorkOrder> HallOrder(IEnumerable<WorkOrder> q) =>
        q.OrderBy(o => o.Urgency == Urgency.Urgent ? 0 : 1).ThenByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id);

    private static int MinePriority(OrderStatus s) => s switch
    {
        OrderStatus.InProgress => 0,
        OrderStatus.Accepted => 1,
        OrderStatus.OnHold => 2,
        OrderStatus.Submitted => 3,
        OrderStatus.Closed => 4,
        _ => 5
    };

    private static DateTime SortTime(WorkOrder o) => o.ClosedAt ?? o.SubmittedAt ?? o.ClaimedAt ?? o.CreatedAt;

    public async Task<ListResult<OrderListItem>> Mine(CurrentUser user, string? group)
    {
        var orders = await _view.Orders()
            .Where(o => (o.AssigneeId == user.Id || o.Assistants.Any(a => a.EngineerId == user.Id))
                        && o.Status != OrderStatus.Voided && o.Status != OrderStatus.Pending)
            .ToListAsync();
        var counts = new Dictionary<string, int>
        {
            ["doing"] = orders.Count(o => Doing.Contains(o.Status)),
            ["check"] = orders.Count(o => o.Status == OrderStatus.Submitted),
            ["closed"] = orders.Count(o => o.Status == OrderStatus.Closed),
            ["all"] = orders.Count,
        };
        IEnumerable<WorkOrder> q = (group ?? "all").ToLowerInvariant() switch
        {
            "doing" => orders.Where(o => Doing.Contains(o.Status)),
            "check" => orders.Where(o => o.Status == OrderStatus.Submitted),
            "closed" => orders.Where(o => o.Status == OrderStatus.Closed),
            "all" or "" => orders,
            _ => throw BizException.BadRequest("分组参数不正确")
        };
        q = q.OrderBy(o => MinePriority(o.Status)).ThenByDescending(SortTime).ThenByDescending(o => o.Id);
        return new ListResult<OrderListItem> { Items = await _view.BuildList(q, user), Counts = counts };
    }

    public async Task<ListResult<OrderListItem>> Created(CurrentUser user, string? group)
    {
        var orders = await _view.Orders()
            .Where(o => o.CreatorId == user.Id && o.Status != OrderStatus.Voided)
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .ToListAsync();
        var counts = new Dictionary<string, int>
        {
            ["all"] = orders.Count,
            ["wait"] = orders.Count(o => o.Status == OrderStatus.Pending),
            ["doing"] = orders.Count(o => Doing.Contains(o.Status)),
            ["check"] = orders.Count(o => o.Status == OrderStatus.Submitted),
            ["closed"] = orders.Count(o => o.Status == OrderStatus.Closed),
        };
        IEnumerable<WorkOrder> q = (group ?? "all").ToLowerInvariant() switch
        {
            "wait" => orders.Where(o => o.Status == OrderStatus.Pending),
            "doing" => orders.Where(o => Doing.Contains(o.Status)),
            "check" => orders.Where(o => o.Status == OrderStatus.Submitted),
            "closed" => orders.Where(o => o.Status == OrderStatus.Closed),
            "all" or "" => orders,
            _ => throw BizException.BadRequest("分组参数不正确")
        };
        return new ListResult<OrderListItem> { Items = await _view.BuildList(q, user), Counts = counts };
    }

    public static DateOnly? ParseDate(string? s, string label)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        throw BizException.BadRequest($"{label}格式应为 yyyy-MM-dd");
    }

    public static TEnum? ParseEnum<TEnum>(string? s, string label) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (Enum.TryParse<TEnum>(s.Trim(), true, out var v) && Enum.IsDefined(v)) return v;
        throw BizException.BadRequest($"{label}不正确");
    }

    private IQueryable<WorkOrder> ApplyKeyword(IQueryable<WorkOrder> q, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return q;
        var k = keyword.Trim();
        return q.Where(o => o.OrderNo.Contains(k) || o.CustomerName.Contains(k) || o.DeviceModel.Contains(k) || o.DeviceSn.Contains(k));
    }

    public async Task<PagedOrders> Query(OrderQuery p, CurrentUser user)
    {
        bool? priceSet = null;
        if (!string.IsNullOrWhiteSpace(p.PriceSet))
        {
            if (!bool.TryParse(p.PriceSet.Trim(), out var ps)) throw BizException.BadRequest("定价筛选不正确");
            priceSet = ps;
        }
        var status = ParseEnum<OrderStatus>(p.Status, "状态");
        var issue = ParseEnum<IssueType>(p.Issue, "异常类型");
        var serviceType = ParseEnum<ServiceType>(p.ServiceType, "维修类型");
        var from = ParseDate(p.From, "开始日期");
        var to = ParseDate(p.To, "结束日期");
        var page = Math.Max(1, p.Page);
        var size = Math.Clamp(p.PageSize, 1, 200);

        var baseQ = ApplyKeyword(_db.Orders.AsQueryable(), p.Keyword);
        // counts 只受 keyword 影响
        var statusCounts = await baseQ.GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var counts = new Dictionary<string, int>
        {
            ["all"] = statusCounts.Where(x => x.Key != OrderStatus.Voided).Sum(x => x.Count)
        };
        foreach (var s in new[] { OrderStatus.Pending, OrderStatus.Accepted, OrderStatus.InProgress, OrderStatus.OnHold, OrderStatus.Submitted, OrderStatus.Closed })
            counts[s.ToString()] = statusCounts.FirstOrDefault(x => x.Key == s)?.Count ?? 0;

        var q = ApplyKeyword(_view.Orders(), p.Keyword);
        q = status == null ? q.Where(o => o.Status != OrderStatus.Voided) : q.Where(o => o.Status == status);
        if (p.AssigneeId != null) q = q.Where(o => o.AssigneeId == p.AssigneeId);
        if (serviceType != null) q = q.Where(o => o.ServiceType == serviceType);
        if (from != null) { var f = from.Value.ToDateTime(TimeOnly.MinValue); q = q.Where(o => o.CreatedAt >= f); }
        if (to != null) { var t = to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(o => o.CreatedAt < t); }
        if (priceSet != null) q = q.Where(o => o.PriceSet == priceSet);
        if (p.PendingRequest == true)
            q = q.Where(o => _db.OrderRequests.Any(r => r.OrderId == o.Id && r.Status == RequestStatus.Pending));

        var orders = await q.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).ToListAsync();
        var items = await _view.BuildList(orders, user);
        if (issue != null) items = items.Where(i => i.Issues.Contains(issue.Value)).ToList();
        return new PagedOrders
        {
            Items = items.Skip((page - 1) * size).Take(size).ToList(),
            Total = items.Count, Page = page, PageSize = size, Counts = counts
        };
    }

    public async Task<List<RecentCustomerDto>> RecentCustomers(CurrentUser user)
    {
        var orders = await _db.Orders.Where(o => o.CreatorId == user.Id)
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(200).ToListAsync();
        return orders.GroupBy(o => o.CustomerName.Trim()).Select(g => g.First()).Take(5).Select(o => new RecentCustomerDto
        {
            CustomerName = o.CustomerName, ContactName = o.ContactName, ContactPhone = o.ContactPhone, Address = o.Address,
            Latitude = o.Latitude ?? 0, Longitude = o.Longitude ?? 0, DeviceModel = o.DeviceModel, DeviceSn = o.DeviceSn,
            SatDate = o.SatDate, SatSigned = o.SatSigned, ProjectNo = o.ProjectNo
        }).ToList();
    }

    public Task<OrderDetail> Detail(int id, CurrentUser user) => _view.BuildDetail(id, user);

    // ---------- 新建 ----------

    private static readonly Regex PhoneRe = new(@"^[0-9\-]{7,20}$", RegexOptions.Compiled);
    private static readonly Regex SatRe = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.Compiled);

    private static string Req(string? v, string label, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(v)) throw BizException.BadRequest($"请填写{label}");
        var s = v.Trim();
        if (s.Length > max) throw BizException.BadRequest($"{label}不能超过 {max} 个字");
        return s;
    }

    private static string? Opt(string? v, string label, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim();
        if (s.Length > max) throw BizException.BadRequest($"{label}不能超过 {max} 个字");
        return s;
    }

    public async Task<OrderDetail> Create(CreateOrderRequest r, CurrentUser user)
    {
        if (r.Source == null) throw BizException.BadRequest("请选择工单来源");
        if (r.ProblemType == null) throw BizException.BadRequest("请选择问题类型");
        if (r.Urgency == null) throw BizException.BadRequest("请选择紧急程度");
        if (r.ServiceType == null) throw BizException.BadRequest("请选择维修类型");
        var customer = Req(r.CustomerName, "客户单位", 100);
        var contact = Req(r.ContactName, "联系人", 50);
        var phone = Req(r.ContactPhone, "联系电话", 40).Replace(" ", "");
        if (!PhoneRe.IsMatch(phone)) throw BizException.BadRequest("联系电话格式不正确");
        var address = Req(r.Address, "客户地址", 200);
        if (r.ServiceType == ServiceType.Onsite && (r.Latitude == null || r.Longitude == null))
            throw BizException.BadRequest("现场工单请在地图上选择客户地址");
        if (r.Latitude is < -90 or > 90 || r.Longitude is < -180 or > 180) throw BizException.BadRequest("经纬度不正确");
        var model = Req(r.DeviceModel, "设备型号", 100);
        var sn = Req(r.DeviceSn, "设备编号", 100);
        var sat = Req(r.SatDate, "购买/SAT 时间", 7);
        if (!SatRe.IsMatch(sat)) throw BizException.BadRequest("购买/SAT 时间格式应为 yyyy-MM");
        if (r.SatSigned == null) throw BizException.BadRequest("请选择 SAT 是否已签字");
        var faultTime = Req(r.FaultTime, "发生时间", 50);
        var symptom = Req(r.Symptom, "故障现象/报错", 1000);
        var tried = Req(r.CustomerTried, "客户已尝试的操作", 500);
        var env = Req(r.SiteEnvironment, "现场环境条件", 500);
        var photoIds = r.PhotoFileIds ?? new List<int>();
        if (photoIds.Distinct().Count() > 9) throw BizException.BadRequest("现场照片/视频最多 9 个");

        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var (city, region) = AddressParser.Parse(address);

        var order = new WorkOrder
        {
            Status = OrderStatus.Pending, Source = r.Source.Value, ProblemType = r.ProblemType.Value, Urgency = r.Urgency.Value,
            ServiceType = r.ServiceType.Value, CustomerName = customer, ContactName = contact, ContactPhone = phone,
            Address = address, City = city, Region = region, Latitude = r.Latitude, Longitude = r.Longitude,
            DeviceModel = model, DeviceSn = sn, SatDate = sat, SatSigned = r.SatSigned.Value, ProjectNo = Opt(r.ProjectNo, "项目编号", 50),
            FaultTime = faultTime, Symptom = symptom, CustomerTried = tried, SiteEnvironment = env,
            CustomerRequest = Opt(r.CustomerRequest, "客户诉求与限制", 500),
            // v1.1：建单不定价（忽略请求里的 price），由主管在详情中定价
            Price = 0, PriceSet = false,
            CreatorId = me.Id, CreatedAt = now,
        };

        // 工单号：事务内取当日最大号；唯一索引冲突时重试
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var prefix = OrderNoGenerator.Prefix(now);
                var max = await _db.Orders.Where(o => o.OrderNo.StartsWith(prefix))
                    .OrderByDescending(o => o.OrderNo).Select(o => o.OrderNo).FirstOrDefaultAsync();
                order.OrderNo = OrderNoGenerator.Next(now, max);
                _db.Orders.Add(order);
                await _db.SaveChangesAsync();
                await _files.Attach(photoIds, order.Id, FileCategory.OrderPhoto, user, "现场照片");
                AddLog(order, me, order.Urgency == Urgency.Urgent ? $"{me.Name} 建单 · 紧急" : $"{me.Name} 建单", OrderStatus.Pending, now);
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
                break;
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                await tx.RollbackAsync();
                _db.ChangeTracker.Clear();
                order.Id = 0;
                order.Logs.Clear();
            }
        }

        if (order.Urgency == Urgency.Urgent)
        {
            await _notifier.NotifyAsync("紧急工单待认领",
                $"**{order.CustomerName}**（{order.OrderNo}）\n\n{order.DeviceModel} · {order.Symptom}\n\n{EnumText.Service(order.ServiceType)} · {order.Region} · 建单人 {me.Name}");
        }
        return await _view.BuildDetail(order.Id, user);
    }

    // ---------- 公共 ----------

    private static readonly OrderStatus[] Active = { OrderStatus.Accepted, OrderStatus.InProgress };

    private async Task<WorkOrder> Load(int id) =>
        await _db.Orders.Include(o => o.Assignee).Include(o => o.Creator)
            .Include(o => o.Assistants).ThenInclude(a => a.Engineer)
            .FirstOrDefaultAsync(o => o.Id == id) ?? throw BizException.NotFound("工单不存在");

    private void AddLog(WorkOrder o, Employee actor, string text, OrderStatus to, DateTime time, LogKind kind = LogKind.Status) =>
        _db.StatusLogs.Add(new StatusLog { Order = o, OrderId = o.Id, ActorId = actor.Id, Text = text, ToStatus = to, Time = time, Kind = kind });

    private async Task<List<string>> SupervisorPhones() =>
        await _db.Employees.Where(e => e.Role == Role.Supervisor && e.Enabled).Select(e => e.Phone).ToListAsync();

    /// <summary>负责人 + 协助人的手机号（通知 @ 用）</summary>
    private static List<string> ParticipantPhones(WorkOrder o)
    {
        var phones = new List<string>();
        if (o.Assignee != null) phones.Add(o.Assignee.Phone);
        phones.AddRange(o.Assistants.Where(a => a.Engineer != null).Select(a => a.Engineer!.Phone));
        return phones;
    }

    /// <summary>本单今天未离场（现场计时中）的到场配对，按到场时间正序</summary>
    private async Task<List<PunchPair>> RunningPairs(int orderId, DateTime now)
    {
        var punches = await _db.Punches.Where(p => p.OrderId == orderId)
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement)).ToListAsync();
        return PunchCalculator.Pair(punches).Where(p => p.IsRunning(now.Date)).OrderBy(p => p.Arrive.Time).ToList();
    }

    /// <summary>
    /// 申请的系统自动撤销（CHANGES-v1.1 §3.2）：在每个会改变工单状态 / 负责人 / 协助人的动作里、修改完成后保存前调用。
    /// 不写状态记录、不通知。
    /// </summary>
    private async Task AutoCancelRequests(WorkOrder o, DateTime now, int? oldLeadId = null, string? holdReply = null)
    {
        var pending = (await _db.OrderRequests.Where(r => r.OrderId == o.Id && r.Status == RequestStatus.Pending).ToListAsync())
            .Where(r => r.Status == RequestStatus.Pending).ToList();
        void Cancel(OrderRequest r, string reply)
        {
            if (r.Status != RequestStatus.Pending) return;
            r.Status = RequestStatus.Cancelled;
            r.DeciderId = null;
            r.DecidedAt = now;
            r.Reply = reply;
        }
        if (oldLeadId != null && oldLeadId != o.AssigneeId)
            foreach (var r in pending.Where(r => r.RequesterId == oldLeadId)) Cancel(r, "负责人已变更");
        if (!Active.Contains(o.Status))
            foreach (var r in pending.Where(r => r.Type == OrderRequestType.Hold)) Cancel(r, holdReply ?? "工单状态已变化");
        if (o.Status is OrderStatus.Submitted or OrderStatus.Closed or OrderStatus.Voided)
            foreach (var r in pending.Where(r => r.Type == OrderRequestType.AddAssistant)) Cancel(r, "工单状态已变化");
        foreach (var r in pending.Where(r => r.Type == OrderRequestType.AddAssistant && r.TargetEngineerId != null
                     && (r.TargetEngineerId == o.AssigneeId || o.Assistants.Any(a => a.EngineerId == r.TargetEngineerId))))
            Cancel(r, "已是本单参与人");
    }

    // ---------- 工程师动作 ----------

    public async Task<OrderDetail> Claim(int id, CurrentUser user)
    {
        var o = await Load(id);
        if (o.Status == OrderStatus.Voided) throw BizException.Conflict("工单已作废");
        if (o.Status != OrderStatus.Pending) throw BizException.Conflict("工单已被他人认领");
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var to = OrderStateMachine.Apply(o.Status, OrderAction.Claim);
        // 条件更新防并发：只有仍为 Pending 才能成功
        var rows = await _db.Orders.Where(x => x.Id == id && x.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, to).SetProperty(x => x.AssigneeId, me.Id).SetProperty(x => x.ClaimedAt, now));
        if (rows == 0) throw BizException.Conflict("工单已被他人认领");
        await _db.Entry(o).ReloadAsync();
        AddLog(o, me, $"{me.Name} 认领 → {EnumText.Status(to)}", to, now);
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(id, user);
    }

    private static void EnsureAssignee(WorkOrder o, CurrentUser user)
    {
        if (o.AssigneeId != user.Id) throw BizException.Forbidden("只有负责工程师可以操作");
    }

    public async Task<OrderDetail> Appointment(int id, AppointmentRequest r, CurrentUser user)
    {
        var o = await Load(id);
        EnsureAssignee(o, user);
        if (o.Status is not (OrderStatus.Accepted or OrderStatus.InProgress))
            throw BizException.Conflict($"工单当前为「{EnumText.Status(o.Status)}」，不能预约");
        if (r.AppointmentTime == null) throw BizException.BadRequest("请选择预约时间");
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var t = r.AppointmentTime.Value;
        o.AppointmentTime = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
        AddLog(o, me, $"{me.Name} 预约 {TimeText.MdHm(o.AppointmentTime.Value)}", o.Status, now);
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(id, user);
    }

    public async Task<OrderDetail> Start(int id, CurrentUser user)
    {
        var o = await Load(id);
        EnsureAssignee(o, user);
        if (o.Status == OrderStatus.OnHold) throw BizException.Conflict("工单已暂停搁置，恢复后才能开始处理");
        if (o.ServiceType == ServiceType.Factory) throw BizException.Conflict("返厂工单记录「工厂收货」后自动开始处理");
        if (o.ServiceType == ServiceType.Onsite) throw BizException.Conflict("现场工单请到场打卡后自动开始");
        var to = OrderStateMachine.Apply(o.Status, OrderAction.Start);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        o.Status = to;
        o.StartedAt ??= now;
        AddLog(o, me, $"{me.Name} 开始处理 → {EnumText.Status(to)}", to, now);
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(id, user);
    }

    public async Task<OrderDetail> Complete(int id, CompleteRequest r, CurrentUser user)
    {
        var o = await Load(id);
        EnsureAssignee(o, user);
        if (o.Status == OrderStatus.OnHold) throw BizException.Conflict("工单已暂停搁置，恢复后才能提交完工");
        if (o.ServiceType == ServiceType.Factory
            && !await _db.FactoryRecords.AnyAsync(x => x.OrderId == id && x.Node == FactoryNode.ShippedBack))
            throw BizException.Conflict("返厂工单请先记录「寄回客户」再提交完工");
        var now = _clock.Now;
        // 现场计时：先判本人（CompletionValidator 抛「请先离场打卡」），再判其他人（协助人或已退出的人，取到场最早的一位）
        var runningPairs = await RunningPairs(id, now);
        var running = runningPairs.Any(p => p.Arrive.EngineerId == user.Id);
        if (o.Status == OrderStatus.InProgress && o.ServiceType == ServiceType.Onsite && !running)
        {
            var other = runningPairs.FirstOrDefault(p => p.Arrive.EngineerId != user.Id);
            if (other != null)
            {
                var name = await _db.Employees.Where(e => e.Id == other.Arrive.EngineerId).Select(e => e.Name).FirstOrDefaultAsync();
                throw BizException.Conflict($"{name}还在现场计时，请其先离场打卡后再提交完工");
            }
        }
        var processIds = (r.ProcessPhotoIds ?? new()).Distinct().ToList();
        var signIds = (r.SignaturePhotoIds ?? new()).Distinct().ToList();
        CompletionValidator.Validate(o.Status, o.ServiceType, running,
            new CompletionInput(r.FaultCause, r.Solution, r.RunStatus, processIds.Count, signIds.Count));
        if (processIds.Count + signIds.Count > 30) throw BizException.BadRequest("照片数量过多");

        // processPhotoIds / signaturePhotoIds 为最终全集：本单已挂载但未在本次列表中的处理照片/签字件解绑
        var keep = processIds.Concat(signIds).ToHashSet();
        var stale = await _db.Files.Where(f => f.OrderId == id
                && (f.Category == FileCategory.ProcessPhoto || f.Category == FileCategory.Signature))
            .ToListAsync();
        foreach (var f in stale.Where(f => !keep.Contains(f.Id))) f.OrderId = null;
        await _files.Attach(processIds, id, FileCategory.ProcessPhoto, user, "处理照片");
        await _files.Attach(signIds, id, FileCategory.Signature, user, "客户签字件");
        var me = await _view.LoadEmployee(user.Id);
        var to = OrderStateMachine.Apply(o.Status, OrderAction.Complete);
        o.Status = to;
        o.FaultCause = Req(r.FaultCause, "故障原因", 1000);
        o.Solution = Req(r.Solution, "解决措施", 1000);
        o.PartsReplaced = Opt(r.PartsReplaced, "更换备件", 500) ?? "";
        o.RunStatus = r.RunStatus;
        o.SubmittedAt = now;
        AddLog(o, me, $"{me.Name} 提交完工 → {EnumText.Status(to)}", to, now);
        await AutoCancelRequests(o, now);
        await _db.SaveChangesAsync();

        await _notifier.NotifyAsync("工单待验收",
            $"{me.Name} 提交了 **{o.CustomerName}**（{o.OrderNo}）的完工资料，请确认闭环。\n\n运行状况：{EnumText.Run(r.RunStatus!.Value)}", await SupervisorPhones());
        return await _view.BuildDetail(id, user);
    }

    // ---------- 主管动作 ----------

    private record AssistantPlan(List<Employee> ToAdd, List<OrderAssistant> ToRemove);

    /// <summary>
    /// 校验协助人最终全集并算出增减（不修改任何数据）。leadId 为（调整后的）负责人。
    /// 新负责人若当前是协助人，不算移除（由调用方转正）。
    /// </summary>
    private async Task<AssistantPlan> PlanAssistants(WorkOrder o, IEnumerable<int> ids, int? leadId, DateTime now)
    {
        var set = ids.Distinct().ToList();
        if (leadId != null && set.Contains(leadId.Value)) throw BizException.BadRequest("协助人不能是负责人");
        var engineers = await _db.Employees.Where(e => set.Contains(e.Id) && e.Role == Role.Engineer && e.Enabled).ToListAsync();
        if (engineers.Count != set.Count) throw BizException.BadRequest("所选协助人不存在或已停用");
        if (set.Count > MaxAssistants) throw BizException.BadRequest("协助人最多 5 人");
        var current = o.Assistants.Where(a => a.EngineerId != leadId).ToList();
        var toRemove = current.Where(a => !set.Contains(a.EngineerId)).OrderBy(a => a.AddedAt).ThenBy(a => a.Id).ToList();
        var toAdd = set.Where(id => current.All(a => a.EngineerId != id)).Select(id => engineers.First(e => e.Id == id)).ToList();
        if (toRemove.Count > 0)
        {
            var running = await RunningPairs(o.Id, now);
            var onSite = toRemove.FirstOrDefault(a => running.Any(p => p.Arrive.EngineerId == a.EngineerId));
            if (onSite != null) throw BizException.Conflict($"{onSite.Engineer?.Name}正在现场计时，请先离场打卡后再移除");
        }
        return new AssistantPlan(toAdd, toRemove);
    }

    public const int MaxAssistants = 5;

    /// <summary>执行协助人增减并写状态记录（先添加后移除）</summary>
    private void ApplyAssistants(WorkOrder o, AssistantPlan plan, Employee me, DateTime now)
    {
        foreach (var e in plan.ToAdd)
            o.Assistants.Add(new OrderAssistant { OrderId = o.Id, EngineerId = e.Id, Engineer = e, AddedById = me.Id, AddedAt = now });
        foreach (var a in plan.ToRemove)
        {
            o.Assistants.Remove(a);
            _db.OrderAssistants.Remove(a);
        }
        if (plan.ToAdd.Count > 0)
            AddLog(o, me, $"{me.Name} 添加协助人 {string.Join("、", plan.ToAdd.Select(e => e.Name))}", o.Status, now, LogKind.Assist);
        if (plan.ToRemove.Count > 0)
            AddLog(o, me, $"{me.Name} 移除协助人 {string.Join("、", plan.ToRemove.Select(a => a.Engineer?.Name))}", o.Status, now, LogKind.Assist);
    }

    private async Task NotifyNewAssistants(WorkOrder o, Employee me, List<Employee> added)
    {
        if (added.Count == 0) return;
        await _notifier.NotifyAsync("工单协助提醒",
            $"{me.Name} 安排你协助 **{o.CustomerName}**（{o.OrderNo}），负责人 {o.Assignee?.Name}。\n\n{o.DeviceModel} · {o.Symptom}",
            added.Select(e => e.Phone).ToList());
    }

    public async Task<OrderDetail> Assign(int id, AssignRequest r, CurrentUser user)
    {
        if (r.EngineerId == null) throw BizException.BadRequest("请选择工程师");
        var o = await Load(id);
        var eng = await _db.Employees.FirstOrDefaultAsync(e => e.Id == r.EngineerId && e.Role == Role.Engineer && e.Enabled)
                  ?? throw BizException.BadRequest("所选工程师不存在或已停用");
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var oldLeadId = o.AssigneeId;
        var oldName = o.Assignee?.Name ?? "";
        var assign = o.Status == OrderStatus.Pending;
        var to = OrderStateMachine.Apply(o.Status, assign ? OrderAction.Assign : OrderAction.Reassign);
        if (!assign)
        {
            if (o.AssigneeId == eng.Id) throw BizException.Conflict($"该工单已由{eng.Name}负责");
            // 原负责人今天在本单仍在现场计时，不允许改派
            await EnsureAssigneeNotOnSite(o, now, "原负责工程师正在现场计时，请先离场打卡后再改派");
        }
        // 协助人：先校验，全部通过后再修改
        var plan = r.AssistantIds == null ? null : await PlanAssistants(o, r.AssistantIds, eng.Id, now);

        o.Status = to;
        o.AssigneeId = eng.Id;
        o.Assignee = eng;
        o.ClaimedAt = now;
        AddLog(o, me, assign
            ? $"{me.Name} 指派给 {eng.Name} → {EnumText.Status(to)}"
            : $"{me.Name} 改派给 {eng.Name}" + (oldName.Length > 0 ? $"（原 {oldName}）" : ""), to, now);
        // 新负责人若当前是协助人：转正并从协助人中移除（不算移除，不写记录）
        var promoted = o.Assistants.FirstOrDefault(a => a.EngineerId == eng.Id);
        if (promoted != null)
        {
            o.Assistants.Remove(promoted);
            _db.OrderAssistants.Remove(promoted);
        }
        if (plan != null) ApplyAssistants(o, plan, me, now);
        await AutoCancelRequests(o, now, oldLeadId);
        await _db.SaveChangesAsync();
        await _notifier.NotifyAsync("工单指派提醒",
            $"{me.Name} 将 **{o.CustomerName}**（{o.OrderNo}）指派给 {eng.Name}。\n\n{o.DeviceModel} · {o.Symptom}", new[] { eng.Phone });
        if (plan != null) await NotifyNewAssistants(o, me, plan.ToAdd);
        return await _view.BuildDetail(id, user);
    }

    /// <summary>主管调整协助人（最终全集）</summary>
    public async Task<OrderDetail> SetAssistants(int id, AssistantsRequest r, CurrentUser user)
    {
        EnsureRole(user, Role.Supervisor, "只有主管可以调整协助人");
        if (r.EngineerIds == null) throw BizException.BadRequest("请选择协助人");
        var o = await Load(id);
        if (!Doing.Contains(o.Status))
            throw BizException.Conflict($"工单当前为「{EnumText.Status(o.Status)}」，不能调整协助人");
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var plan = await PlanAssistants(o, r.EngineerIds, o.AssigneeId, now);
        if (plan.ToAdd.Count > 0 || plan.ToRemove.Count > 0)
        {
            ApplyAssistants(o, plan, me, now);
            await AutoCancelRequests(o, now);
            await _db.SaveChangesAsync();
            await NotifyNewAssistants(o, me, plan.ToAdd);
        }
        return await _view.BuildDetail(id, user);
    }

    private static void EnsureRole(CurrentUser user, Role role, string message)
    {
        if (user.Role != role) throw BizException.Forbidden(message);
    }

    public async Task<OrderDetail> Void(int id, ReasonRequest r, CurrentUser user)
    {
        var o = await Load(id);
        var to = OrderStateMachine.Apply(o.Status, OrderAction.Void);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var reason = Opt(r.Reason, "作废原因", 200);
        o.Status = to;
        o.VoidReason = reason;
        AddLog(o, me, $"{me.Name} 作废 → {EnumText.Status(to)}" + (reason == null ? "" : $"（{reason}）"), to, now);
        await AutoCancelRequests(o, now);
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(id, user);
    }

    /// <summary>负责工程师今天在本单有未离场的到场卡 → 40900</summary>
    private async Task EnsureAssigneeNotOnSite(WorkOrder o, DateTime now, string message)
    {
        if (o.AssigneeId == null) return;
        if ((await RunningPairs(o.Id, now)).Any(p => p.Arrive.EngineerId == o.AssigneeId))
            throw BizException.Conflict(message);
    }

    public async Task<OrderDetail> Reject(int id, ReasonRequest r, CurrentUser user)
    {
        var o = await Load(id);
        var to = OrderStateMachine.Apply(o.Status, OrderAction.Reject);
        var reason = Opt(r.Reason, "退回原因", 200);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        o.Status = to;
        o.SubmittedAt = null;
        AddLog(o, me, $"{me.Name} 退回 → {EnumText.Status(to)}" + (reason == null ? "" : $"（{reason}）"), to, now);
        await _db.SaveChangesAsync();
        if (o.Assignee != null)
            await _notifier.NotifyAsync("工单被退回", $"**{o.CustomerName}**（{o.OrderNo}）被退回处理中" + (reason == null ? "" : $"：{reason}"), new[] { o.Assignee.Phone });
        return await _view.BuildDetail(id, user);
    }

    public async Task<OrderDetail> Close(int id, CurrentUser user)
    {
        var o = await Load(id);
        var to = OrderStateMachine.Apply(o.Status, OrderAction.Close);
        var now = _clock.Now;
        // 缺卡判断覆盖本单所有人的打卡（含协助人、已退出的人）
        var punches = await _db.Punches.Where(p => p.OrderId == id)
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement)).ToListAsync();
        if (PunchCalculator.Issues(punches, now.Date).Contains(IssueType.MissingPunch))
            throw BizException.Conflict("请先补录缺卡");
        if (!o.PriceSet) throw BizException.Conflict("请先设置服务价格（保内填 0）");
        var me = await _view.LoadEmployee(user.Id);
        o.Status = to;
        o.ClosedAt = now;
        AddLog(o, me, $"{me.Name} 确认闭环 → {EnumText.Status(to)}", to, now);
        await AutoCancelRequests(o, now);
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(id, user);
    }

    // ---------- 价格（v1.1）----------

    private const decimal MaxPrice = 100_000_000;

    /// <summary>主管定价 / 改价（0 = 保内免费）</summary>
    public async Task<OrderDetail> SetPrice(int id, PriceRequest r, CurrentUser user)
    {
        EnsureRole(user, Role.Supervisor, "只有主管可以定价");
        if (r.Price == null) throw BizException.BadRequest("请填写服务价格（保内填 0）");
        if (r.Price < 0) throw BizException.BadRequest("服务价格不能小于 0");
        if (r.Price > MaxPrice) throw BizException.BadRequest("服务价格过大");
        var price = Math.Round(r.Price.Value, 2, MidpointRounding.AwayFromZero);
        var o = await Load(id);
        if (o.Status == OrderStatus.Voided) throw BizException.Conflict("工单已作废，不能定价");
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        if (!o.PriceSet)
            AddLog(o, me, $"{me.Name} 定价 {EnumText.Money(price)}", o.Status, now, LogKind.Price);
        else if (o.Price != price)
            AddLog(o, me, $"{me.Name} 改价 {EnumText.Money(o.Price)} → {EnumText.Money(price)}", o.Status, now, LogKind.Price);
        o.Price = price;
        o.PriceSet = true;
        o.PriceSetAt = now;
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(id, user);
    }

    /// <summary>销售提交价格建议（任何销售都可以，不限建单人）</summary>
    public async Task<OrderDetail> SuggestPrice(int id, PriceSuggestRequest r, CurrentUser user)
    {
        EnsureRole(user, Role.Sales, "只有销售可以提交价格建议");
        if (r.Amount == null) throw BizException.BadRequest("请填写建议价格");
        if (r.Amount < 0) throw BizException.BadRequest("建议价格不能小于 0");
        if (r.Amount > MaxPrice) throw BizException.BadRequest("建议价格过大");
        var note = Opt(r.Note, "备注", 200);
        var amount = Math.Round(r.Amount.Value, 2, MidpointRounding.AwayFromZero);
        var o = await Load(id);
        if (o.Status is OrderStatus.Closed or OrderStatus.Voided)
            throw BizException.Conflict($"工单当前为「{EnumText.Status(o.Status)}」，不能提交价格建议");
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        _db.PriceSuggestions.Add(new PriceSuggestion { OrderId = o.Id, SalesId = me.Id, Amount = amount, Note = note, CreatedAt = now });
        AddLog(o, me, $"{me.Name} 建议价格 {EnumText.Money(amount)}" + (note == null ? "" : $"（{note}）"), o.Status, now, LogKind.Price);
        await _db.SaveChangesAsync();
        // 正文不得出现金额（群里有工程师）
        await _notifier.NotifyAsync("价格建议",
            $"{me.Name} 对 **{o.CustomerName}**（{o.OrderNo}）提交了价格建议，请在工单详情中查看并定价。", await SupervisorPhones());
        return await _view.BuildDetail(id, user);
    }
}
