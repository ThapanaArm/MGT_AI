using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Security;
using MgtAiAuthen.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MgtAiAuthen.Api.Controllers;

/// <summary>Sign in / refresh token / sign out / change password.</summary>
[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService auth) : ControllerBase
{
    /// <summary>เข้าสู่ระบบด้วยชื่อผู้ใช้และรหัสผ่าน</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
        => Ok(await auth.LoginAsync(request, ct));

    /// <summary>ขอ access token ใหม่ด้วย refresh token (token เดิมจะถูกยกเลิกทันที)</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LoginResponse>> Refresh(RefreshRequest request, CancellationToken ct)
        => Ok(await auth.RefreshAsync(request.RefreshToken, ct));

    /// <summary>Signs out and revokes the supplied refresh token.</summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(RefreshRequest? request, CancellationToken ct)
    {
        await auth.LogoutAsync(User.GetUserId(), request?.RefreshToken, ct);
        return NoContent();
    }

    /// <summary>ข้อมูลผู้ใช้ที่ล็อกอินอยู่ — ใช้ตรวจว่า token ยังใช้ได้ตอนเปิดหน้าเว็บ</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserProfileDto>> Me(CancellationToken ct)
        => Ok(await auth.GetProfileAsync(User.GetUserId(), ct));

    /// <summary>Changes your own password (all other sessions are revoked).</summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        await auth.ChangePasswordAsync(User.GetUserId(), request, ct);
        return NoContent();
    }
}
