using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Huotong.AfterSales.Api.Controllers;

/// <summary>
/// v1.8 小程序里的注册审批：管理员或有「注册审批权限」的人可用。
/// 与电脑后台的 /api/admin/registrations 是同一份数据，区别是这里不能授予管理员岗位 / 管理员权限。
/// </summary>
[Route("api/registrations")]
[Authorize(Policy = ClaimsExtensions.ApproverPolicy)]
public class RegistrationsController : ApiControllerBase
{
    private readonly AccountService _accounts;

    public RegistrationsController(AccountService accounts) => _accounts = accounts;

    [HttpGet]
    public async Task<ApiResponse<RegistrationListDto>> List() => Ok(await _accounts.List());

    [HttpPost("{id:int}/approve")]
    public async Task<ApiResponse<RegistrationDto>> Approve(int id, [FromBody] RegistrationApproveRequest req) =>
        Ok(await _accounts.Approve(id, req, Me, allowAdmin: false));

    [HttpPost("{id:int}/reject")]
    public async Task<ApiResponse<RegistrationDto>> Reject(int id, [FromBody] RegistrationRejectRequest req) =>
        Ok(await _accounts.Reject(id, req, Me));
}
