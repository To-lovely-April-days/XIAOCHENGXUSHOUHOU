using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Huotong.AfterSales.Api.Controllers;

[Route("api/me")]
[Authorize]
public class MeController : ApiControllerBase
{
    private readonly HomeService _home;
    private readonly PasswordService _password;
    private readonly ISubscribeNotifier _subscribe;

    public MeController(HomeService home, PasswordService password, ISubscribeNotifier subscribe)
    {
        _home = home;
        _password = password;
        _subscribe = subscribe;
    }

    [HttpGet]
    public async Task<ApiResponse<MeDto>> Get() => Ok(await _home.Me(Me));

    /// <summary>v1.4：微信订阅消息配置（任何已登录角色）。未启用时 enabled=false、templateIds=[]</summary>
    [HttpGet("subscribe")]
    public ApiResponse<SubscribeConfigDto> Subscribe() =>
        Ok(new SubscribeConfigDto { Enabled = _subscribe.Enabled, TemplateIds = _subscribe.TemplateIds.ToList() });

    [HttpPut("avatar")]
    public async Task<ApiResponse<UserDto>> Avatar([FromBody] AvatarRequest req) => Ok(await _home.SetAvatar(Me, req.FileId));

    [HttpPut("password")]
    public async Task<ApiResponse<UserDto>> Password([FromBody] ChangePasswordRequest req) => Ok(await _password.ChangeMine(Me, req));
}
