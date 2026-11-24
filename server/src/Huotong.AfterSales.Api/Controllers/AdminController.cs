using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Huotong.AfterSales.Api.Controllers;

/// <summary>报表类接口：售后主管即可；账号管理与注册审批：需要管理员权限（v1.7）</summary>
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

    /// <summary>工单文件中心（v1.2）</summary>
    /// <summary>v1.9 文件中心按工单归档：一个工单一个文件夹</summary>
    [HttpGet("files/folders")]
    public async Task<ApiResponse<FileFolderPage>> FileFolders([FromServices] FileCenterService center,
        [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 30) =>
        Ok(await center.Folders(keyword, page, pageSize, Me));

    [HttpGet("files")]
    public async Task<ApiResponse<FileCenterPage>> Files([FromServices] FileCenterService center, [FromQuery] FileCenterQuery q) =>
        Ok(await center.Query(q, Me));

    // ---------- v1.5 注册审批 ----------
    [Authorize(Policy = ClaimsExtensions.AdminPolicy)]
    [HttpGet("registrations")]
    public async Task<ApiResponse<RegistrationListDto>> Registrations([FromServices] AccountService accounts) => Ok(await accounts.List());

    [Authorize(Policy = ClaimsExtensions.AdminPolicy)]
    [HttpPost("registrations/{id:int}/approve")]
    public async Task<ApiResponse<RegistrationDto>> ApproveRegistration([FromServices] AccountService accounts, int id,
        [FromBody] RegistrationApproveRequest req) => Ok(await accounts.Approve(id, req, Me));

    [Authorize(Policy = ClaimsExtensions.AdminPolicy)]
    [HttpPost("registrations/{id:int}/reject")]
    public async Task<ApiResponse<RegistrationDto>> RejectRegistration([FromServices] AccountService accounts, int id,
        [FromBody] RegistrationRejectRequest req) => Ok(await accounts.Reject(id, req, Me));

    [Authorize(Policy = ClaimsExtensions.AdminPolicy)]
    [HttpGet("employees")]
    public async Task<ApiResponse<List<EmployeeDto>>> Employees() => Ok(await _admin.Employees());

    [Authorize(Policy = ClaimsExtensions.AdminPolicy)]
    [HttpPost("employees")]
    public async Task<ApiResponse<EmployeeDto>> CreateEmployee([FromBody] EmployeeCreateRequest req) => Ok(await _admin.CreateEmployee(req));

    [Authorize(Policy = ClaimsExtensions.AdminPolicy)]
    [HttpPut("employees/{id:int}")]
    public async Task<ApiResponse<EmployeeDto>> UpdateEmployee(int id, [FromBody] EmployeeUpdateRequest req) =>
        Ok(await _admin.UpdateEmployee(id, req, Me));

    [Authorize(Policy = ClaimsExtensions.AdminPolicy)]
    [HttpPost("employees/{id:int}/unbind")]
    public async Task<ApiResponse<EmployeeDto>> Unbind(int id) => Ok(await _admin.Unbind(id));

    [Authorize(Policy = ClaimsExtensions.AdminPolicy)]
    [HttpPost("employees/{id:int}/password")]
    public async Task<ApiResponse<EmployeeDto>> SetPassword(int id, [FromBody] SetPasswordRequest req) =>
        Ok(_admin.ToDto(await _password.SetFor(id, req)));
}
