using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using MgtAiAuthen.Api.Options;
using MgtAiAuthen.Api.Security;
using MgtAiAuthen.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Controllers;

/// <summary>User management — Admin role only.</summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = UserRoles.Admin)]
public class AdminUsersController(
    AppDbContext db,
    IAuditService audit,
    IOptions<SecurityOptions> securityOptions) : ControllerBase
{
    private readonly SecurityOptions _security = securityOptions.Value;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AdminUserDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AdminUserDto>>> GetAll(CancellationToken ct)
        => Ok(await db.Users
            .AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new AdminUserDto(
                u.UserId, u.Username, u.FullName, u.Email, u.Department, u.UserRole,
                u.IsActive, u.MustChangePassword, u.LastLoginAt, u.LockoutUntil, u.FailedLoginCount,
                db.ChatMessages.Count(m => m.UserId == u.UserId && m.MessageRole == MessageRoles.User),
                u.CreatedAt))
            .ToListAsync(ct));

    [HttpPost]
    [ProducesResponseType(typeof(AdminUserDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<AdminUserDto>> Create(CreateUserRequest request, CancellationToken ct)
    {
        ValidatePasswordStrength(request.Password);

        string username = request.Username.Trim();
        if (await db.Users.AnyAsync(u => u.Username == username, ct))
        {
            throw AppException.Conflict($"Username \"{username}\" already exists");
        }

        var user = new AppUser
        {
            Username = username,
            Email = request.Email.Trim(),
            FullName = request.FullName.Trim(),
            Department = string.IsNullOrWhiteSpace(request.Department) ? null : request.Department.Trim(),
            PasswordHash = PasswordHasher.Hash(request.Password),
            UserRole = request.UserRole,
            IsActive = true,
            MustChangePassword = request.MustChangePassword,
            CreatedAt = DateTime.Now,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Admin, AuditActions.UserCreated,
            User.GetUserId(), User.GetUsername(),
            $"Created user {user.Username} (role {user.UserRole}, department {user.Department ?? "-"})",
            isSuccess: true, ct);

        return CreatedAtAction(nameof(GetAll), new { id = user.UserId }, ToDto(user, 0));
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(AdminUserDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminUserDto>> Update(
        int id, UpdateUserRequest request, CancellationToken ct)
    {
        AppUser user = await db.Users.FirstOrDefaultAsync(u => u.UserId == id, ct)
            ?? throw AppException.NotFound("User not found");

        // กันกรณีถอนสิทธิ์ Admin ของตัวเองแล้วไม่มีใครเข้ามาแก้ได้อีก
        if (user.UserId == User.GetUserId() && request.UserRole != UserRoles.Admin)
        {
            throw new AppException("You cannot remove the Admin role from your own account");
        }

        if (user.UserId == User.GetUserId() && !request.IsActive)
        {
            throw new AppException("You cannot disable your own account");
        }

        user.Email = request.Email.Trim();
        user.FullName = request.FullName.Trim();
        user.Department = string.IsNullOrWhiteSpace(request.Department) ? null : request.Department.Trim();
        user.UserRole = request.UserRole;
        user.IsActive = request.IsActive;
        user.UpdatedAt = DateTime.Now;

        // ปิดใช้งานแล้วต้องตัด session ที่ค้างอยู่ด้วย ไม่ใช่รอ token หมดอายุ
        if (!request.IsActive)
        {
            await RevokeTokensAsync(id, "Account is disabled", ct);
        }

        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Admin, AuditActions.UserUpdated,
            User.GetUserId(), User.GetUsername(),
            $"Updated user {user.Username} (role {user.UserRole}, {(user.IsActive ? "enabled" : "disabled")})",
            isSuccess: true, ct);

        int messageCount = await db.ChatMessages
            .CountAsync(m => m.UserId == id && m.MessageRole == MessageRoles.User, ct);

        return Ok(ToDto(user, messageCount));
    }

    /// <summary>ตั้งรหัสผ่านใหม่แทนผู้ใช้ (ใช้เมื่อพนักงานลืมรหัสผ่าน) และปลดล็อกบัญชี</summary>
    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(
        int id, ResetPasswordRequest request, CancellationToken ct)
    {
        ValidatePasswordStrength(request.NewPassword);

        AppUser user = await db.Users.FirstOrDefaultAsync(u => u.UserId == id, ct)
            ?? throw AppException.NotFound("User not found");

        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        user.MustChangePassword = request.MustChangePassword;
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.UpdatedAt = DateTime.Now;

        await RevokeTokensAsync(id, "Administrator reset the password", ct);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Admin, AuditActions.UserPasswordReset,
            User.GetUserId(), User.GetUsername(),
            $"Reset the password for {user.Username}", isSuccess: true, ct);

        return NoContent();
    }

    private async Task RevokeTokensAsync(int userId, string reason, CancellationToken ct)
        => await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ForEachAsync(t =>
            {
                t.RevokedAt = DateTime.Now;
                t.RevokedReason = reason;
            }, ct);

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

    private static AdminUserDto ToDto(AppUser u, int messageCount) => new(
        u.UserId, u.Username, u.FullName, u.Email, u.Department, u.UserRole,
        u.IsActive, u.MustChangePassword, u.LastLoginAt, u.LockoutUntil, u.FailedLoginCount,
        messageCount, u.CreatedAt);
}
