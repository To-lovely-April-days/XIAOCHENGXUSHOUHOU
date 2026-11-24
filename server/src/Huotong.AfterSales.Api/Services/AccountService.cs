using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

/// <summary>注册限流（内存）：同一个微信一天最多提交 3 次注册申请</summary>
public class RegisterThrottle
{
    public const int MaxPerDay = 3;
    private readonly ConcurrentDictionary<string, (DateTime Day, int Count)> _map = new();
    private readonly IClock _clock;

    public RegisterThrottle(IClock clock) => _clock = clock;

    /// <summary>记一次提交；超过当天上限返回 false（不计数）</summary>
    public bool TryUse(string key)
    {
        var today = _clock.Now.Date;
        var ok = true;
        _map.AddOrUpdate(key, _ => (today, 1), (_, old) =>
        {
            if (old.Day != today) return (today, 1);
            if (old.Count >= MaxPerDay) { ok = false; return old; }
            return (today, old.Count + 1);
        });
        return ok;
    }
}

/// <summary>
/// v1.5 账号：小程序注册申请、账号密码登录、主管审批（docs/CHANGES-v1.5.md）。
/// 注册申请单独放 Registrations 表，审批通过才生成 Employee —— 没通过的人不会出现在任何员工列表里。
/// </summary>
public class AccountService
{
    /// <summary>登录时账号还在审批中</summary>
    public const int CodePending = 40310;
    /// <summary>登录时账号的注册申请已被驳回</summary>
    public const int CodeRejected = 40311;
    public const string PendingText = "账号正在审核中，请联系管理员";
    public const string SubmittedText = "已提交，等待管理员审批";
    /// <summary>排队中的申请总数上限，防止公开的注册入口被刷</summary>
    public const int MaxPending = 200;

    private static readonly Regex MobileRe = new(@"^1\d{10}$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly IWeChatService _wx;
    private readonly TokenService _tokens;
    private readonly OrderViewBuilder _view;
    private readonly LoginThrottle _throttle;
    private readonly RegisterThrottle _registerThrottle;
    private readonly IClock _clock;
    private readonly ILogger<AccountService> _log;
    private readonly ISubscribeNotifier _subscribe;

    public AccountService(AppDbContext db, IWeChatService wx, TokenService tokens, OrderViewBuilder view,
        LoginThrottle throttle, RegisterThrottle registerThrottle, IClock clock, ILogger<AccountService> log,
        ISubscribeNotifier? subscribe = null)
    {
        _subscribe = subscribe ?? NullSubscribeNotifier.Instance;
        _db = db;
        _wx = wx;
        _tokens = tokens;
        _view = view;
        _throttle = throttle;
        _registerThrottle = registerThrottle;
        _clock = clock;
        _log = log;
    }

    // ---------- 密码哈希（与 Employee 共用一种算法；PasswordHasher 不使用 user 参数） ----------

    private static string Hash(string password) => PasswordPolicy.Hash(new Employee(), password);

    private static bool Verify(Registration r, string password) =>
        PasswordPolicy.Verify(new Employee { PasswordHash = r.PasswordHash }, password, out _);

    // ---------- 注册 ----------

    public async Task<RegisterResult> Register(RegisterRequest req)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) throw BizException.BadRequest("请填写姓名");
        if (name.Length > 20) throw BizException.BadRequest("姓名不能超过 20 个字");
        var phone = AuthService.NormalizePhone(req.Phone ?? "");
        if (!MobileRe.IsMatch(phone)) throw BizException.BadRequest("请填写 11 位手机号");
        PasswordPolicy.Ensure(req.Password);
        if (string.IsNullOrWhiteSpace(req.Code)) throw BizException.BadRequest("请在微信小程序内注册");
        var openId = await _wx.Code2Session(req.Code.Trim());

        if (await _db.Employees.AnyAsync(e => e.Phone == phone))
            throw BizException.Conflict("该手机号已有账号，请直接登录");
        var reg = await _db.Registrations.FirstOrDefaultAsync(r => r.Phone == phone);
        if (reg != null && reg.Status == RegistrationStatus.Pending)
            throw BizException.Conflict("该手机号已提交注册，正在等待审批");
        if (await _db.Registrations.CountAsync(r => r.Status == RegistrationStatus.Pending) >= MaxPending)
            throw BizException.BadRequest("当前待审批的注册申请过多，请联系管理员");
        if (!_registerThrottle.TryUse(openId))
            throw BizException.BadRequest("今天提交注册的次数过多，请明天再试");

        var now = _clock.Now;
        if (reg == null)
        {
            reg = new Registration { Phone = phone, CreatedAt = now };
            _db.Registrations.Add(reg);
        }
        // 被驳回后重新提交 / 原账号换了手机号后旧号码再次注册：复用这一行
        reg.Name = name;
        reg.PasswordHash = Hash(req.Password!);
        reg.OpenId = openId;
        reg.Status = RegistrationStatus.Pending;
        reg.RejectReason = null;
        reg.ReviewedById = null;
        reg.ReviewedAt = null;
        reg.EmployeeId = null;
        reg.SubmittedAt = now;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // 同一手机号并发提交撞唯一索引
            throw BizException.Conflict("该手机号已提交注册，正在等待审批");
        }
        await NotifyApprovers(reg);
        return new RegisterResult { Status = RegistrationStatus.Pending, Message = SubmittedText };
    }

    /// <summary>v1.8 有新的注册申请：提醒开了「注册审批权限」的人（只有管理员权限、没单独开的人不提醒）</summary>
    private async Task NotifyApprovers(Registration reg)
    {
        try
        {
            if (_subscribe.RegisterTemplateIds.Count == 0) return;
            var to = await _db.Employees.AsNoTracking().Where(e => e.Enabled && e.CanApprove && e.OpenId != null).ToListAsync();
            if (to.Count == 0) return;
            await _subscribe.NotifyRegistrationAsync(to, new SubscribeSlots(
                Title: "新的注册申请", OrderNo: reg.Phone, Customer: reg.Name,
                Content: $"{reg.Name} 申请注册账号，请审批", Time: reg.SubmittedAt, Operator: reg.Name, Status: "待审批", Device: ""));
        }
        catch (Exception ex)
        {
            _log.LogWarning("注册申请提醒发送失败（不影响注册）：{Message}", ex.Message);
        }
    }

    /// <summary>v1.8 申请人提交注册时要申请订阅的「审批结果」模板（公开，登录前就要用）</summary>
    public IReadOnlyList<string> ResultTemplateIds => _subscribe.ResultTemplateIds;

    /// <summary>v1.8 把审批结果通知申请人：通过时提示去登录，未通过时带原因。失败只记日志</summary>
    private async Task NotifyResult(string? openId, string name, string phone, bool approved, string? reason, DateTime time)
    {
        try
        {
            var content = approved ? "审批已通过，请用手机号和密码登录" : "未通过：" + (string.IsNullOrWhiteSpace(reason) ? "请修改后重新提交" : reason.Trim());
            await _subscribe.NotifyRegistrationResultAsync(openId, name, new SubscribeSlots(
                Title: "注册审批结果", OrderNo: phone, Customer: name, Content: content, Time: time, Operator: name,
                Status: approved ? "已通过" : "未通过", Device: ""));
        }
        catch (Exception ex)
        {
            _log.LogWarning("审批结果提醒发送失败（不影响审批）：{Message}", ex.Message);
        }
    }

    /// <summary>v1.8 当前用户要申请订阅的「注册申请提醒」模板（没有注册审批权限的人为空）</summary>
    public async Task<IReadOnlyList<string>> RegisterTemplateIdsFor(CurrentUser user)
    {
        var ids = _subscribe.RegisterTemplateIds;
        if (ids.Count == 0) return ids;
        return await _db.Employees.AnyAsync(e => e.Id == user.Id && e.CanApprove) ? ids : Array.Empty<string>();
    }

    // ---------- 小程序账号密码登录 ----------

    public async Task<LoginResult> Login(AccountLoginRequest req)
    {
        var phone = AuthService.NormalizePhone(req.Phone ?? "");
        if (phone.Length == 0) throw BizException.BadRequest("请输入手机号");
        if (string.IsNullOrEmpty(req.Password)) throw BizException.BadRequest("请输入密码");
        if (_throttle.IsLocked(phone)) throw BizException.BadRequest(PasswordService.TooManyAttempts);

        var emp = await _db.Employees.Include(e => e.AvatarFile).FirstOrDefaultAsync(e => e.Phone == phone);
        if (emp != null)
        {
            if (!PasswordPolicy.Verify(emp, req.Password, out var rehashed)) throw Failed(phone);
            _throttle.Reset(phone);
            if (!emp.Enabled) throw BizException.Forbidden("账号已停用，请联系管理员");
            if (rehashed != null) emp.PasswordHash = rehashed;
            await BindWeChat(emp, req.Code);
            await _db.SaveChangesAsync();
            return new LoginResult { Token = _tokens.CreateUserToken(emp), User = _view.User(emp), NeedBind = false };
        }

        // 还没有正式账号：密码对得上注册申请时，才告诉对方申请的状态（不向陌生人泄露谁注册过）
        var reg = await _db.Registrations.FirstOrDefaultAsync(r => r.Phone == phone && r.Status != RegistrationStatus.Approved);
        if (reg == null || !Verify(reg, req.Password)) throw Failed(phone);
        _throttle.Reset(phone);
        if (reg.Status == RegistrationStatus.Rejected)
            throw new BizException(CodeRejected, RejectedText(reg.RejectReason));
        throw new BizException(CodePending, PendingText);
    }

    public static string RejectedText(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "注册申请未通过，可修改后重新提交" : $"注册申请未通过：{reason.Trim()}。可修改后重新提交";

    private BizException Failed(string phone)
    {
        // 账号不存在 / 未设置密码 / 密码错误：同一文案，同样计数
        var locked = _throttle.RecordFailure(phone);
        return BizException.BadRequest(locked ? PasswordService.TooManyAttempts : PasswordService.WrongCredentials);
    }

    /// <summary>
    /// 把当前微信绑到账号上（订阅消息要靠 openid 才能发）。微信那边取不到 openid 不影响登录。
    /// 同一个微信之前绑在别的账号上的，挪到这个账号；提醒跟着最近登录的手机走。由调用方保存。
    /// </summary>
    private async Task BindWeChat(Employee emp, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        string openId;
        try
        {
            openId = await _wx.Code2Session(code.Trim());
        }
        catch (Exception ex)
        {
            _log.LogWarning("账号密码登录时获取微信身份失败（不影响登录）：{Message}", ex.Message);
            return;
        }
        if (string.IsNullOrEmpty(openId) || emp.OpenId == openId) return;
        var others = await _db.Employees.Where(e => e.OpenId == openId && e.Id != emp.Id).ToListAsync();
        foreach (var o in others) o.OpenId = null;
        emp.OpenId = openId;
    }

    /// <summary>退出登录：解除微信绑定，之后不再往这个微信发提醒</summary>
    public async Task Logout(CurrentUser user)
    {
        var me = await _db.Employees.FirstOrDefaultAsync(e => e.Id == user.Id);
        if (me == null || me.OpenId == null) return;
        me.OpenId = null;
        await _db.SaveChangesAsync();
    }

    // ---------- 主管审批 ----------

    private static string StatusText(RegistrationStatus s) => s switch
    {
        RegistrationStatus.Pending => "待审批",
        RegistrationStatus.Approved => "已通过",
        _ => "已驳回"
    };

    private static RegistrationDto ToDto(Registration r) => new()
    {
        Id = r.Id, Name = r.Name, Phone = r.Phone, Status = r.Status, StatusText = StatusText(r.Status), RejectReason = r.RejectReason,
        SubmittedAt = r.SubmittedAt, ReviewedAt = r.ReviewedAt, ReviewedByName = r.ReviewedBy?.Name, EmployeeId = r.EmployeeId
    };

    /// <summary>待审批的全部（先提交的在前）+ 最近处理过的 50 条</summary>
    public async Task<RegistrationListDto> List()
    {
        var pending = await _db.Registrations.Where(r => r.Status == RegistrationStatus.Pending)
            .OrderBy(r => r.SubmittedAt).ThenBy(r => r.Id).ToListAsync();
        var handled = await _db.Registrations.Include(r => r.ReviewedBy).Where(r => r.Status != RegistrationStatus.Pending)
            .OrderByDescending(r => r.ReviewedAt).ThenByDescending(r => r.Id).Take(50).ToListAsync();
        return new RegistrationListDto { PendingCount = pending.Count, Items = pending.Concat(handled).Select(ToDto).ToList() };
    }

    private async Task<Registration> LoadPending(int id)
    {
        var r = await _db.Registrations.Include(x => x.ReviewedBy).FirstOrDefaultAsync(x => x.Id == id)
                ?? throw BizException.NotFound("注册申请不存在");
        if (r.Status != RegistrationStatus.Pending) throw BizException.Conflict("该申请已处理过，请刷新");
        return r;
    }

    /// <summary>通过：必须选角色；可顺手改姓名、填部门。生成 Employee，密码沿用注册时填的</summary>
    public async Task<RegistrationDto> Approve(int id, RegistrationApproveRequest req, CurrentUser user, bool allowAdmin = true)
    {
        // v1.8 小程序里审批：不能给管理员岗位 / 管理员权限，那要去电脑后台
        if (!allowAdmin && (req.Position == Position.Admin || req.IsAdmin == true))
            throw BizException.BadRequest("管理员权限请到电脑后台设置");
        var position = AdminService.ResolvePosition(req.Position, req.Role);
        var name = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim();
        if (name != null && name.Length > 20) throw BizException.BadRequest("姓名不能超过 20 个字");
        var dept = AdminService.ResolveDepartment(req.Department);

        await using var tx = await _db.Database.BeginTransactionAsync();
        var r = await LoadPending(id);
        if (await _db.Employees.AnyAsync(e => e.Phone == r.Phone))
            throw BizException.Conflict("该手机号已有账号，无需再审批，可直接驳回这条申请");
        var now = _clock.Now;
        var emp = new Employee
        {
            Name = name ?? r.Name, Phone = r.Phone, Position = position, Role = Positions.ToRole(position),
            IsAdmin = position == Position.Admin || req.IsAdmin == true, Department = dept, Enabled = true,
            PasswordHash = r.PasswordHash, CreatedAt = now
        };
        _db.Employees.Add(emp);
        await _db.SaveChangesAsync();
        r.Name = emp.Name;
        r.Status = RegistrationStatus.Approved;
        r.PasswordHash = null;
        r.RejectReason = null;
        r.ReviewedById = user.Id;
        r.ReviewedAt = now;
        r.EmployeeId = emp.Id;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        _throttle.Reset(r.Phone);
        await NotifyResult(r.OpenId, emp.Name, r.Phone, true, null, now);
        return await Reloaded(r.Id);
    }

    public async Task<RegistrationDto> Reject(int id, RegistrationRejectRequest req, CurrentUser user)
    {
        var reason = (req.Reason ?? "").Trim();
        if (reason.Length == 0) throw BizException.BadRequest("请填写驳回原因");
        if (reason.Length > 100) throw BizException.BadRequest("驳回原因不能超过 100 个字");
        var r = await LoadPending(id);
        r.Status = RegistrationStatus.Rejected;
        r.RejectReason = reason;
        r.ReviewedById = user.Id;
        r.ReviewedAt = _clock.Now;
        await _db.SaveChangesAsync();
        await NotifyResult(r.OpenId, r.Name, r.Phone, false, reason, r.ReviewedAt.Value);
        return await Reloaded(r.Id);
    }

    private async Task<RegistrationDto> Reloaded(int id) =>
        ToDto(await _db.Registrations.Include(x => x.ReviewedBy).FirstAsync(x => x.Id == id));
}
