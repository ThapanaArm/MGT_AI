using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using MgtAiAuthen.Api.Options;
using MgtAiAuthen.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Services;

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<LoginResponse> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task LogoutAsync(int userId, string? refreshToken, CancellationToken ct = default);
    Task ChangePasswordAsync(int userId, ChangePasswordRequest request, CancellationToken ct = default);
    Task<UserProfileDto> GetProfileAsync(int userId, CancellationToken ct = default);
}

public class AuthService(
    AppDbContext db,
    IJwtTokenService tokens,
    IAuditService audit,
    IHttpContextAccessor httpContextAccessor,
    IOptions<JwtOptions> jwtOptions,
    IOptions<SecurityOptions> securityOptions) : IAuthService
{
    /// <summary>ข้อความเดียวกันทั้งกรณีไม่มีผู้ใช้และรหัสผ่านผิด เพื่อไม่ให้เดาได้ว่ามี username นี้อยู่จริง</summary>
    private const string InvalidCredentials = "Incorrect username or password";

    private readonly JwtOptions _jwt = jwtOptions.Value;
    private readonly SecurityOptions _security = securityOptions.Value;

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        string username = request.Username.Trim();

        AppUser? user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

        if (user is null)
        {
            await audit.LogAsync(AuditCategories.Auth, AuditActions.LoginFailed, null, username,
                "Username does not exist", isSuccess: false, ct);
            throw AppException.Unauthorized(InvalidCredentials);
        }

        if (!user.IsActive)
        {
            await audit.LogAsync(AuditCategories.Auth, AuditActions.LoginFailed, user.UserId, user.Username,
                "Account is disabled", isSuccess: false, ct);
            throw AppException.Forbidden("This account is disabled. Please contact your system administrator");
        }

        if (user.LockoutUntil is { } lockedUntil && lockedUntil > DateTime.Now)
        {
            await audit.LogAsync(AuditCategories.Auth, AuditActions.LoginLockedOut, user.UserId, user.Username,
                $"Account locked until {lockedUntil:yyyy-MM-dd HH:mm:ss}", isSuccess: false, ct);
            throw AppException.Forbidden(
                $"Account temporarily locked after too many failed sign-in attempts. Please try again after {lockedUntil:HH:mm}");
        }

        if (!PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            string detail = $"Incorrect password (attempt {user.FailedLoginCount})";

            if (user.FailedLoginCount >= _security.MaxFailedLoginAttempts)
            {
                user.LockoutUntil = DateTime.Now.AddMinutes(_security.LockoutMinutes);
                user.FailedLoginCount = 0;
                detail += $" — account locked until {user.LockoutUntil:yyyy-MM-dd HH:mm:ss}";
            }

            user.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(AuditCategories.Auth, AuditActions.LoginFailed, user.UserId, user.Username,
                detail, isSuccess: false, ct);
            throw AppException.Unauthorized(InvalidCredentials);
        }

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = DateTime.Now;
        user.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Auth, AuditActions.LoginSuccess, user.UserId, user.Username,
            $"Signed in with role {user.UserRole}", isSuccess: true, ct);

        return await IssueTokensAsync(user, ct);
    }

    public async Task<LoginResponse> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        string hash = PasswordHasher.Sha256(refreshToken);

        RefreshToken? stored = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null || stored.RevokedAt is not null || stored.ExpiresAt <= DateTime.Now)
        {
            throw AppException.Unauthorized("Refresh token is invalid or has expired. Please sign in again");
        }

        AppUser user = stored.User
            ?? throw AppException.Unauthorized("No user matches this token. Please sign in again");

        if (!user.IsActive)
        {
            throw AppException.Forbidden("This account is disabled. Please contact your system administrator");
        }

        // rotate: token เดิมใช้ซ้ำไม่ได้อีก
        stored.RevokedAt = DateTime.Now;
        stored.RevokedReason = "Rotated on refresh";
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Auth, AuditActions.TokenRefreshed, user.UserId, user.Username,
            null, isSuccess: true, ct);

        return await IssueTokensAsync(user, ct);
    }

    public async Task LogoutAsync(int userId, string? refreshToken, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            string hash = PasswordHasher.Sha256(refreshToken);
            RefreshToken? stored = await db.RefreshTokens
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, ct);

            if (stored is not null && stored.RevokedAt is null)
            {
                stored.RevokedAt = DateTime.Now;
                stored.RevokedReason = "User signed out";
                await db.SaveChangesAsync(ct);
            }
        }

        string? username = await db.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(ct);

        await audit.LogAsync(AuditCategories.Auth, AuditActions.Logout, userId, username, null, true, ct);
    }

    public async Task ChangePasswordAsync(int userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        AppUser user = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct)
            ?? throw AppException.NotFound("User not found");

        if (!PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            await audit.LogAsync(AuditCategories.Auth, AuditActions.PasswordChanged, user.UserId, user.Username,
                "Current password is incorrect", isSuccess: false, ct);
            throw AppException.Unauthorized("Current password is incorrect");
        }

        ValidatePasswordStrength(request.NewPassword);

        if (PasswordHasher.Verify(request.NewPassword, user.PasswordHash))
        {
            throw new AppException("The new password must differ from the current one");
        }

        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        user.MustChangePassword = false;
        user.UpdatedAt = DateTime.Now;

        // Changing the password revokes every other session.
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ForEachAsync(t =>
            {
                t.RevokedAt = DateTime.Now;
                t.RevokedReason = "Password changed";
            }, ct);

        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Auth, AuditActions.PasswordChanged, user.UserId, user.Username,
            "Password changed successfully", isSuccess: true, ct);
    }

    public async Task<UserProfileDto> GetProfileAsync(int userId, CancellationToken ct = default)
    {
        AppUser user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, ct)
            ?? throw AppException.NotFound("User not found");

        return ToProfile(user);
    }

    private void ValidatePasswordStrength(string password)
    {
        if (password.Length < _security.MinPasswordLength)
        {
            throw new AppException($"Password must be at least {_security.MinPasswordLength} characters");
        }

        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
        {
            throw new AppException("Password must contain both letters and digits");
        }
    }

    private async Task<LoginResponse> IssueTokensAsync(AppUser user, CancellationToken ct)
    {
        (string accessToken, DateTime expiresAt) = tokens.CreateAccessToken(user);

        string refreshToken = PasswordHasher.NewRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.UserId,
            TokenHash = PasswordHasher.Sha256(refreshToken),
            ExpiresAt = DateTime.Now.AddDays(_jwt.RefreshTokenDays),
            CreatedAt = DateTime.Now,
            CreatedByIp = httpContextAccessor.HttpContext?.GetClientIp(),
        });
        await db.SaveChangesAsync(ct);

        return new LoginResponse(accessToken, refreshToken, expiresAt, ToProfile(user));
    }

    private static UserProfileDto ToProfile(AppUser user) => new(
        user.UserId,
        user.Username,
        user.FullName,
        user.Email,
        user.Department,
        user.UserRole,
        user.MustChangePassword,
        user.LastLoginAt);
}
