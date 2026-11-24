using System.Security.Claims;
using Huotong.AfterSales.Api.Domain;

namespace Huotong.AfterSales.Api.Infrastructure;

/// <summary>SalesManager：销售主管（Role 仍是 Sales）；Admin：有管理员权限</summary>
public record CurrentUser(int Id, Role Role, bool SalesManager = false, bool Admin = false);

public static class ClaimsExtensions
{
    public const string UserIdClaim = "userId";
    public const string RoleClaim = "role";
    public const string TokenVersionClaim = "tv";
    /// <summary>"1" = 有管理员权限</summary>
    public const string AdminClaim = "adm";
    /// <summary>"1" = 销售主管</summary>
    public const string SalesManagerClaim = "sm";
    /// <summary>授权策略名：需要管理员权限</summary>
    public const string AdminPolicy = "Admin";
    /// <summary>"1" = 能审批注册（管理员或有注册审批权限）</summary>
    public const string ApproverClaim = "apr";
    /// <summary>授权策略名：能审批注册</summary>
    public const string ApproverPolicy = "Approver";

    public static CurrentUser ToCurrentUser(this ClaimsPrincipal p)
    {
        var id = p.FindFirst(UserIdClaim)?.Value;
        var role = p.FindFirst(RoleClaim)?.Value;
        if (!int.TryParse(id, out var uid) || !Enum.TryParse<Role>(role, out var r))
            throw new BizException(40100, "登录已失效，请重新登录");
        return new CurrentUser(uid, r, p.FindFirst(SalesManagerClaim)?.Value == "1", p.FindFirst(AdminClaim)?.Value == "1");
    }
}
