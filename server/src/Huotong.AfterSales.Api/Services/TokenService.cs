using System.Security.Claims;
using System.Text;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Huotong.AfterSales.Api.Services;

public class TokenService
{
    private readonly JwtOptions _opt;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(IOptions<JwtOptions> opt) => _opt = opt.Value;

    public static SymmetricSecurityKey SigningKey(string key) => new(Encoding.UTF8.GetBytes(key));

    public string CreateUserToken(Employee e) => Create(new Dictionary<string, object>
    {
        [ClaimsExtensions.UserIdClaim] = e.Id.ToString(),
        [ClaimsExtensions.RoleClaim] = e.Role.ToString(),
        [ClaimsExtensions.TokenVersionClaim] = e.TokenVersion.ToString(),
        ["name"] = e.Name,
    }, TimeSpan.FromDays(_opt.ExpireDays), "huotong-app");

    /// <summary>绑定票据：10 分钟，含 openid，purpose=bind</summary>
    public string CreateBindTicket(string openId) => Create(new Dictionary<string, object>
    {
        ["openid"] = openId,
        ["purpose"] = "bind",
    }, TimeSpan.FromMinutes(10), "huotong-bind");

    public async Task<string?> ReadBindTicket(string ticket)
    {
        var result = await _handler.ValidateTokenAsync(ticket, new TokenValidationParameters
        {
            ValidIssuer = _opt.Issuer, ValidAudience = "huotong-bind", IssuerSigningKey = SigningKey(_opt.Key),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30)
        });
        if (!result.IsValid) return null;
        if (!result.Claims.TryGetValue("purpose", out var p) || p?.ToString() != "bind") return null;
        return result.Claims.TryGetValue("openid", out var o) ? o?.ToString() : null;
    }

    private string Create(Dictionary<string, object> claims, TimeSpan life, string audience)
    {
        var now = DateTime.UtcNow;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _opt.Issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(life),
            SigningCredentials = new SigningCredentials(SigningKey(_opt.Key), SecurityAlgorithms.HmacSha256)
        });
    }
}
