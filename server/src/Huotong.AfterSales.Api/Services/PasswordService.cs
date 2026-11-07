using System.Collections.Concurrent;
using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

/// <summary>密码规则与哈希（PasswordHasher&lt;Employee&gt;，PBKDF2）</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 64;
    private static readonly PasswordHasher<Employee> Hasher = new();

    /// <summary>≥ 8 位且同时含字母和数字；不符合返回中文错误，否则 null</summary>
    public static string? Validate(string? password, string label = "密码")
    {
        if (string.IsNullOrEmpty(password)) return $"请填写{label}";
        if (password.Length < MinLength) return $"{label}至少 {MinLength} 位，且需同时包含字母和数字";
        if (password.Length > MaxLength) return $"{label}不能超过 {MaxLength} 位";
        if (!password.Any(char.IsAsciiLetter) || !password.Any(char.IsAsciiDigit))
            return $"{label}需同时包含字母和数字";
        return null;
    }

    public static void Ensure(string? password, string label = "密码")
    {
        var err = Validate(password, label);
        if (err != null) throw BizException.BadRequest(err);
    }

    public static string Hash(Employee e, string password) => Hasher.HashPassword(e, password);

    /// <summary>校验密码；哈希算法参数过旧时顺带重新哈希（rehashed 非 null）</summary>
    public static bool Verify(Employee e, string password, out string? rehashed)
    {
        rehashed = null;
        if (string.IsNullOrEmpty(e.PasswordHash) || string.IsNullOrEmpty(password)) return false;
        var r = Hasher.VerifyHashedPassword(e, e.PasswordHash, password);
        if (r == PasswordVerificationResult.SuccessRehashNeeded) rehashed = Hasher.HashPassword(e, password);
        return r != PasswordVerificationResult.Failed;
    }
}

/// <summary>密码登录失败计数（内存）：同一手机号连续 5 次失败锁定 10 分钟</summary>
public class LoginThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(10);

    private record Entry(int Failures, DateTime? LockedUntil);
    private readonly ConcurrentDictionary<string, Entry> _map = new();
    private readonly IClock _clock;

    public LoginThrottle(IClock clock) => _clock = clock;

    public bool IsLocked(string key)
    {
        if (!_map.TryGetValue(key, out var e) || e.LockedUntil == null) return false;
        if (e.LockedUntil > _clock.Now) return true;
        _map.TryRemove(key, out _); // 锁定已过期，重新计数
        return false;
    }

    /// <summary>记一次失败；返回本次之后是否已锁定</summary>
    public bool RecordFailure(string key)
    {
        var now = _clock.Now;
        var e = _map.AddOrUpdate(key,
            _ => new Entry(1, null),
            (_, old) => old.LockedUntil != null && old.LockedUntil <= now ? new Entry(1, null) : new Entry(old.Failures + 1, old.LockedUntil));
        if (e.Failures >= MaxFailures && e.LockedUntil == null)
        {
            _map[key] = e with { LockedUntil = now + LockDuration };
            return true;
        }
        return e.LockedUntil != null;
    }

    public void Reset(string key) => _map.TryRemove(key, out _);
}

/// <summary>PC 后台账号密码：登录、改密、主管重置（API.md §9）</summary>
public class PasswordService
{
    public const string WrongCredentials = "手机号或密码错误";
    public const string TooManyAttempts = "尝试次数过多，请 10 分钟后再试";

    private readonly AppDbContext _db;
    private readonly TokenService _tokens;
    private readonly OrderViewBuilder _view;
    private readonly LoginThrottle _throttle;

    public PasswordService(AppDbContext db, TokenService tokens, OrderViewBuilder view, LoginThrottle throttle)
    {
        _db = db;
        _tokens = tokens;
        _view = view;
        _throttle = throttle;
    }

    public async Task<LoginResult> Login(PasswordLoginRequest req)
    {
        var phone = AuthService.NormalizePhone(req.Phone ?? "");
        if (phone.Length == 0) throw BizException.BadRequest("请输入手机号");
        if (string.IsNullOrEmpty(req.Password)) throw BizException.BadRequest("请输入密码");
        if (_throttle.IsLocked(phone)) throw BizException.BadRequest(TooManyAttempts);

        var emp = await _db.Employees.Include(e => e.AvatarFile).FirstOrDefaultAsync(e => e.Phone == phone);
        string? rehashed = null;
        if (emp == null || !PasswordPolicy.Verify(emp, req.Password, out rehashed))
        {
            // 账号不存在 / 未设置密码 / 密码错误：同一文案，同样计数
            var locked = _throttle.RecordFailure(phone);
            throw BizException.BadRequest(locked ? TooManyAttempts : WrongCredentials);
        }
        _throttle.Reset(phone);
        if (!emp.Enabled) throw BizException.Forbidden("账号已停用");
        if (emp.Role != Role.Supervisor) throw BizException.Forbidden("仅售后主管可登录电脑后台");
        if (rehashed != null)
        {
            emp.PasswordHash = rehashed;
            await _db.SaveChangesAsync();
        }
        return new LoginResult { Token = _tokens.CreateUserToken(emp), User = _view.User(emp), NeedBind = false };
    }

    public async Task<UserDto> ChangeMine(CurrentUser user, ChangePasswordRequest req)
    {
        var me = await _view.LoadEmployee(user.Id);
        if (!string.IsNullOrEmpty(me.PasswordHash))
        {
            if (string.IsNullOrEmpty(req.OldPassword)) throw BizException.BadRequest("请填写原密码");
            if (!PasswordPolicy.Verify(me, req.OldPassword, out _)) throw BizException.BadRequest("原密码错误");
        }
        PasswordPolicy.Ensure(req.NewPassword, "新密码");
        me.PasswordHash = PasswordPolicy.Hash(me, req.NewPassword!);
        await _db.SaveChangesAsync();
        return _view.User(me);
    }

    /// <summary>主管为员工设置/重置密码</summary>
    public async Task<Employee> SetFor(int employeeId, SetPasswordRequest req)
    {
        var e = await _db.Employees.Include(x => x.AvatarFile).FirstOrDefaultAsync(x => x.Id == employeeId)
                ?? throw BizException.NotFound("员工不存在");
        PasswordPolicy.Ensure(req.Password);
        e.PasswordHash = PasswordPolicy.Hash(e, req.Password!);
        await _db.SaveChangesAsync();
        _throttle.Reset(e.Phone); // 重置密码后解除锁定
        return e;
    }

    /// <summary>
    /// 启动时开通：配置了 Bootstrap:SupervisorPassword 时，给 PasswordHash 为空的主管设上该密码。
    /// 返回设置的人数；密码不符合强度规则时不设置并返回错误信息。
    /// </summary>
    public static async Task<(int Count, string? Error)> BootstrapSupervisors(AppDbContext db, string? password)
    {
        if (string.IsNullOrEmpty(password)) return (0, null);
        var err = PasswordPolicy.Validate(password);
        if (err != null) return (0, err);
        var list = await db.Employees.Where(e => e.Role == Role.Supervisor && (e.PasswordHash == null || e.PasswordHash == "")).ToListAsync();
        foreach (var e in list) e.PasswordHash = PasswordPolicy.Hash(e, password);
        if (list.Count > 0) await db.SaveChangesAsync();
        return (list.Count, null);
    }
}
