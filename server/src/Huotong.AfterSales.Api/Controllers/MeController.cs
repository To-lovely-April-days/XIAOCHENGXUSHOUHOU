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

    public MeController(HomeService home, PasswordService password)
    {
        _home = home;
        _password = password;
    }

    [HttpGet]
    public async Task<ApiResponse<MeDto>> Get() => Ok(await _home.Me(Me));

    [HttpPut("avatar")]
    public async Task<ApiResponse<UserDto>> Avatar([FromBody] AvatarRequest req) => Ok(await _home.SetAvatar(Me, req.FileId));

    [HttpPut("password")]
    public async Task<ApiResponse<UserDto>> Password([FromBody] ChangePasswordRequest req) => Ok(await _password.ChangeMine(Me, req));
}
