using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Security;

namespace MgtAiAuthen.Api.Services;

public interface IAuditService
{
    /// <summary>บันทึกเหตุการณ์ลง dbo.AuditLogs พร้อม IP/User-Agent ของ request ปัจจุบัน</summary>
    Task LogAsync(
        string category,
        string action,
        int? userId,
        string? username,
        string? detail = null,
        bool isSuccess = true,
        CancellationToken ct = default);
}

public class AuditService(
    AppDbContext db,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AuditService> logger) : IAuditService
{
    public async Task LogAsync(
        string category,
        string action,
        int? userId,
        string? username,
        string? detail = null,
        bool isSuccess = true,
        CancellationToken ct = default)
    {
        HttpContext? context = httpContextAccessor.HttpContext;

        var entry = new AuditLog
        {
            UserId = userId,
            Username = username,
            Category = category,
            Action = action,
            Detail = detail,
            IsSuccess = isSuccess,
            ClientIp = context?.GetClientIp(),
            UserAgent = context?.GetUserAgent(),
            CreatedAt = DateTime.Now,
        };

        db.AuditLogs.Add(entry);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // การบันทึก audit ต้องไม่ทำให้ request หลักล้ม แต่ต้องเห็นใน log ของแอป
            logger.LogError(ex, "Failed to write audit log ({Category}/{Action})", category, action);
            db.AuditLogs.Remove(entry);
        }
    }
}
