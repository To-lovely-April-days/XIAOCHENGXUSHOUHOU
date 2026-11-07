using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Huotong.AfterSales.Api.Controllers;

[Route("api/admin")]
[Authorize(Roles = "Supervisor")]
public class AdminController : ApiControllerBase
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly AdminService _admin;
    private readonly AdminReportService _reports;
    private readonly PasswordService _password;
    private readonly IClock _clock;

    public AdminController(AdminService admin, AdminReportService reports, PasswordService password, IClock clock)
    {
        _admin = admin;
        _reports = reports;
        _password = password;
        _clock = clock;
    }

    [HttpGet("overview")]
    public async Task<ApiResponse<OverviewDto>> Overview() => Ok(await _admin.Overview());

    [HttpPost("punches/supplement")]
    public async Task<ApiResponse<OrderDetail>> Supplement([FromBody] SupplementRequest req) => Ok(await _admin.Supplement(req, Me));

    /// <summary>打卡记录（abnormal = distance | supplement | missing）</summary>
    [HttpGet("punches")]
    public async Task<ApiResponse<AdminPunchPageDto>> Punches([FromQuery] AdminPunchQuery q) => Ok(await _reports.Punches(q));

    /// <summary>工时统计：每个工程师一行</summary>
    [HttpGet("hours")]
    public async Task<ApiResponse<AdminHoursDto>> Hours([FromQuery] int? year, [FromQuery] int? month)
    {
        var now = _clock.Now;
        return Ok(await _reports.Hours(year ?? now.Year, month ?? now.Month));
    }

    [HttpGet("export/orders")]
    [Produces(Xlsx, "application/json")]
    public async Task<IActionResult> ExportOrders([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? status)
    {
        var bytes = await _admin.ExportOrders(from, to, status);
        return File(bytes, Xlsx, $"售后进度台账_{_clock.Now:yyyyMMddHHmm}.xlsx");
    }

    [HttpGet("export/hours")]
    [Produces(Xlsx, "application/json")]
    public async Task<IActionResult> ExportHours([FromQuery] int? year, [FromQuery] int? month)
    {
        var now = _clock.Now;
        var y = year ?? now.Year;
        var m = month ?? now.Month;
        var bytes = await _admin.ExportHours(y, m);
        return File(bytes, Xlsx, $"工时统计_{y}{m:00}.xlsx");
    }

    [HttpGet("employees")]
    public async Task<ApiResponse<List<EmployeeDto>>> Employees() => Ok(await _admin.Employees());

    [HttpPost("employees")]
    public async Task<ApiResponse<EmployeeDto>> CreateEmployee([FromBody] EmployeeCreateRequest req) => Ok(await _admin.CreateEmployee(req));

    [HttpPut("employees/{id:int}")]
    public async Task<ApiResponse<EmployeeDto>> UpdateEmployee(int id, [FromBody] EmployeeUpdateRequest req) =>
        Ok(await _admin.UpdateEmployee(id, req, Me));

    [HttpPost("employees/{id:int}/unbind")]
    public async Task<ApiResponse<EmployeeDto>> Unbind(int id) => Ok(await _admin.Unbind(id));

    [HttpPost("employees/{id:int}/password")]
    public async Task<ApiResponse<EmployeeDto>> SetPassword(int id, [FromBody] SetPasswordRequest req) =>
        Ok(_admin.ToDto(await _password.SetFor(id, req)));
}
