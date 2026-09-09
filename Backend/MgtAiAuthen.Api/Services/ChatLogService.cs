using System.Globalization;
using System.Text;
using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MgtAiAuthen.Api.Services;

public interface IChatLogService
{
    Task<PagedResult<ChatLogItemDto>> SearchAsync(ChatLogQuery query, CancellationToken ct = default);
    Task<ChatLogStats> GetStatsAsync(ChatLogQuery query, CancellationToken ct = default);

    /// <summary>Token cost report broken down by user / model / department / day.</summary>
    Task<CostReport> GetCostReportAsync(ChatLogQuery query, CancellationToken ct = default);

    Task<byte[]> ExportCsvAsync(ChatLogQuery query, CancellationToken ct = default);
    Task<PagedResult<AuditLogItemDto>> SearchAuditAsync(AuditLogQuery query, CancellationToken ct = default);
    Task<LogFilterOptions> GetFilterOptionsAsync(CancellationToken ct = default);
}

/// <summary>
/// ค้นหาและสรุป log การแชท — ทุกเงื่อนไขเป็น optional และรวมกันแบบ AND
/// ตัวกรองทั้งหมดถูกแปลเป็น SQL ฝั่งฐานข้อมูล (ไม่ดึงทั้งตารางมากรองในหน่วยความจำ)
/// ใช้ navigation property (m.User / m.Session) ให้ EF สร้าง JOIN เอง
/// </summary>
public class ChatLogService(AppDbContext db) : IChatLogService
{
    private const int MaxPageSize = 200;
    private const int MaxExportRows = 20_000;

    public async Task<PagedResult<ChatLogItemDto>> SearchAsync(
        ChatLogQuery query, CancellationToken ct = default)
    {
        IQueryable<ChatMessage> filtered = BuildQuery(query);

        int total = await filtered.CountAsync(ct);
        (int page, int pageSize) = Normalize(query.Page, query.PageSize);

        List<ChatLogItemDto> items = await ApplySort(filtered, query.SortBy, query.SortDir)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new ChatLogItemDto(
                m.MessageId,
                m.SessionId,
                m.Session!.Title,
                m.UserId,
                m.User!.Username,
                m.User!.FullName,
                m.User!.Department,
                m.MessageRole,
                m.Content,
                m.QuestionType,
                m.IsBlocked,
                m.PolicyFlag,
                m.PolicyRuleName,
                m.ChatMode,
                m.ModelName,
                m.InputTokens,
                m.OutputTokens,
                m.CacheWriteTokens,
                m.CacheReadTokens,
                m.TotalTokens,
                m.InputCostUsd,
                m.OutputCostUsd,
                m.CacheCostUsd,
                m.TotalCostUsd,
                m.TotalCostThb,
                m.UsdToThbRate,
                m.LatencyMs,
                m.ClientIp,
                m.AttachmentCount,
                m.Attachments
                    .OrderBy(a => a.AttachmentId)
                    .Select(a => new ChatAttachmentDto(
                        a.AttachmentId, a.FileName, a.ContentType, a.FileKind, a.SizeBytes,
                        a.IsTextExtracted, a.PolicyScanned, a.Sha256, a.CreatedAt))
                    .ToList(),
                m.CreatedAt))
            .ToListAsync(ct);

        return new PagedResult<ChatLogItemDto>(items, page, pageSize, total);
    }

    public async Task<ChatLogStats> GetStatsAsync(ChatLogQuery query, CancellationToken ct = default)
    {
        IQueryable<ChatMessage> filtered = BuildQuery(query);

        var totals = await filtered
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Questions = g.Count(m => m.MessageRole == MessageRoles.User),
                Answers = g.Count(m => m.MessageRole == MessageRoles.Assistant),
                Blocked = g.Count(m => m.IsBlocked),
                Flagged = g.Count(m => m.PolicyFlag != null),
                Users = g.Select(m => m.UserId).Distinct().Count(),
                Sessions = g.Select(m => m.SessionId).Distinct().Count(),
                InputTokens = g.Sum(m => (long?)m.InputTokens) ?? 0L,
                OutputTokens = g.Sum(m => (long?)m.OutputTokens) ?? 0L,
                CacheWriteTokens = g.Sum(m => (long?)m.CacheWriteTokens) ?? 0L,
                CacheReadTokens = g.Sum(m => (long?)m.CacheReadTokens) ?? 0L,
                TotalTokens = g.Sum(m => (long?)m.TotalTokens) ?? 0L,
                CostUsd = g.Sum(m => m.TotalCostUsd) ?? 0m,
                CostThb = g.Sum(m => m.TotalCostThb) ?? 0m,
            })
            .FirstOrDefaultAsync(ct);

        // Question types are counted from employee messages only (AI replies have no QuestionType).
        IQueryable<ChatMessage> questions = filtered.Where(m => m.MessageRole == MessageRoles.User);

        var byType = await questions
            .GroupBy(m => m.QuestionType)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byUser = await questions
            .GroupBy(m => new { m.User!.Username, m.User!.FullName })
            .Select(g => new { g.Key.Username, g.Key.FullName, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(10)
            .ToListAsync(ct);

        var byDay = await questions
            .GroupBy(m => m.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Day)
            .Take(30)
            .ToListAsync(ct);

        return new ChatLogStats(
            TotalMessages: totals?.Total ?? 0,
            QuestionCount: totals?.Questions ?? 0,
            AnswerCount: totals?.Answers ?? 0,
            BlockedCount: totals?.Blocked ?? 0,
            FlaggedCount: totals?.Flagged ?? 0,
            DistinctUsers: totals?.Users ?? 0,
            DistinctSessions: totals?.Sessions ?? 0,
            TotalInputTokens: totals?.InputTokens ?? 0,
            TotalOutputTokens: totals?.OutputTokens ?? 0,
            TotalCacheWriteTokens: totals?.CacheWriteTokens ?? 0,
            TotalCacheReadTokens: totals?.CacheReadTokens ?? 0,
            TotalTokens: totals?.TotalTokens ?? 0,
            TotalCostUsd: totals?.CostUsd ?? 0m,
            TotalCostThb: totals?.CostThb ?? 0m,
            ByQuestionType: byType
                .OrderByDescending(t => t.Count)
                .Select(t => new NamedCount(t.Key ?? "UNKNOWN", QuestionClassifier.LabelOf(t.Key), t.Count))
                .ToList(),
            ByUser: byUser
                .Select(u => new NamedCount(u.Username, $"{u.FullName} ({u.Username})", u.Count))
                .ToList(),
            ByDay: byDay
                .OrderBy(d => d.Day)
                .Select(d => new NamedCount(
                    d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    d.Day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    d.Count))
                .ToList());
    }

    public async Task<byte[]> ExportCsvAsync(ChatLogQuery query, CancellationToken ct = default)
    {
        var rows = await ApplySort(BuildQuery(query), query.SortBy, query.SortDir)
            .Take(MaxExportRows)
            .Select(m => new
            {
                m.MessageId,
                m.CreatedAt,
                m.User!.Username,
                m.User!.FullName,
                m.User!.Department,
                m.MessageRole,
                m.QuestionType,
                m.IsBlocked,
                m.PolicyFlag,
                m.PolicyRuleName,
                m.ChatMode,
                m.ModelName,
                m.InputTokens,
                m.OutputTokens,
                m.CacheWriteTokens,
                m.CacheReadTokens,
                m.TotalTokens,
                m.InputCostUsd,
                m.OutputCostUsd,
                m.CacheCostUsd,
                m.TotalCostUsd,
                m.TotalCostThb,
                m.UsdToThbRate,
                m.LatencyMs,
                m.ClientIp,
                m.SessionId,
                m.AttachmentCount,
                AttachmentNames = string.Join(" | ", m.Attachments.Select(a => a.FileName)),
                m.Content,
            })
            .ToListAsync(ct);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', new[]
        {
            "MessageID", "Timestamp", "Username", "Full name", "Department", "Message role",
            "Question type", "Blocked", "Policy flag", "Matched rule", "Mode", "Model",
            "Tokens in", "Tokens out", "Cache write tokens", "Cache read tokens", "Total tokens",
            "Input cost (USD)", "Output cost (USD)", "Cache cost (USD)",
            "Total cost (USD)", "Total cost (THB)", "USD/THB rate",
            "Latency (ms)", "IP", "SessionID", "Attachment count", "Attachment names", "Content",
        }.Select(Escape)));

        foreach (var r in rows)
        {
            csv.AppendLine(string.Join(',', new[]
            {
                r.MessageId.ToString(CultureInfo.InvariantCulture),
                r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                r.Username,
                r.FullName,
                r.Department ?? string.Empty,
                r.MessageRole,
                QuestionClassifier.LabelOf(r.QuestionType),
                r.IsBlocked ? "Yes" : "No",
                r.PolicyFlag ?? string.Empty,
                r.PolicyRuleName ?? string.Empty,
                r.ChatMode ?? string.Empty,
                r.ModelName ?? string.Empty,
                Num(r.InputTokens),
                Num(r.OutputTokens),
                Num(r.CacheWriteTokens),
                Num(r.CacheReadTokens),
                r.TotalTokens.ToString(CultureInfo.InvariantCulture),
                Money(r.InputCostUsd, 8),
                Money(r.OutputCostUsd, 8),
                Money(r.CacheCostUsd, 8),
                Money(r.TotalCostUsd, 8),
                Money(r.TotalCostThb, 4),
                Money(r.UsdToThbRate, 4),
                Num(r.LatencyMs),
                r.ClientIp ?? string.Empty,
                r.SessionId.ToString(),
                r.AttachmentCount.ToString(CultureInfo.InvariantCulture),
                r.AttachmentNames,
                r.Content,
            }.Select(Escape)));
        }

        // Prepend a BOM so Excel on Windows renders Thai text correctly.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    public async Task<CostReport> GetCostReportAsync(ChatLogQuery query, CancellationToken ct = default)
    {
        // Costs live only on assistant rows; user messages carry no separate cost.
        //
        // ต้อง project ให้แบนก่อน GroupBy — ถ้า group ตรงจาก entity ที่ต้อง join navigation
        // EF จะแปลเป็น SQL ไม่ได้ (The LINQ expression could not be translated)
        var billable = BuildQuery(query)
            .Where(m => m.TotalCostUsd != null)
            .Select(m => new
            {
                m.User!.Username,
                m.User!.FullName,
                m.User!.Department,
                m.ModelName,
                Day = m.CreatedAt.Date,
                InputTokens = (long)(m.InputTokens ?? 0),
                OutputTokens = (long)(m.OutputTokens ?? 0),
                CacheWriteTokens = (long)(m.CacheWriteTokens ?? 0),
                CacheReadTokens = (long)(m.CacheReadTokens ?? 0),
                TotalTokens = (long)m.TotalTokens,
                CostUsd = m.TotalCostUsd ?? 0m,
                CostThb = m.TotalCostThb ?? 0m,
            });

        var totalRow = await billable
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Messages = g.Count(),
                InputTokens = g.Sum(x => x.InputTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
                CacheWrite = g.Sum(x => x.CacheWriteTokens),
                CacheRead = g.Sum(x => x.CacheReadTokens),
                TotalTokens = g.Sum(x => x.TotalTokens),
                Usd = g.Sum(x => x.CostUsd),
                Thb = g.Sum(x => x.CostThb),
                MaxThb = g.Max(x => x.CostThb),
            })
            .FirstOrDefaultAsync(ct);

        var total = new CostBucket(
            "TOTAL", "All records",
            totalRow?.Messages ?? 0,
            totalRow?.InputTokens ?? 0,
            totalRow?.OutputTokens ?? 0,
            totalRow?.CacheWrite ?? 0,
            totalRow?.CacheRead ?? 0,
            totalRow?.TotalTokens ?? 0,
            totalRow?.Usd ?? 0m,
            totalRow?.Thb ?? 0m);

        // การรวมยอดเกิดฝั่งฐานข้อมูล แต่ "การเรียงลำดับ" ต้องทำในหน่วยความจำ
        // เพราะ EF แปล OrderBy บนสมาชิกของ record ที่ project แล้วเป็น SQL ไม่ได้
        // (จำนวนกลุ่มมีแค่ระดับจำนวนคน/model/แผนก จึงไม่กระทบประสิทธิภาพ)
        List<CostBucket> byUser = SortByCost(await billable
            .GroupBy(x => new { x.Username, x.FullName })
            .Select(g => new CostBucket(
                g.Key.Username,
                g.Key.FullName + " (" + g.Key.Username + ")",
                g.Count(),
                g.Sum(x => x.InputTokens),
                g.Sum(x => x.OutputTokens),
                g.Sum(x => x.CacheWriteTokens),
                g.Sum(x => x.CacheReadTokens),
                g.Sum(x => x.TotalTokens),
                g.Sum(x => x.CostUsd),
                g.Sum(x => x.CostThb)))
            .ToListAsync(ct));

        List<CostBucket> byModel = SortByCost(await billable
            .GroupBy(x => x.ModelName)
            .Select(g => new CostBucket(
                g.Key ?? "UNKNOWN",
                g.Key ?? "Unspecifiedmodel",
                g.Count(),
                g.Sum(x => x.InputTokens),
                g.Sum(x => x.OutputTokens),
                g.Sum(x => x.CacheWriteTokens),
                g.Sum(x => x.CacheReadTokens),
                g.Sum(x => x.TotalTokens),
                g.Sum(x => x.CostUsd),
                g.Sum(x => x.CostThb)))
            .ToListAsync(ct));

        List<CostBucket> byDepartment = SortByCost(await billable
            .GroupBy(x => x.Department)
            .Select(g => new CostBucket(
                g.Key ?? "UNKNOWN",
                g.Key ?? "No department",
                g.Count(),
                g.Sum(x => x.InputTokens),
                g.Sum(x => x.OutputTokens),
                g.Sum(x => x.CacheWriteTokens),
                g.Sum(x => x.CacheReadTokens),
                g.Sum(x => x.TotalTokens),
                g.Sum(x => x.CostUsd),
                g.Sum(x => x.CostThb)))
            .ToListAsync(ct));

        var dayRows = await billable
            .GroupBy(x => x.Day)
            .Select(g => new
            {
                Day = g.Key,
                Messages = g.Count(),
                InputTokens = g.Sum(x => x.InputTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
                CacheWrite = g.Sum(x => x.CacheWriteTokens),
                CacheRead = g.Sum(x => x.CacheReadTokens),
                TotalTokens = g.Sum(x => x.TotalTokens),
                Usd = g.Sum(x => x.CostUsd),
                Thb = g.Sum(x => x.CostThb),
            })
            .OrderByDescending(g => g.Day)
            .Take(60)
            .ToListAsync(ct);

        List<CostBucket> byDay = dayRows
            .OrderBy(d => d.Day)
            .Select(d => new CostBucket(
                d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                d.Day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                d.Messages, d.InputTokens, d.OutputTokens, d.CacheWrite, d.CacheRead,
                d.TotalTokens, d.Usd, d.Thb))
            .ToList();

        return new CostReport(
            Total: total,
            ByUser: byUser,
            ByModel: byModel,
            ByDay: byDay,
            ByDepartment: byDepartment,
            AverageCostThbPerQuestion: total.MessageCount == 0
                ? 0m
                : Math.Round(total.TotalCostThb / total.MessageCount, 4, MidpointRounding.AwayFromZero),
            MaxCostThbSingleMessage: totalRow?.MaxThb ?? 0m);
    }

    /// <summary>Sorts cost groups from highest to lowest so the biggest spender shows first.</summary>
    private static List<CostBucket> SortByCost(List<CostBucket> buckets)
        => [.. buckets.OrderByDescending(b => b.TotalCostThb).ThenBy(b => b.Label)];

    private static string Num(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Money(decimal? value, int decimals)
        => value?.ToString($"F{decimals}", CultureInfo.InvariantCulture) ?? string.Empty;

    public async Task<PagedResult<AuditLogItemDto>> SearchAuditAsync(
        AuditLogQuery query, CancellationToken ct = default)
    {
        IQueryable<AuditLog> q = db.AuditLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            string keyword = query.Keyword.Trim();
            q = q.Where(a => (a.Detail != null && a.Detail.Contains(keyword)) || a.Action.Contains(keyword));
        }

        if (query.UserId is { } userId)
        {
            q = q.Where(a => a.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(query.User))
        {
            string user = query.User.Trim();
            q = q.Where(a => a.Username != null && a.Username.Contains(user));
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            string category = query.Category.Trim();
            q = q.Where(a => a.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            string action = query.Action.Trim();
            q = q.Where(a => a.Action == action);
        }

        if (query.OnlyFailed == true)
        {
            q = q.Where(a => !a.IsSuccess);
        }

        if (query.From is { } from)
        {
            q = q.Where(a => a.CreatedAt >= from.Date);
        }

        if (query.To is { } to)
        {
            DateTime upper = EndOfDay(to);
            q = q.Where(a => a.CreatedAt <= upper);
        }

        int total = await q.CountAsync(ct);
        (int page, int pageSize) = Normalize(query.Page, query.PageSize);

        List<AuditLogItemDto> items = await q
            .OrderByDescending(a => a.AuditId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditLogItemDto(
                a.AuditId, a.UserId, a.Username, a.Category, a.Action,
                a.Detail, a.IsSuccess, a.ClientIp, a.CreatedAt))
            .ToListAsync(ct);

        return new PagedResult<AuditLogItemDto>(items, page, pageSize, total);
    }

    public async Task<LogFilterOptions> GetFilterOptionsAsync(CancellationToken ct = default)
    {
        List<UserOption> users = await db.Users
            .AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new UserOption(u.UserId, u.Username, u.FullName, u.Department, u.UserRole))
            .ToListAsync(ct);

        List<string> departments = await db.Users
            .AsNoTracking()
            .Where(u => u.Department != null && u.Department != "")
            .Select(u => u.Department!)
            .Distinct()
            .OrderBy(d => d)
            .ToListAsync(ct);

        List<string> actions = await db.AuditLogs
            .AsNoTracking()
            .Select(a => a.Action)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync(ct);

        return new LogFilterOptions(
            Users: users,
            Departments: departments,
            QuestionTypes: QuestionClassifier.Labels
                .Select(kv => new NamedCount(kv.Key, kv.Value, 0))
                .ToList(),
            PolicyFlags: [Data.PolicyFlags.Block, Data.PolicyFlags.Warn, Data.PolicyFlags.Audit],
            AuditCategories: [
                Data.AuditCategories.Auth,
                Data.AuditCategories.Chat,
                Data.AuditCategories.Admin,
                Data.AuditCategories.Policy],
            AuditActions: actions);
    }

    // ------------------------------------------------------------ ภายใน

    private IQueryable<ChatMessage> BuildQuery(ChatLogQuery query)
    {
        IQueryable<ChatMessage> q = db.ChatMessages.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            string keyword = query.Keyword.Trim();
            q = q.Where(m => m.Content.Contains(keyword));
        }

        if (query.UserId is { } userId)
        {
            q = q.Where(m => m.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(query.User))
        {
            string user = query.User.Trim();
            q = q.Where(m => m.User!.Username.Contains(user) || m.User!.FullName.Contains(user));
        }

        if (!string.IsNullOrWhiteSpace(query.Department))
        {
            string department = query.Department.Trim();
            q = q.Where(m => m.User!.Department != null && m.User!.Department!.Contains(department));
        }

        if (!string.IsNullOrWhiteSpace(query.MessageRole))
        {
            string role = query.MessageRole.Trim();
            q = q.Where(m => m.MessageRole == role);
        }

        if (!string.IsNullOrWhiteSpace(query.QuestionType))
        {
            string questionType = query.QuestionType.Trim();
            q = q.Where(m => m.QuestionType == questionType);
        }

        if (!string.IsNullOrWhiteSpace(query.PolicyFlag))
        {
            string flag = query.PolicyFlag.Trim();
            q = q.Where(m => m.PolicyFlag == flag);
        }

        if (query.OnlyBlocked == true)
        {
            q = q.Where(m => m.IsBlocked);
        }

        if (query.HasAttachments == true)
        {
            q = q.Where(m => m.AttachmentCount > 0);
        }

        if (!string.IsNullOrWhiteSpace(query.ChatMode))
        {
            string mode = query.ChatMode.Trim();
            q = q.Where(m => m.ChatMode == mode);
        }

        if (query.SessionId is { } sessionId)
        {
            q = q.Where(m => m.SessionId == sessionId);
        }

        if (query.From is { } from)
        {
            q = q.Where(m => m.CreatedAt >= from.Date);
        }

        if (query.To is { } to)
        {
            DateTime upper = EndOfDay(to);
            q = q.Where(m => m.CreatedAt <= upper);
        }

        return q;
    }

    private static IQueryable<ChatMessage> ApplySort(
        IQueryable<ChatMessage> q, string? sortBy, string? sortDir)
    {
        bool descending = !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "Username" => descending
                ? q.OrderByDescending(m => m.User!.Username).ThenByDescending(m => m.MessageId)
                : q.OrderBy(m => m.User!.Username).ThenBy(m => m.MessageId),
            "QuestionType" => descending
                ? q.OrderByDescending(m => m.QuestionType).ThenByDescending(m => m.MessageId)
                : q.OrderBy(m => m.QuestionType).ThenBy(m => m.MessageId),
            "OutputTokens" => descending
                ? q.OrderByDescending(m => m.OutputTokens).ThenByDescending(m => m.MessageId)
                : q.OrderBy(m => m.OutputTokens).ThenBy(m => m.MessageId),
            "LatencyMs" => descending
                ? q.OrderByDescending(m => m.LatencyMs).ThenByDescending(m => m.MessageId)
                : q.OrderBy(m => m.LatencyMs).ThenBy(m => m.MessageId),
            // MessageID เรียงตามลำดับการบันทึกจริง จึงใช้แทน CreatedAt ได้และเร็วกว่า
            _ => descending
                ? q.OrderByDescending(m => m.MessageId)
                : q.OrderBy(m => m.MessageId),
        };
    }

    /// <summary>When a date is given with no time, extend the range to the end of that day.</summary>
    private static DateTime EndOfDay(DateTime value)
        => value.TimeOfDay == TimeSpan.Zero ? value.Date.AddDays(1).AddSeconds(-1) : value;

    private static (int Page, int PageSize) Normalize(int page, int pageSize)
        => (page < 1 ? 1 : page, Math.Clamp(pageSize, 1, MaxPageSize));

    private static string Escape(string? value)
    {
        string text = value ?? string.Empty;

        // ป้องกัน CSV/formula injection เมื่อเปิดไฟล์ใน Excel
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@')
        {
            text = "'" + text;
        }

        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}
