using System.Globalization;
using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Security;
using MgtAiAuthen.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MgtAiAuthen.Api.Controllers;

/// <summary>
/// Log auditing — available to the Admin and Auditor roles only.
/// การเปิดค้นหาและการ export จะถูกบันทึกลง AuditLogs ด้วย
/// </summary>
[ApiController]
[Route("api/admin/logs")]
[Authorize(Roles = UserRoles.AdminOrAuditor)]
public class AdminLogsController(IChatLogService logs, IAuditService audit) : ControllerBase
{
    /// <summary>
    /// ค้นหา log การแชทตามเงื่อนไข เช่น
    /// <c>?keyword=ที่ไหน&amp;questionType=WHERE&amp;userId=2&amp;from=2026-09-01&amp;to=2026-09-30</c>
    /// </summary>
    [HttpGet("chat")]
    [ProducesResponseType(typeof(PagedResult<ChatLogItemDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ChatLogItemDto>>> SearchChat(
        [FromQuery] ChatLogQuery query, CancellationToken ct)
    {
        PagedResult<ChatLogItemDto> result = await logs.SearchAsync(query, ct);

        // บันทึกเฉพาะการค้นครั้งแรกของแต่ละชุดเงื่อนไข ไม่บันทึกตอนเปลี่ยนหน้า
        if (query.Page <= 1)
        {
            await audit.LogAsync(
                AuditCategories.Admin, AuditActions.ChatLogSearched,
                User.GetUserId(), User.GetUsername(),
                $"Filters: {Describe(query)} | {result.TotalCount} match(es)", isSuccess: true, ct);
        }

        return Ok(result);
    }

    /// <summary>สรุปตัวเลขของผลค้นหาชุดเดียวกัน (ใช้เงื่อนไขเหมือน /chat)</summary>
    [HttpGet("chat/stats")]
    [ProducesResponseType(typeof(ChatLogStats), StatusCodes.Status200OK)]
    public async Task<ActionResult<ChatLogStats>> ChatStats(
        [FromQuery] ChatLogQuery query, CancellationToken ct)
        => Ok(await logs.GetStatsAsync(query, ct));

    /// <summary>
    /// Token cost report — accepts the same filters as /chat.
    /// Counts only rows that carry a cost (AI answers); user questions have no separate cost.
    /// </summary>
    [HttpGet("cost")]
    [ProducesResponseType(typeof(CostReport), StatusCodes.Status200OK)]
    public async Task<ActionResult<CostReport>> CostReport(
        [FromQuery] ChatLogQuery query, CancellationToken ct)
    {
        CostReport report = await logs.GetCostReportAsync(query, ct);

        if (query.Page <= 1)
        {
            await audit.LogAsync(
                AuditCategories.Admin, AuditActions.CostReportViewed,
                User.GetUserId(), User.GetUsername(),
                $"Filters: {Describe(query)} | total {report.Total.TotalCostThb:N4} THB " +
                $"({report.Total.TotalTokens:N0} tokens, {report.Total.MessageCount:N0} messages)",
                isSuccess: true, ct);
        }

        return Ok(report);
    }

    /// <summary>ดาวน์โหลดผลค้นหาเป็น CSV (UTF-8 with BOM เปิดใน Excel ได้ทันที)</summary>
    [HttpGet("chat/export")]
    public async Task<IActionResult> ExportChat([FromQuery] ChatLogQuery query, CancellationToken ct)
    {
        byte[] csv = await logs.ExportCsvAsync(query, ct);

        await audit.LogAsync(
            AuditCategories.Admin, AuditActions.ChatLogExported,
            User.GetUserId(), User.GetUsername(),
            $"CSV export — filters: {Describe(query)} | {csv.Length:N0} bytes", isSuccess: true, ct);

        string fileName = $"chat-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        return File(csv, "text/csv; charset=utf-8", fileName);
    }

    /// <summary>ค้นหา audit log ระดับระบบ (login, logout, ค้น log, แก้ policy ฯลฯ)</summary>
    [HttpGet("audit")]
    [ProducesResponseType(typeof(PagedResult<AuditLogItemDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AuditLogItemDto>>> SearchAudit(
        [FromQuery] AuditLogQuery query, CancellationToken ct)
        => Ok(await logs.SearchAuditAsync(query, ct));

    /// <summary>ค่าตั้งต้นสำหรับเติม dropdown ตัวกรองบนหน้าค้นหา</summary>
    [HttpGet("filters")]
    [ProducesResponseType(typeof(LogFilterOptions), StatusCodes.Status200OK)]
    public async Task<ActionResult<LogFilterOptions>> Filters(CancellationToken ct)
        => Ok(await logs.GetFilterOptionsAsync(ct));

    /// <summary>สรุปเงื่อนไขที่ใช้ค้นเป็นข้อความสั้น ๆ เพื่อเก็บลง audit log</summary>
    private static string Describe(ChatLogQuery q)
    {
        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(q.Keyword)) parts.Add($"keyword=\"{q.Keyword}\"");
        if (q.UserId is { } id) parts.Add($"userId={id}");
        if (!string.IsNullOrWhiteSpace(q.User)) parts.Add($"user=\"{q.User}\"");
        if (!string.IsNullOrWhiteSpace(q.Department)) parts.Add($"department=\"{q.Department}\"");
        if (!string.IsNullOrWhiteSpace(q.MessageRole)) parts.Add($"messageRole={q.MessageRole}");
        if (!string.IsNullOrWhiteSpace(q.QuestionType)) parts.Add($"Question type={q.QuestionType}");
        if (!string.IsNullOrWhiteSpace(q.PolicyFlag)) parts.Add($"policyFlag={q.PolicyFlag}");
        if (q.OnlyBlocked == true) parts.Add("blocked only");
        if (q.SessionId is { } sid) parts.Add($"session={sid}");
        if (q.From is { } from) parts.Add($"from={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        if (q.To is { } to) parts.Add($"to={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");

        return parts.Count == 0 ? "no filters (all records)" : string.Join(", ", parts);
    }
}
