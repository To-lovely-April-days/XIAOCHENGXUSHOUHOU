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
    public async Task<ApiResponse<SubscribeConfigDto>> Subscribe([FromServices] AccountService? accounts = null)
    {
        // v1.8 有注册审批权限的人，多申请一个「注册申请提醒」模板
        IEnumerable<string> extra = accounts == null ? Array.Empty<string>() : await accounts.RegisterTemplateIdsFor(Me);
        var ids = _subscribe.TemplateIds.Concat(extra).Distinct().ToList();
        return Ok(new SubscribeConfigDto { Enabled = ids.Count > 0, TemplateIds = ids });
    }

    /// <summary>v2.2 提醒设置：我用得上的几类微信提醒，各自开着还是关着</summary>
    [HttpGet("notices")]
    public async Task<ApiResponse<NoticeSettingsDto>> Notices() => Ok(await _home.Notices(Me, _subscribe.Enabled));

    [HttpPut("notices")]
    public async Task<ApiResponse<NoticeSettingsDto>> SetNotice([FromBody] NoticeToggleRequest? req) =>
        Ok(await _home.SetNotice(Me, req ?? new(), _subscribe.Enabled));

    [HttpPut("avatar")]
    public async Task<ApiResponse<UserDto>> Avatar([FromBody] AvatarRequest req) => Ok(await _home.SetAvatar(Me, req.FileId));

    /// <summary>v1.5 退出登录：解除微信绑定（不再往这个微信发提醒）</summary>
    [HttpPost("logout")]
    public async Task<ApiResponse<object?>> Logout([FromServices] AccountService accounts)
    {
        await accounts.Logout(Me);
        return Ok<object?>(null);
    }

    [HttpPut("password")]
    public async Task<ApiResponse<UserDto>> Password([FromBody] ChangePasswordRequest req) => Ok(await _password.ChangeMine(Me, req));
}
