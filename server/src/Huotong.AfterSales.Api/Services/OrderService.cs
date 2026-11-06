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
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class OrderService
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

    public async Task<ListResult<OrderListItem>> Hall(string? filter)
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
        return new ListResult<OrderListItem> { Items = await _view.BuildList(q), Counts = counts };
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
            .Where(o => o.AssigneeId == user.Id && o.Status != OrderStatus.Voided && o.Status != OrderStatus.Pending)
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
        return new ListResult<OrderListItem> { Items = await _view.BuildList(q), Counts = counts };
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
        return new ListResult<OrderListItem> { Items = await _view.BuildList(q), Counts = counts };
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

    public async Task<PagedOrders> Query(OrderQuery p)
    {
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

        var orders = await q.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).ToListAsync();
        var items = await _view.BuildList(orders);
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
        if (r.Price == null) throw BizException.BadRequest("请填写服务价格（保内填 0）");
        if (r.Price < 0) throw BizException.BadRequest("服务价格不能小于 0");
        if (r.Price > 100_000_000) throw BizException.BadRequest("服务价格过大");
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
            CustomerRequest = Opt(r.CustomerRequest, "客户诉求与限制", 500), Price = Math.Round(r.Price.Value, 2),
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

    // ---------- 工程师动作 ----------

    private async Task<WorkOrder> Load(int id) =>
        await _db.Orders.Include(o => o.Assignee).FirstOrDefaultAsync(o => o.Id == id) ?? throw BizException.NotFound("工单不存在");

    private void AddLog(WorkOrder o, Employee actor, string text, OrderStatus to, DateTime time) =>
        _db.StatusLogs.Add(new StatusLog { Order = o, OrderId = o.Id, ActorId = actor.Id, Text = text, ToStatus = to, Time = time });

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
        var now = _clock.Now;
        var punches = await _db.Punches.Where(p => p.OrderId == id)
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement)).ToListAsync();
        var running = PunchCalculator.Pair(punches).Any(p => p.IsRunning(now.Date));
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
        await _db.SaveChangesAsync();

        var supervisors = await _db.Employees.Where(e => e.Role == Role.Supervisor && e.Enabled).Select(e => e.Phone).ToListAsync();
        await _notifier.NotifyAsync("工单待验收",
            $"{me.Name} 提交了 **{o.CustomerName}**（{o.OrderNo}）的完工资料，请确认闭环。\n\n运行状况：{EnumText.Run(r.RunStatus!.Value)}", supervisors);
        return await _view.BuildDetail(id, user);
    }

    // ---------- 主管动作 ----------

    public async Task<OrderDetail> Assign(int id, AssignRequest r, CurrentUser user)
    {
        if (r.EngineerId == null) throw BizException.BadRequest("请选择工程师");
        var o = await Load(id);
        var eng = await _db.Employees.FirstOrDefaultAsync(e => e.Id == r.EngineerId && e.Role == Role.Engineer && e.Enabled)
                  ?? throw BizException.BadRequest("所选工程师不存在或已停用");
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        if (o.Status == OrderStatus.Pending)
        {
            var to = OrderStateMachine.Apply(o.Status, OrderAction.Assign);
            o.Status = to;
            o.AssigneeId = eng.Id;
            o.ClaimedAt = now;
            AddLog(o, me, $"{me.Name} 指派给 {eng.Name} → {EnumText.Status(to)}", to, now);
        }
        else
        {
            var to = OrderStateMachine.Apply(o.Status, OrderAction.Reassign);
            if (o.AssigneeId == eng.Id) throw BizException.Conflict($"该工单已由{eng.Name}负责");
            // 原负责人今天在本单仍在现场计时，不允许改派
            await EnsureAssigneeNotOnSite(o, now, "原负责工程师正在现场计时，请先离场打卡后再改派");
            var oldName = o.Assignee?.Name ?? "";
            o.AssigneeId = eng.Id;
            o.ClaimedAt = now;
            AddLog(o, me, $"{me.Name} 改派给 {eng.Name}" + (oldName.Length > 0 ? $"（原 {oldName}）" : ""), to, now);
        }
        await _db.SaveChangesAsync();
        await _notifier.NotifyAsync("工单指派提醒",
            $"{me.Name} 将 **{o.CustomerName}**（{o.OrderNo}）指派给 {eng.Name}。\n\n{o.DeviceModel} · {o.Symptom}", new[] { eng.Phone });
        return await _view.BuildDetail(id, user);
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
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(id, user);
    }

    public async Task<OrderDetail> Hold(int id, ReasonRequest r, CurrentUser user)
    {
        var o = await Load(id);
        var to = OrderStateMachine.Apply(o.Status, OrderAction.Hold);
        var reason = Req(r.Reason, "搁置原因", 200);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        // 工程师今天在本单仍在现场计时，搁置后将无法离场打卡
        await EnsureAssigneeNotOnSite(o, now, "工程师正在现场计时，请先离场打卡后再搁置");
        o.Status = to;
        o.HoldReason = reason;
        AddLog(o, me, $"{me.Name} 标为暂停搁置 → {EnumText.Status(to)}（{reason}）", to, now);
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(id, user);
    }

    /// <summary>负责工程师今天在本单有未离场的到场卡 → 40900</summary>
    private async Task EnsureAssigneeNotOnSite(WorkOrder o, DateTime now, string message)
    {
        if (o.AssigneeId == null) return;
        var punches = await _db.Punches.Where(p => p.OrderId == o.Id && p.EngineerId == o.AssigneeId)
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement)).ToListAsync();
        if (PunchCalculator.Pair(punches).Any(p => p.IsRunning(now.Date)))
            throw BizException.Conflict(message);
    }

    public async Task<OrderDetail> Resume(int id, CurrentUser user)
    {
        var o = await Load(id);
        var to = OrderStateMachine.Apply(o.Status, OrderAction.Resume);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        o.Status = to;
        o.HoldReason = null;
        AddLog(o, me, $"{me.Name} 恢复处理 → {EnumText.Status(to)}", to, now);
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(id, user);
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
        var punches = await _db.Punches.Where(p => p.OrderId == id)
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement)).ToListAsync();
        if (PunchCalculator.Issues(punches, now.Date).Contains(IssueType.MissingPunch))
            throw BizException.Conflict("请先补录缺卡");
        var me = await _view.LoadEmployee(user.Id);
        o.Status = to;
        o.ClosedAt = now;
        AddLog(o, me, $"{me.Name} 确认闭环 → {EnumText.Status(to)}", to, now);
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(id, user);
    }
}
