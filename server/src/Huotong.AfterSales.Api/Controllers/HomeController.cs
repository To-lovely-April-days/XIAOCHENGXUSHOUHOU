using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Huotong.AfterSales.Api.Controllers;

[Authorize]
public class HomeController : ApiControllerBase
{
    private readonly HomeService _home;

    public HomeController(HomeService home) => _home = home;

    [HttpGet("api/home/engineer")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<EngineerHomeDto>> Engineer() => Ok(await _home.Engineer(Me));

    [HttpGet("api/home/supervisor")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<SupervisorHomeDto>> Supervisor() => Ok(await _home.Supervisor(Me));

    [HttpGet("api/engineers")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<List<EngineerLoadDto>>> Engineers() => Ok(await _home.Engineers());
}
