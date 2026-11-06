using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

public class AuthService
{
    private readonly AppDbContext _db;
    private readonly IWeChatService _wx;
    private readonly TokenService _tokens;
    private readonly OrderViewBuilder _view;

    public AuthService(AppDbContext db, IWeChatService wx, TokenService tokens, OrderViewBuilder view)
    {
        _db = db;
        _wx = wx;
        _tokens = tokens;
        _view = view;
    }

    public async Task<LoginResult> Login(LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Code)) throw BizException.BadRequest("缺少登录 code");
        var openId = await _wx.Code2Session(req.Code.Trim());
        var emp = await _db.Employees.Include(e => e.AvatarFile).FirstOrDefaultAsync(e => e.OpenId == openId);
        if (emp == null)
            return new LoginResult { NeedBind = true, BindTicket = _tokens.CreateBindTicket(openId) };
        if (!emp.Enabled) throw BizException.Forbidden("账号已停用，请联系售后主管");
        return Success(emp);
    }

    public async Task<LoginResult> Bind(BindRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.BindTicket)) throw BizException.BadRequest("缺少绑定票据，请重新登录");
        if (string.IsNullOrWhiteSpace(req.PhoneCode)) throw BizException.BadRequest("请先验证手机号");
        var openId = await _tokens.ReadBindTicket(req.BindTicket);
        if (openId == null) throw BizException.BadRequest("绑定票据已过期，请重新登录");
        var phone = NormalizePhone(await _wx.GetPhoneNumber(req.PhoneCode));

        var emp = await _db.Employees.Include(e => e.AvatarFile).FirstOrDefaultAsync(e => e.Phone == phone && e.Enabled)
                  ?? throw new BizException(40401, "未找到账号，请联系售后主管在后台添加");
        if (!string.IsNullOrEmpty(emp.OpenId) && emp.OpenId != openId)
            throw BizException.Conflict("该账号已绑定其他微信，请联系主管解绑");

        // 同一微信若绑在别的账号上，先解除
        var others = await _db.Employees.Where(e => e.OpenId == openId && e.Id != emp.Id).ToListAsync();
        foreach (var o in others) { o.OpenId = null; o.TokenVersion++; }
        emp.OpenId = openId;
        await _db.SaveChangesAsync();
        return Success(emp);
    }

    public async Task<LoginResult> DevLogin(DevLoginRequest req)
    {
        var phone = NormalizePhone(req.Phone ?? "");
        if (phone.Length == 0) throw BizException.BadRequest("请输入手机号");
        var emp = await _db.Employees.Include(e => e.AvatarFile).FirstOrDefaultAsync(e => e.Phone == phone && e.Enabled)
                  ?? throw new BizException(40401, "未找到账号，请联系售后主管在后台添加");
        return Success(emp);
    }

    private LoginResult Success(Employee emp) => new()
    {
        Token = _tokens.CreateUserToken(emp), User = _view.User(emp), NeedBind = false
    };

    public static string NormalizePhone(string s) =>
        new string(s.Where(char.IsDigit).ToArray()) is var d && d.StartsWith("86") && d.Length == 13 ? d[2..] : new string(s.Where(char.IsDigit).ToArray());
}
