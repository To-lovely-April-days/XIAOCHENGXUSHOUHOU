using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

public class AdminService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly OrderViewBuilder _view;
    private readonly FileService _files;

    public AdminService(AppDbContext db, IClock clock, OrderViewBuilder view, FileService files)
    {
        _db = db;
        _clock = clock;
        _view = view;
        _files = files;
    }

    private static PunchInfo Info(Punch p) => PunchInfo.From(p);

    public async Task<OverviewDto> Overview()
    {
        var now = _clock.Now;
        var today = now.Date;
        var weekStart = TimeText.WeekStart(today);
        var orders = await _db.Orders.Select(o => new { o.Status, o.Urgency, o.ClosedAt }).ToListAsync();
        var punches = await _db.Punches.Where(p => p.Time >= weekStart)
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement)).ToListAsync();
        var pairs = PunchCalculator.Pair(punches).Where(p => p.Date >= weekStart).ToList();
        // 本周工时 = 已配对 + 今日正在计时
        var running = pairs.Where(p => p.IsRunning(today)).Sum(p => PunchCalculator.DiffMinutes(p.Arrive.Time, now));
        return new OverviewDto
        {
            Pending = orders.Count(o => o.Status == OrderStatus.Pending),
            UrgentPending = orders.Count(o => o.Status == OrderStatus.Pending && o.Urgency == Urgency.Urgent),
            InProgress = orders.Count(o => o.Status == OrderStatus.InProgress),
            OnSiteEngineers = pairs.Where(p => p.IsRunning(today)).Select(p => p.Arrive.EngineerId).Distinct().Count(),
            Submitted = orders.Count(o => o.Status == OrderStatus.Submitted),
            ClosedThisWeek = orders.Count(o => o.Status == OrderStatus.Closed && o.ClosedAt >= weekStart),
            WeekMinutes = pairs.Sum(p => p.Minutes) + running,
            UpdatedAt = now
        };
    }

    public async Task<OrderDetail> Supplement(SupplementRequest r, CurrentUser user)
    {
        if (r.OrderId == null) throw BizException.BadRequest("缺少工单");
        if (r.ArrivePunchId == null) throw BizException.BadRequest("缺少对应的到场打卡");
        if (r.Kind == null) throw BizException.BadRequest("缺少补录类型");
        if (r.Kind != PunchKind.Leave) throw BizException.BadRequest("只能补录缺失的离场卡");
        if (r.Time == null) throw BizException.BadRequest("请填写补录时间");
        if (string.IsNullOrWhiteSpace(r.Reason)) throw BizException.BadRequest("请填写补录原因");
        var reason = r.Reason.Trim();
        if (reason.Length > 200) throw BizException.BadRequest("补录原因不能超过 200 个字");

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == r.OrderId) ?? throw BizException.NotFound("工单不存在");
        var arrive = await _db.Punches.FirstOrDefaultAsync(p => p.Id == r.ArrivePunchId && p.OrderId == order.Id)
                     ?? throw BizException.NotFound("未找到对应的到场打卡");
        var now = _clock.Now;
        var t = r.Time.Value;
        var leaveTime = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);
        var engineerPunches = await _db.Punches.Where(p => p.EngineerId == arrive.EngineerId).ToListAsync();
        var err = PunchCalculator.ValidateSupplementLeave(engineerPunches.Select(Info), arrive.Id, leaveTime, now);
        if (err != null) throw BizException.BadRequest(err);

        var me = await _view.LoadEmployee(user.Id);
        _db.Punches.Add(new Punch
        {
            OrderId = order.Id, EngineerId = arrive.EngineerId, Kind = PunchKind.Leave, Time = leaveTime,
            Address = "", IsSupplement = true, SupplementById = me.Id, SupplementReason = reason, SupplementAt = now
        });
        _db.StatusLogs.Add(new StatusLog
        {
            OrderId = order.Id, ActorId = me.Id, Text = $"{me.Name} 补录离场 {TimeText.Hm(leaveTime)}", Time = now, ToStatus = order.Status
        });
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(order.Id, user);
    }

    // ---------- 导出 ----------

    public async Task<byte[]> ExportOrders(string? from, string? to, string? status)
    {
        var f = OrderService.ParseDate(from, "开始日期");
        var t = OrderService.ParseDate(to, "结束日期");
        var st = OrderService.ParseEnum<OrderStatus>(status, "状态");
        var q = _db.Orders.Include(o => o.Assignee).Include(o => o.Creator).AsQueryable();
        if (f != null) { var x = f.Value.ToDateTime(TimeOnly.MinValue); q = q.Where(o => o.CreatedAt >= x); }
        if (t != null) { var x = t.Value.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(o => o.CreatedAt < x); }
        q = st == null ? q.Where(o => o.Status != OrderStatus.Voided) : q.Where(o => o.Status == st);
        var orders = await q.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).ToListAsync();
        var map = await _view.PunchMap(orders.Select(o => o.Id));
        var sigOrders = (await _db.Files.Where(x => x.Category == FileCategory.Signature && x.OrderId != null).Select(x => x.OrderId!.Value).ToListAsync()).ToHashSet();

        string[] headers =
        {
            "项目编号", "工单号", "客户全称", "设备型号", "设备编号", "产品阶段", "故障描述", "故障发生时间", "报修人", "报修电话",
            "维修类型", "处理人", "处理时间(首次到场)", "解决措施", "进度状态", "是否收费", "服务价格", "现场工时(小时)",
            "验收结果(运行状况)", "客户签字(有/无)", "建单人", "建单时间"
        };
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("售后进度台账");
        WriteHeader(ws, headers);
        var row = 2;
        foreach (var o in orders)
        {
            var punches = map.GetValueOrDefault(o.Id) ?? new();
            var firstArrive = punches.Where(p => p.Kind == PunchKind.Arrive).Select(p => (DateTime?)p.Time).Min();
            var handleTime = firstArrive ?? o.StartedAt;
            var minutes = PunchCalculator.Pair(punches).Sum(p => p.Minutes);
            object?[] values =
            {
                o.ProjectNo ?? "", o.OrderNo, o.CustomerName, o.DeviceModel, o.DeviceSn, o.SatSigned ? "老产品" : "新产品",
                o.Symptom, o.FaultTime, o.ContactName, o.ContactPhone, EnumText.Service(o.ServiceType), o.Assignee?.Name ?? "",
                handleTime == null ? "" : handleTime.Value.ToString("yyyy-MM-dd HH:mm"), o.Solution ?? "", EnumText.Status(o.Status),
                o.Price > 0 ? "是" : "否（保内）", (double)o.Price, TimeText.Hours1(minutes),
                o.RunStatus == null ? "" : EnumText.Run(o.RunStatus.Value), sigOrders.Contains(o.Id) ? "有" : "无",
                o.Creator?.Name ?? "", o.CreatedAt.ToString("yyyy-MM-dd HH:mm")
            };
            WriteRow(ws, row++, values);
        }
        ws.Column(17).Style.NumberFormat.Format = "#,##0.00";
        ws.Column(18).Style.NumberFormat.Format = "0.0";
        return Save(wb, ws);
    }

    public async Task<byte[]> ExportHours(int year, int month)
    {
        if (year < 2000 || year > 2100 || month < 1 || month > 12) throw BizException.BadRequest("年月不正确");
        var today = _clock.Now.Date;
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);
        var punches = await _db.Punches.Include(p => p.Engineer).Where(p => p.Time >= start && p.Time < end.AddDays(1)).ToListAsync();
        var byId = punches.ToDictionary(p => p.Id);
        var pairs = PunchCalculator.Pair(punches.Select(Info)).Where(p => p.Date >= start && p.Date < end)
            .OrderBy(p => byId[p.Arrive.Id].Engineer?.Name).ThenBy(p => p.Arrive.Time).ToList();
        var orderIds = pairs.Select(p => p.Arrive.OrderId).Distinct().ToList();
        var orders = await _db.Orders.Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id);

        string[] headers = { "工程师", "日期", "工单号", "客户", "到场", "离场", "工时(小时)", "是否补录", "补录原因" };
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet($"{year}年{month}月工时");
        WriteHeader(ws, headers);
        var row = 2;
        foreach (var p in pairs)
        {
            var a = byId[p.Arrive.Id];
            var l = p.Leave == null ? null : byId[p.Leave.Id];
            var o = orders.GetValueOrDefault(p.Arrive.OrderId);
            var reason = l?.IsSupplement == true ? l.SupplementReason : a.IsSupplement ? a.SupplementReason : null;
            object?[] values =
            {
                a.Engineer?.Name ?? "", p.Date.ToString("yyyy-MM-dd"), o?.OrderNo ?? "", o?.CustomerName ?? "",
                a.Time.ToString("HH:mm"),
                l != null ? l.Time.ToString("HH:mm") : p.IsRunning(today) ? "计时中" : "缺卡",
                TimeText.Hours1(p.Minutes), p.IsSupplement ? "主管补录" : "否", reason ?? ""
            };
            WriteRow(ws, row++, values);
        }
        ws.Column(7).Style.NumberFormat.Format = "0.0";
        return Save(wb, ws);
    }

    private static void WriteHeader(IXLWorksheet ws, string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.FromHtml("#F0F0EE");
        }
        ws.SheetView.FreezeRows(1);
    }

    private static void WriteRow(IXLWorksheet ws, int row, object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var c = ws.Cell(row, i + 1);
            c.Value = values[i] switch
            {
                null => Blank.Value,
                double d => d,
                int n => n,
                _ => values[i]!.ToString()
            };
        }
    }

    private static byte[] Save(XLWorkbook wb, IXLWorksheet ws)
    {
        ws.Columns().AdjustToContents(1, 200, 8, 60);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ---------- 账号管理 ----------

    private static readonly Regex MobileRe = new(@"^1\d{10}$", RegexOptions.Compiled);

    public EmployeeDto ToDto(Employee e) => new()
    {
        Id = e.Id, Name = e.Name, Phone = e.Phone, Role = e.Role, RoleName = EnumText.RoleName(e.Role), Department = e.Department,
        Enabled = e.Enabled, Bound = !string.IsNullOrEmpty(e.OpenId), AvatarUrl = e.AvatarFile == null ? null : _files.Url(e.AvatarFile),
        HasPassword = !string.IsNullOrEmpty(e.PasswordHash)
    };

    public async Task<List<EmployeeDto>> Employees() =>
        (await _db.Employees.Include(e => e.AvatarFile).OrderBy(e => e.Role).ThenBy(e => e.Id).ToListAsync()).Select(ToDto).ToList();

    private async Task<(string Name, string Phone, Role Role, string Dept)> ValidateEmployee(EmployeeCreateRequest r, int? selfId)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw BizException.BadRequest("请填写姓名");
        var name = r.Name.Trim();
        if (name.Length > 20) throw BizException.BadRequest("姓名不能超过 20 个字");
        var phone = AuthService.NormalizePhone(r.Phone ?? "");
        if (!MobileRe.IsMatch(phone)) throw BizException.BadRequest("请填写 11 位手机号");
        if (r.Role == null) throw BizException.BadRequest("请选择角色");
        var dept = (r.Department ?? "").Trim();
        if (dept.Length > 50) throw BizException.BadRequest("部门不能超过 50 个字");
        if (await _db.Employees.AnyAsync(e => e.Phone == phone && e.Id != selfId))
            throw BizException.Conflict("该手机号已登记");
        return (name, phone, r.Role.Value, dept);
    }

    public async Task<EmployeeDto> CreateEmployee(EmployeeCreateRequest r)
    {
        var v = await ValidateEmployee(r, null);
        var e = new Employee { Name = v.Name, Phone = v.Phone, Role = v.Role, Department = v.Dept, Enabled = true, CreatedAt = _clock.Now };
        _db.Employees.Add(e);
        await _db.SaveChangesAsync();
        return ToDto(e);
    }

    public async Task<EmployeeDto> UpdateEmployee(int id, EmployeeUpdateRequest r, CurrentUser user)
    {
        var e = await _db.Employees.Include(x => x.AvatarFile).FirstOrDefaultAsync(x => x.Id == id) ?? throw BizException.NotFound("员工不存在");
        var v = await ValidateEmployee(r, id);
        if (id == user.Id && (r.Enabled == false || v.Role != Role.Supervisor))
            throw BizException.Conflict("不能停用自己或修改自己的角色");
        var revoke = e.Phone != v.Phone || e.Role != v.Role || (r.Enabled == false && e.Enabled);
        if (e.Phone != v.Phone) e.OpenId = null; // 手机号变更需重新绑定
        if (revoke) e.TokenVersion++;             // 旧登录令牌失效
        e.Name = v.Name;
        e.Phone = v.Phone;
        e.Role = v.Role;
        e.Department = v.Dept;
        if (r.Enabled != null) e.Enabled = r.Enabled.Value;
        await _db.SaveChangesAsync();
        return ToDto(e);
    }

    public async Task<EmployeeDto> Unbind(int id)
    {
        var e = await _db.Employees.Include(x => x.AvatarFile).FirstOrDefaultAsync(x => x.Id == id) ?? throw BizException.NotFound("员工不存在");
        e.OpenId = null;
        e.TokenVersion++; // 解绑后旧登录令牌失效
        await _db.SaveChangesAsync();
        return ToDto(e);
    }
}
