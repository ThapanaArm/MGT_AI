using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MgtAiAuthen.Api.Security;

/// <summary>claim ที่ใส่เพิ่มนอกเหนือจาก claim มาตรฐาน</summary>
public static class AppClaims
{
    public const string FullName = "full_name";
    public const string Department = "department";
}

public interface IJwtTokenService
{
    (string Token, DateTime ExpiresAt) CreateAccessToken(AppUser user);
}

public class JwtTokenService(IOptions<JwtOptions> options) : IJwtTokenService
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTime ExpiresAt) CreateAccessToken(AppUser user)
    {
        DateTime expiresAt = DateTime.Now.AddMinutes(_options.AccessTokenMinutes);

        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.UserRole),
            new(AppClaims.FullName, user.FullName),
            new(AppClaims.Department, user.Department ?? string.Empty),
        ];

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.Now,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

/// <summary>The standard claim names we use, kept here so the library constants are referenced in one place.</summary>
internal static class JwtRegisteredClaimNames
{
    public const string Sub = "sub";
    public const string Jti = "jti";
}

/// <summary>Helpers for reading the signed-in user out of the HttpContext.</summary>
public static class CurrentUserExtensions
{
    public static int GetUserId(this ClaimsPrincipal principal)
    {
        string? raw = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        return int.TryParse(raw, out int id)
            ? id
            : throw new InvalidOperationException("The token contains no user id");
    }

    public static string GetUsername(this ClaimsPrincipal principal)
        => principal.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;

    public static string GetUserRole(this ClaimsPrincipal principal)
        => principal.FindFirst(ClaimTypes.Role)?.Value ?? UserRoles.User;

    public static bool CanReadAllLogs(this ClaimsPrincipal principal)
        => principal.IsInRole(UserRoles.Admin) || principal.IsInRole(UserRoles.Auditor);

    /// <summary>IP ของ client — เคารพ X-Forwarded-For เมื่ออยู่หลัง reverse proxy</summary>
    public static string? GetClientIp(this HttpContext context)
    {
        string? forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            return forwarded.Split(',')[0].Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }

    public static string? GetUserAgent(this HttpContext context)
    {
        string? ua = context.Request.Headers.UserAgent.FirstOrDefault();
        return string.IsNullOrWhiteSpace(ua) ? null : ua.Length > 400 ? ua[..400] : ua;
    }
}
