using System.Security.Claims;
using Huotong.AfterSales.Api.Domain;

namespace Huotong.AfterSales.Api.Infrastructure;

public record CurrentUser(int Id, Role Role);

public static class ClaimsExtensions
{
    public const string UserIdClaim = "userId";
    public const string RoleClaim = "role";
    public const string TokenVersionClaim = "tv";

    public static CurrentUser ToCurrentUser(this ClaimsPrincipal p)
    {
        var id = p.FindFirst(UserIdClaim)?.Value;
        var role = p.FindFirst(RoleClaim)?.Value;
        if (!int.TryParse(id, out var uid) || !Enum.TryParse<Role>(role, out var r))
            throw new BizException(40100, "登录已失效，请重新登录");
        return new CurrentUser(uid, r);
    }
}
