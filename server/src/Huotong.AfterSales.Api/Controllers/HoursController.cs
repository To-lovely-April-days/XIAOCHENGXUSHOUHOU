using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Huotong.AfterSales.Api.Controllers;

[Route("api/hours")]
[Authorize(Roles = "Engineer,Supervisor")]
public class HoursController : ApiControllerBase
{
    private readonly HoursService _hours;
    private readonly IClock _clock;

    public HoursController(HoursService hours, IClock clock)
    {
        _hours = hours;
        _clock = clock;
    }

    /// <summary>工程师查自己；主管须带 engineerId</summary>
    private int Target(int? engineerId)
    {
        var me = Me;
        if (me.Role == Role.Engineer)
        {
            if (engineerId != null && engineerId != me.Id) throw BizException.Forbidden("只能查看本人工时");
            return me.Id;
        }
        return engineerId ?? throw BizException.BadRequest("请指定工程师");
    }

    [HttpGet("today")]
    public async Task<ApiResponse<TodayHoursDto>> Today([FromQuery] int? engineerId) => Ok(await _hours.Today(Target(engineerId)));

    [HttpGet("month")]
    public async Task<ApiResponse<MonthHoursDto>> Month([FromQuery] int? year, [FromQuery] int? month, [FromQuery] int? engineerId)
    {
        var now = _clock.Now;
        return Ok(await _hours.Month(Target(engineerId), year ?? now.Year, month ?? now.Month));
    }
}
