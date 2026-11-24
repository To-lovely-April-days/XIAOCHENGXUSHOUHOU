using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Api.Controllers;

[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ApiControllerBase
{
    private readonly AuthService _auth;
    private readonly PasswordService _password;
    private readonly WeChatOptions _wx;

    public AuthController(AuthService auth, PasswordService password, IOptions<WeChatOptions> wx)
    {
        _auth = auth;
        _password = password;
        _wx = wx.Value;
    }

    /// <summary>PC 后台账号密码登录（仅 Supervisor）</summary>
    [HttpPost("password-login")]
    public async Task<ApiResponse<LoginResult>> PasswordLogin([FromBody] PasswordLoginRequest req) => Ok(await _password.Login(req));

    /// <summary>v1.5 小程序账号密码登录（所有角色）。40310 = 审核中，40311 = 注册被驳回</summary>
    [HttpPost("account-login")]
    public async Task<ApiResponse<LoginResult>> AccountLogin([FromServices] AccountService accounts, [FromBody] AccountLoginRequest req) =>
        Ok(await accounts.Login(req));

    /// <summary>v1.5 小程序注册申请，等主管审批</summary>
    [HttpPost("register")]
    public async Task<ApiResponse<RegisterResult>> Register([FromServices] AccountService accounts, [FromBody] RegisterRequest req) =>
        Ok(await accounts.Register(req));

    [HttpPost("login")]
    public async Task<ApiResponse<LoginResult>> Login([FromBody] LoginRequest req) => Ok(await _auth.Login(req));

    [HttpPost("bind")]
    public async Task<ApiResponse<LoginResult>> Bind([FromBody] BindRequest req) => Ok(await _auth.Bind(req));

    /// <summary>仅 MockMode 可用</summary>
    [HttpPost("dev-login")]
    public async Task<ApiResponse<LoginResult>> DevLogin([FromBody] DevLoginRequest req)
    {
        if (!_wx.MockMode) throw BizException.NotFound("接口不存在");
        return Ok(await _auth.DevLogin(req));
    }
}
