using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Huotong.AfterSales.Api.Controllers;

[Route("api/punches")]
[Authorize(Roles = "Engineer")]
public class PunchesController : ApiControllerBase
{
    private readonly PunchService _punches;

    public PunchesController(PunchService punches) => _punches = punches;

    [HttpGet("context")]
    public async Task<ApiResponse<PunchContextDto?>> Context([FromQuery] int? orderId) => Ok(await _punches.Context(Me, orderId));

    [HttpPost]
    public async Task<ApiResponse<PunchResultDto>> Create([FromBody] PunchRequest req) => Ok(await _punches.Create(Me, req));
}
