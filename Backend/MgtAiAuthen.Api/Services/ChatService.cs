using System.Text;
using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using MgtAiAuthen.Api.Options;
using MgtAiAuthen.Api.Security;
using MgtAiAuthen.Api.Services.DataSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Services;

public interface IChatService
{
    Task<IReadOnlyList<ChatSessionDto>> GetSessionsAsync(int userId, CancellationToken ct = default);

    Task<IReadOnlyList<ChatMessageDto>> GetMessagesAsync(
        Guid sessionId, int userId, bool canReadAllLogs, CancellationToken ct = default);

    /// <summary><paramref name="files"/> เป็น null หรือว่างได้ — ใช้เส้นทางเดียวกันทั้งมีและไม่มีไฟล์แนบ</summary>
    Task<ChatSendResponse> SendAsync(
        int userId,
        ChatSendRequest request,
        IReadOnlyList<IFormFile>? files = null,
        CancellationToken ct = default);

    Task DeleteSessionAsync(Guid sessionId, int userId, CancellationToken ct = default);

    /// <summary>Owner sees it always; Admin/Auditor see any session's status for oversight.</summary>
    Task<DataSourceFetchStatusDto> GetDataSourceStatusAsync(
        Guid sessionId, int userId, bool canReadAllLogs, CancellationToken ct = default);

    /// <summary>Owner only — re-pulls the conversation's Data Source and overwrites the cached fetch.</summary>
    Task<DataSourceFetchStatusDto> RefreshDataSourceAsync(
        Guid sessionId, int userId, string username, CancellationToken ct = default);

    /// <summary>Reads an attachment for download — owner or Admin/Auditor only.</summary>
    Task<(ChatAttachment Meta, byte[] Content)> GetAttachmentAsync(
        long attachmentId, int userId, bool canReadAllLogs, CancellationToken ct = default);

    /// <summary>Models the user may choose from — every model that has active pricing.</summary>
    Task<IReadOnlyList<AvailableModelDto>> GetAvailableModelsAsync(CancellationToken ct = default);
}

public class ChatService(
    AppDbContext db,
    IAiClient ai,
    IPolicyService policy,
    ICostCalculator costCalculator,
    IAttachmentService attachments,
    IDataSourceService dataSources,
    IAuditService audit,
    IHttpContextAccessor httpContextAccessor,
    IOptions<ClaudeOptions> claudeOptions,
    ILogger<ChatService> logger) : IChatService
{
    private const int TitleMaxLength = 100;

    private readonly ClaudeOptions _options = claudeOptions.Value;

    public async Task<IReadOnlyList<ChatSessionDto>> GetSessionsAsync(int userId, CancellationToken ct = default)
        => await db.ChatSessions
            .AsNoTracking()
            .Where(s => s.UserId == userId && !s.IsDeleted)
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new ChatSessionDto(
                s.SessionId, s.Title, s.MessageCount,
                s.ProjectId, s.Project == null ? null : s.Project.Name,
                s.DataSourceId, s.DataSource == null ? null : s.DataSource.SourceName,
                s.CreatedAt, s.UpdatedAt))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ChatMessageDto>> GetMessagesAsync(
        Guid sessionId, int userId, bool canReadAllLogs, CancellationToken ct = default)
    {
        ChatSession session = await db.ChatSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct)
            ?? throw AppException.NotFound("Conversation not found");

        // เจ้าของเห็นได้เสมอ; Admin/Auditor เห็นของทุกคนเพื่อตรวจสอบ
        if (session.UserId != userId && !canReadAllLogs)
        {
            throw AppException.Forbidden("You do not have permission to view this conversation");
        }

        return await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.MessageId)
            .Select(m => new ChatMessageDto(
                m.MessageId, m.MessageRole, m.Content, m.QuestionType, m.IsBlocked,
                m.PolicyFlag, m.PolicyRuleName, m.ChatMode, m.ModelName, m.InputTokens, m.OutputTokens,
                m.CacheWriteTokens, m.CacheReadTokens, m.TotalTokens,
                m.TotalCostUsd, m.TotalCostThb, m.LatencyMs,
                m.Attachments
                    .OrderBy(a => a.AttachmentId)
                    .Select(a => new ChatAttachmentDto(
                        a.AttachmentId, a.FileName, a.ContentType, a.FileKind, a.SizeBytes,
                        a.IsTextExtracted, a.PolicyScanned, a.Sha256, a.CreatedAt))
                    .ToList(),
                m.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<ChatSendResponse> SendAsync(
        int userId,
        ChatSendRequest request,
        IReadOnlyList<IFormFile>? files = null,
        CancellationToken ct = default)
    {
        string content = request.Message?.Trim() ?? string.Empty;
        List<IFormFile> incoming = files?.Where(f => f.Length > 0).ToList() ?? [];

        // ลากไฟล์มาเฉย ๆ โดยไม่พิมพ์อะไรถือว่าใช้ได้ — ระบบจะใส่คำสั่ง "ช่วยวิเคราะห์ไฟล์" ให้
        if (content.Length == 0 && incoming.Count == 0)
        {
            throw new AppException("Please type a message or attach a file to analyse");
        }

        AppUser user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, ct)
            ?? throw AppException.NotFound("User not found");

        ChatSession session = await ResolveSessionAsync(
            userId, request.SessionId, request.ProjectId, request.DataSourceId, user.Username, ct);

        List<StoredFile> storedFiles = [];
        if (incoming.Count > 0)
        {
            try
            {
                storedFiles = await attachments.StoreAsync(incoming, ct);
            }
            catch (AppException ex)
            {
                await audit.LogAsync(AuditCategories.Chat, AuditActions.FileRejected, userId, user.Username,
                    $"Attachment rejected: {ex.Message} (files: {string.Join(", ", incoming.Select(f => f.FileName))})",
                    isSuccess: false, ct);
                throw;
            }
        }

        ResolvedModel chosen = await ResolveModelAsync(request.Model, session.SessionId, ct);
        string model = chosen.Name;
        string mode = await ResolveModeAsync(request.Mode, session.SessionId, ct);

        string? questionType = QuestionClassifier.Classify(content);

        // Screen the message text, every file name, and the content of readable text files
        // (PDF/images cannot be read, so only their names are screened — see the PolicyScanned column).
        PolicyDecision decision = await policy.EvaluateAsync(BuildPolicyInput(content, storedFiles), ct);

        HttpContext? http = httpContextAccessor.HttpContext;

        var userMessage = new ChatMessage
        {
            SessionId = session.SessionId,
            UserId = userId,
            MessageRole = MessageRoles.User,
            Content = content.Length > 0 ? content : BuildFileOnlyContent(storedFiles),
            QuestionType = questionType,
            ChatMode = mode,
            IsBlocked = decision.IsBlocked,
            PolicyFlag = decision.Flag,
            PolicyRuleId = decision.RuleId,
            PolicyRuleName = decision.RuleName,
            ClientIp = http?.GetClientIp(),
            UserAgent = http?.GetUserAgent(),
            AttachmentCount = storedFiles.Count,
            CreatedAt = DateTime.Now,
        };

        db.ChatMessages.Add(userMessage);

        if (session.MessageCount == 0)
        {
            session.Title = BuildTitle(userMessage.Content);
        }

        session.MessageCount++;
        session.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);

        // ต้อง save ข้อความก่อนจึงจะมี MessageID ไปผูกกับไฟล์แนบได้
        List<ChatAttachment> attachmentRows = [];

        if (storedFiles.Count > 0)
        {
            foreach (StoredFile file in storedFiles)
            {
                var row = new ChatAttachment
                {
                    MessageId = userMessage.MessageId,
                    SessionId = session.SessionId,
                    UserId = userId,
                    FileName = file.FileName,
                    ContentType = file.ContentType,
                    FileKind = file.FileKind,
                    SizeBytes = file.SizeBytes,
                    Sha256 = file.Sha256,
                    StoredPath = file.RelativePath,
                    IsTextExtracted = file.ExtractedText is not null,
                    ExtractedChars = file.ExtractedText?.Length,
                    PolicyScanned = FileKinds.CarriesText(file.FileKind),
                    CreatedAt = DateTime.Now,
                };

                attachmentRows.Add(row);
                db.ChatAttachments.Add(row);
            }

            await db.SaveChangesAsync(ct);

            await audit.LogAsync(AuditCategories.Chat, AuditActions.FileUploaded, userId, user.Username,
                $"Attached {storedFiles.Count} file(s) to message {userMessage.MessageId}: " +
                string.Join(" | ", storedFiles.Select(f =>
                    $"{f.FileName} ({f.FileKind}, {f.SizeBytes / 1024.0:F1} KB, sha256 {f.Sha256[..12]}…" +
                    $"{(FileKinds.CarriesText(f.FileKind) ? ", content scanned" : ", file name only scanned")})")),
                isSuccess: true, ct);
        }

        if (decision.IsBlocked)
        {
            await audit.LogAsync(AuditCategories.Chat, AuditActions.ChatBlocked, userId, user.Username,
                $"Rule \"{decision.RuleName}\" (severity {decision.Severity}) blocked a message in session {session.SessionId}",
                isSuccess: false, ct);

            return new ChatSendResponse(
                session.SessionId, session.Title, ToDto(userMessage, attachmentRows),
                AssistantMessage: null, Blocked: true, PolicyNotice: decision.Notice);
        }

        if (decision.Flag is not null)
        {
            await audit.LogAsync(AuditCategories.Chat, AuditActions.ChatFlagged, userId, user.Username,
                $"Rule \"{decision.RuleName}\" (severity {decision.Severity}, action {decision.Flag}) " +
                $"flagged message {userMessage.MessageId}",
                isSuccess: true, ct);
        }

        List<ChatTurn> turns = await BuildHistoryAsync(session.SessionId, session.ProjectId, session.DataSourceId, ct);

        AiReply reply;
        try
        {
            reply = await ai.CompleteAsync(
                await BuildSystemPromptAsync(user, mode, session.ProjectId, ct), turns, model, chosen.Provider, ct);
        }
        catch (AiUnavailableException)
        {
            await audit.LogAsync(AuditCategories.Chat, AuditActions.ChatFailed, userId, user.Username,
                $"{chosen.Provider} API key is not configured (model {model})", isSuccess: false, ct);
            throw;
        }
        catch (AiBillingException ex)
        {
            await audit.LogAsync(AuditCategories.Chat, AuditActions.ChatFailed, userId, user.Username,
                $"{chosen.Provider} account/credit problem on model {model}: " +
                $"{ex.InnerException?.Message ?? ex.Message} — " +
                "an administrator must check the credit balance and API key",
                isSuccess: false, ct);
            throw;
        }
        catch (AiInvalidInputException ex)
        {
            await audit.LogAsync(AuditCategories.Chat, AuditActions.ChatFailed, userId, user.Username,
                $"{chosen.Provider} rejected the request (400) on model {model}: " +
                $"{ex.InnerException?.Message ?? ex.Message} — " +
                $"session {session.SessionId}, {storedFiles.Count} attachment(s)",
                isSuccess: false, ct);
            throw;
        }
        catch (AiOverloadedException)
        {
            // The user message was already stored before the AI call, so resending is safe.
            await audit.LogAsync(AuditCategories.Chat, AuditActions.ChatFailed, userId, user.Username,
                $"{chosen.Provider} was overloaded after retries on model {model} — session {session.SessionId}",
                isSuccess: false, ct);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Provider} API call failed (session {SessionId})",
                chosen.Provider, session.SessionId);
            await audit.LogAsync(AuditCategories.Chat, AuditActions.ChatFailed, userId, user.Username,
                $"{chosen.Provider} API call failed: {ex.GetType().Name} — {ex.Message}", isSuccess: false, ct);
            throw new AppException(
                "The AI call failed. Please try again; if the problem persists, contact your system administrator",
                System.Net.HttpStatusCode.BadGateway, "AI_CALL_FAILED");
        }

        // Compute the cost now and store it on the row, so later price changes never rewrite history.
        TokenCost cost = await costCalculator.CalculateAsync(
            reply.ModelName, reply.InputTokens, reply.OutputTokens,
            reply.CacheWriteTokens, reply.CacheReadTokens, ct);

        var assistantMessage = new ChatMessage
        {
            SessionId = session.SessionId,
            UserId = userId,
            MessageRole = MessageRoles.Assistant,
            Content = reply.Text,
            ChatMode = mode,
            ModelName = reply.ModelName,
            InputTokens = reply.InputTokens,
            OutputTokens = reply.OutputTokens,
            CacheWriteTokens = reply.CacheWriteTokens,
            CacheReadTokens = reply.CacheReadTokens,
            InputCostUsd = cost.InputUsd,
            OutputCostUsd = cost.OutputUsd,
            CacheCostUsd = cost.CacheUsd,
            TotalCostUsd = cost.TotalUsd,
            TotalCostThb = cost.TotalThb,
            UsdToThbRate = cost.UsdToThbRate,
            PricingId = cost.PricingId,
            LatencyMs = reply.LatencyMs,
            ClientIp = http?.GetClientIp(),
            UserAgent = http?.GetUserAgent(),
            CreatedAt = DateTime.Now,
        };

        db.ChatMessages.Add(assistantMessage);

        // session ถูก track อยู่แล้วจาก ResolveSessionAsync — แก้ค่าแล้ว save ได้เลย
        session.MessageCount++;
        session.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Chat, AuditActions.ChatSent, userId, user.Username,
            $"Mode {mode} | Question type {questionType ?? "-"} | model {reply.ModelName} | " +
            $"provider {chosen.Provider} | " +
            $"tokens in {reply.InputTokens} out {reply.OutputTokens} " +
            $"cache write {reply.CacheWriteTokens} read {reply.CacheReadTokens} | " +
            $"cost {cost.TotalUsd:F6} USD ({cost.TotalThb:F4} THB) | " +
            $"{reply.LatencyMs} ms | session {session.SessionId}",
            isSuccess: true, ct);

        return new ChatSendResponse(
            session.SessionId, session.Title, ToDto(userMessage, attachmentRows), ToDto(assistantMessage),
            Blocked: false, PolicyNotice: decision.Notice);
    }

    public async Task DeleteSessionAsync(Guid sessionId, int userId, CancellationToken ct = default)
    {
        ChatSession session = await db.ChatSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct)
            ?? throw AppException.NotFound("Conversation not found");

        if (session.UserId != userId)
        {
            throw AppException.Forbidden("You can only delete your own conversations");
        }

        // ซ่อนจากผู้ใช้เท่านั้น — ข้อความยังอยู่ในฐานข้อมูลเพื่อการตรวจสอบย้อนหลัง
        session.IsDeleted = true;
        session.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);

        string? username = await db.Users.Where(u => u.UserId == userId)
            .Select(u => u.Username).FirstOrDefaultAsync(ct);

        await audit.LogAsync(AuditCategories.Chat, AuditActions.SessionDeleted, userId, username,
            $"Hid conversation {sessionId} (messages remain in the log)", isSuccess: true, ct);
    }

    public async Task<DataSourceFetchStatusDto> GetDataSourceStatusAsync(
        Guid sessionId, int userId, bool canReadAllLogs, CancellationToken ct = default)
    {
        ChatSession session = await db.ChatSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct)
            ?? throw AppException.NotFound("Conversation not found");

        if (session.UserId != userId && !canReadAllLogs)
        {
            throw AppException.Forbidden("You do not have permission to view this conversation");
        }

        return await dataSources.GetFetchStatusAsync(sessionId, ct);
    }

    public async Task<DataSourceFetchStatusDto> RefreshDataSourceAsync(
        Guid sessionId, int userId, string username, CancellationToken ct = default)
    {
        ChatSession session = await db.ChatSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct)
            ?? throw AppException.NotFound("Conversation not found");

        if (session.UserId != userId)
        {
            throw AppException.Forbidden("You can only refresh your own conversations");
        }

        if (session.DataSourceId is not { } sourceId)
        {
            throw new AppException("This conversation has no Data Source attached.");
        }

        return await dataSources.FetchForSessionAsync(sessionId, sourceId, userId, username, ct);
    }

    private async Task<ChatSession> ResolveSessionAsync(
        int userId, Guid? sessionId, int? projectId, int? dataSourceId, string username, CancellationToken ct)
    {
        if (sessionId is null)
        {
            int? resolvedProjectId = null;

            if (projectId is { } wantedProjectId)
            {
                Project project = await db.Projects.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ProjectId == wantedProjectId && !p.IsDeleted, ct)
                    ?? throw AppException.NotFound("Project not found");

                bool isAdmin = httpContextAccessor.HttpContext?.User.IsInRole(UserRoles.Admin) ?? false;
                if (project.OwnerUserId != userId && !isAdmin && !project.IsShared)
                {
                    throw AppException.Forbidden("This project has not been shared with you");
                }

                resolvedProjectId = project.ProjectId;
            }

            int? resolvedDataSourceId = null;

            if (dataSourceId is { } wantedSourceId)
            {
                bool hasGrant = await db.DataSourceGrants.AsNoTracking()
                    .AnyAsync(g => g.SourceId == wantedSourceId && g.UserId == userId && g.IsActive, ct);
                if (!hasGrant)
                {
                    throw AppException.Forbidden("You do not have access to this data source");
                }

                bool sourceActive = await db.DataSources.AsNoTracking()
                    .AnyAsync(s => s.SourceId == wantedSourceId && s.IsActive, ct);
                if (!sourceActive)
                {
                    throw new AppException("This data source has been deactivated by an administrator");
                }

                resolvedDataSourceId = wantedSourceId;
            }

            var created = new ChatSession
            {
                SessionId = Guid.NewGuid(),
                UserId = userId,
                Title = "New conversation",
                ProjectId = resolvedProjectId,
                DataSourceId = resolvedDataSourceId,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            };
            db.ChatSessions.Add(created);
            await db.SaveChangesAsync(ct);

            if (resolvedDataSourceId is { } sourceId)
            {
                // Pull data once up front so the very first question already has it as context.
                // A failure here must not block starting the conversation — the fetch-status
                // endpoint surfaces the failure, and the user can retry with "Refresh".
                try
                {
                    await dataSources.FetchForSessionAsync(created.SessionId, sourceId, userId, username, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Initial data source fetch failed for session {SessionId} (source {SourceId})",
                        created.SessionId, sourceId);
                }
            }

            return created;
        }

        ChatSession session = await db.ChatSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct)
            ?? throw AppException.NotFound("Conversation not found");

        if (session.UserId != userId)
        {
            throw AppException.Forbidden("You do not have permission to post in this conversation");
        }

        return session;
    }

    /// <summary>
    /// ดึงประวัติล่าสุดเป็นบริบท — ข้ามข้อความที่ถูก policy ระงับ (ไม่เคยส่งให้ AI)
    /// and drops any leading assistant message, because the API must start with a user turn.
    ///
    /// Attachments for every turn in the window are read from disk and resent so follow-up
    /// questions about the same document work — prompt caching in ClaudeClient keeps that cheap.
    /// </summary>
    private async Task<List<ChatTurn>> BuildHistoryAsync(
        Guid sessionId, int? projectId, int? dataSourceId, CancellationToken ct)
    {
        // Project reference files are resent as a synthetic leading turn pair — the same
        // TurnAttachment mechanism every provider already handles for a message's own
        // attachments, so no provider-specific code was needed to support this. They ride ahead
        // of the real history so the model has read them before the first real question, and are
        // resent every call the same way a chat attachment is (prompt caching already covers the
        // cost of that — see README 6.2).
        List<ChatTurn> projectTurns = await BuildProjectContextTurnsAsync(projectId, ct);

        // Data pulled from a Data Source (cached at session start / on "Refresh") rides the same
        // synthetic-turn mechanism, right after the project's own reference files.
        List<ChatTurn> dataSourceTurns = await BuildDataSourceContextTurnsAsync(sessionId, dataSourceId, ct);

        List<ChatMessage> recent = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && !m.IsBlocked && m.MessageRole != MessageRoles.System)
            .OrderByDescending(m => m.MessageId)
            .Take(Math.Max(2, _options.HistoryTurns))
            .ToListAsync(ct);

        recent.Reverse();

        int start = recent.FindIndex(m => m.MessageRole == MessageRoles.User);
        if (start < 0)
        {
            return [];
        }

        List<ChatMessage> window = recent.Skip(start).ToList();

        // โหลด metadata ของไฟล์แนบทั้งหน้าต่างในคำสั่งเดียว เลี่ยงปัญหา N+1
        long[] withFiles = window.Where(m => m.AttachmentCount > 0).Select(m => m.MessageId).ToArray();

        Dictionary<long, List<ChatAttachment>> byMessage = withFiles.Length == 0
            ? []
            : (await db.ChatAttachments
                .AsNoTracking()
                .Where(a => withFiles.Contains(a.MessageId))
                .OrderBy(a => a.AttachmentId)
                .ToListAsync(ct))
                .GroupBy(a => a.MessageId)
                .ToDictionary(g => g.Key, g => g.ToList());

        List<ChatTurn> turns = [];

        foreach (ChatMessage message in window)
        {
            List<TurnAttachment>? turnFiles = null;

            if (byMessage.TryGetValue(message.MessageId, out List<ChatAttachment>? metas))
            {
                turnFiles = [];

                foreach (ChatAttachment meta in metas)
                {
                    try
                    {
                        byte[] bytes = await attachments.ReadAsync(meta.StoredPath, ct);
                        turnFiles.Add(new TurnAttachment(
                            meta.FileName,
                            meta.ContentType,
                            meta.FileKind,
                            bytes,
                            // Same conversion the upload path used — a workbook must be
                            // re-extracted here, not UTF-8 decoded.
                            attachments.ExtractText(meta.FileKind, bytes, meta.FileName)));
                    }
                    catch (Exception ex)
                    {
                        // ไฟล์หายจาก disk ไม่ควรทำให้แชทต่อไม่ได้ — ข้ามไฟล์นั้นแล้วเตือนใน log
                        logger.LogWarning(ex,
                            "Failed to read attachment {AttachmentId} ({Path}) — it was not sent to the AI",
                            meta.AttachmentId, meta.StoredPath);
                    }
                }
            }

            turns.Add(new ChatTurn(message.MessageRole, message.Content, turnFiles));
        }

        return [.. projectTurns, .. dataSourceTurns, .. turns];
    }

    /// <summary>
    /// Builds the leading turn pair carrying a project's reference files, or an empty list when
    /// the session has no project or the project has no files yet.
    ///
    /// A user turn holding the files, followed by a short assistant acknowledgement, keeps the
    /// provider's "must start with a user turn" rule satisfied even though nothing has been asked
    /// yet — the real first question still arrives as its own later user turn.
    /// </summary>
    private async Task<List<ChatTurn>> BuildProjectContextTurnsAsync(int? projectId, CancellationToken ct)
    {
        if (projectId is not { } id)
        {
            return [];
        }

        List<ProjectFile> files = await db.ProjectFiles
            .AsNoTracking()
            .Where(f => f.ProjectId == id)
            .OrderBy(f => f.ProjectFileId)
            .ToListAsync(ct);

        if (files.Count == 0)
        {
            return [];
        }

        var turnFiles = new List<TurnAttachment>();

        foreach (ProjectFile file in files)
        {
            try
            {
                byte[] bytes = await attachments.ReadAsync(file.StoredPath, ct);
                turnFiles.Add(new TurnAttachment(
                    file.FileName, file.ContentType, file.FileKind, bytes,
                    attachments.ExtractText(file.FileKind, bytes, file.FileName)));
            }
            catch (Exception ex)
            {
                // เหมือนไฟล์แนบระดับข้อความ — ไฟล์หายจาก disk ไม่ควรทำให้แชททั้ง Project ใช้ไม่ได้
                logger.LogWarning(ex,
                    "Failed to read project file {ProjectFileId} ({Path}) — it was not sent to the AI",
                    file.ProjectFileId, file.StoredPath);
            }
        }

        if (turnFiles.Count == 0)
        {
            return [];
        }

        return
        [
            new ChatTurn(MessageRoles.User, "Reference materials for this project are attached below.", turnFiles),
            new ChatTurn(MessageRoles.Assistant, "Understood — I have the project's reference files as context."),
        ];
    }

    /// <summary>
    /// Builds the leading turn pair carrying the session's cached Data Source fetch, or an empty
    /// list when there is no Data Source, no fetch has succeeded yet, or the fetch found nothing
    /// readable. The content itself was already pulled (at session creation, or by "Refresh") —
    /// this only resends the cached result, the same way project files are resent every call.
    /// </summary>
    private async Task<List<ChatTurn>> BuildDataSourceContextTurnsAsync(
        Guid sessionId, int? dataSourceId, CancellationToken ct)
    {
        if (dataSourceId is null)
        {
            return [];
        }

        ChatSessionDataFetch? fetch = await db.ChatSessionDataFetches
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.SessionId == sessionId, ct);

        if (fetch is null || !fetch.Success || string.IsNullOrEmpty(fetch.ContentText))
        {
            return [];
        }

        DataSource? source = await db.DataSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SourceId == fetch.SourceId, ct);

        var turnFiles = new List<TurnAttachment>
        {
            new(
                $"{source?.SourceName ?? "data-source"}.txt", "text/plain", FileKinds.Text,
                Encoding.UTF8.GetBytes(fetch.ContentText), fetch.ContentText),
        };

        return
        [
            new ChatTurn(MessageRoles.User,
                $"Data pulled from \"{source?.SourceName}\" is attached below (fetched {fetch.FetchedAt:yyyy-MM-dd HH:mm}).",
                turnFiles),
            new ChatTurn(MessageRoles.Assistant, "Understood — I have the data source's content as context."),
        ];
    }

    /// <summary>
    /// The text screened against policy: the user message, every file name,
    /// and the content of readable text files, so sensitive data cannot slip out via an attachment.
    /// </summary>
    private static string BuildPolicyInput(string content, List<StoredFile> files)
    {
        if (files.Count == 0)
        {
            return content;
        }

        var builder = new System.Text.StringBuilder(content);

        foreach (StoredFile file in files)
        {
            builder.AppendLine().Append(file.FileName);

            if (file.ExtractedText is { Length: > 0 } text)
            {
                builder.AppendLine().Append(text);
            }
        }

        return builder.ToString();
    }

    /// <summary>ข้อความที่บันทึกไว้เมื่อผู้ใช้ลากไฟล์มาโดยไม่พิมพ์อะไร</summary>
    private static string BuildFileOnlyContent(List<StoredFile> files)
        => $"[Attached for analysis: {string.Join(", ", files.Select(f => f.FileName))}]";

    public async Task<IReadOnlyList<AvailableModelDto>> GetAvailableModelsAsync(
        CancellationToken ct = default)
    {
        List<ModelPricing> priced = await db.ModelPricing
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.InputUsdPerMTok)
            .ToListAsync(ct);

        string fallback = ai.DefaultModel;

        List<AvailableModelDto> models = priced
            .Select(p => new AvailableModelDto(
                Name: p.ModelName,
                Provider: AiProviders.Normalise(p.Provider),
                // Whether the vendor actually has a key. Shown in the picker so nobody discovers
                // a missing key only after typing a question and waiting for a 503.
                ProviderReady: ai.IsProviderReady(p.Provider),
                IsDefault: string.Equals(p.ModelName, fallback, StringComparison.OrdinalIgnoreCase),
                InputUsdPerMTok: p.InputUsdPerMTok,
                OutputUsdPerMTok: p.OutputUsdPerMTok,
                UsdToThbRate: p.UsdToThbRate,
                Notes: p.Notes,
                SampleCostThb: SampleCostThb(p)))
            .ToList();

        // The configured default must always be selectable, even if nobody has priced it —
        // otherwise the picker could offer no way back to the model the system actually uses.
        if (!models.Any(m => m.IsDefault))
        {
            models.Insert(0, new AvailableModelDto(
                fallback, ai.DefaultProvider, ai.IsProviderReady(ai.DefaultProvider), true, 0m, 0m, 0m,
                "No pricing configured — usage is logged but billed as 0", 0m));
        }

        return models;
    }

    /// <summary>Indicative cost of one typical question (700 tokens in, 600 out).</summary>
    private static decimal SampleCostThb(ModelPricing p)
        => Math.Round(
            (700m / 1_000_000m * p.InputUsdPerMTok + 600m / 1_000_000m * p.OutputUsdPerMTok)
            * p.UsdToThbRate, 4, MidpointRounding.AwayFromZero);

    /// <summary>A model together with the vendor that has to be called for it.</summary>
    private record ResolvedModel(string Name, string Provider);

    /// <summary>
    /// Works out which model answers this message, and which provider owns it: an explicit choice
    /// wins, otherwise the conversation keeps the model it already used, otherwise the configured
    /// default. An unknown or unpriced name is rejected rather than silently downgraded, so nobody
    /// is billed for a model they did not choose.
    /// </summary>
    private async Task<ResolvedModel> ResolveModelAsync(string? requested, Guid sessionId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string wanted = requested.Trim();

            ModelPricing? priced = await db.ModelPricing
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.IsActive && p.ModelName == wanted, ct);

            if (priced is not null)
            {
                return new ResolvedModel(priced.ModelName, AiProviders.Normalise(priced.Provider));
            }

            // The system default stays usable even with no pricing row of its own.
            if (string.Equals(wanted, ai.DefaultModel, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedModel(ai.DefaultModel, ai.DefaultProvider);
            }

            List<string> available = await db.ModelPricing
                .AsNoTracking()
                .Where(p => p.IsActive)
                .Select(p => p.ModelName)
                .ToListAsync(ct);

            throw new AppException(
                $"\"{wanted}\" is not an available model. Choose one of: " +
                string.Join(", ", available.Append(ai.DefaultModel).Distinct()));
        }

        // Keep the conversation on whatever model already answered in it.
        string? lastUsed = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.ModelName != null)
            .OrderByDescending(m => m.MessageId)
            .Select(m => m.ModelName)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(lastUsed))
        {
            return new ResolvedModel(ai.DefaultModel, ai.DefaultProvider);
        }

        return await DescribeModelAsync(lastUsed, ct);
    }

    /// <summary>
    /// Finds the provider for a model name taken from the log rather than from the picker.
    ///
    /// The stored name can be a dated snapshot the API echoed back ("claude-haiku-4-5-20251001"),
    /// which matches no pricing row exactly — hence the longest-prefix fallback, the same rule the
    /// cost calculator uses. If the provider still cannot be established (its pricing was
    /// deactivated since), the conversation falls back to the system default instead of guessing:
    /// sending a Gemini model to Anthropic would fail, and the reverse would bill the wrong account.
    /// </summary>
    private async Task<ResolvedModel> DescribeModelAsync(string modelName, CancellationToken ct)
    {
        List<ModelPricing> active = await db.ModelPricing
            .AsNoTracking()
            .Where(p => p.IsActive)
            .ToListAsync(ct);

        ModelPricing? match =
            active.FirstOrDefault(p => string.Equals(p.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
            ?? active
                .Where(p => modelName.StartsWith(p.ModelName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.ModelName.Length)
                .FirstOrDefault();

        if (match is not null)
        {
            return new ResolvedModel(modelName, AiProviders.Normalise(match.Provider));
        }

        if (string.Equals(modelName, ai.DefaultModel, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedModel(ai.DefaultModel, ai.DefaultProvider);
        }

        logger.LogWarning(
            "Model {Model} was used earlier in this conversation but has no active pricing now, so " +
            "its provider is unknown — falling back to the default model {Default}",
            modelName, ai.DefaultModel);

        return new ResolvedModel(ai.DefaultModel, ai.DefaultProvider);
    }

    public async Task<(ChatAttachment Meta, byte[] Content)> GetAttachmentAsync(
        long attachmentId, int userId, bool canReadAllLogs, CancellationToken ct = default)
    {
        ChatAttachment meta = await db.ChatAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AttachmentId == attachmentId, ct)
            ?? throw AppException.NotFound("Attachment not found");

        if (meta.UserId != userId && !canReadAllLogs)
        {
            throw AppException.Forbidden("You do not have permission to open this attachment");
        }

        byte[] content = await attachments.ReadAsync(meta.StoredPath, ct);

        string? username = await db.Users.Where(u => u.UserId == userId)
            .Select(u => u.Username).FirstOrDefaultAsync(ct);

        await audit.LogAsync(AuditCategories.Chat, AuditActions.FileDownloaded, userId, username,
            $"Opened attachment {meta.AttachmentId} \"{meta.FileName}\" belonging to user {meta.UserId} " +
            $"(sha256 {meta.Sha256[..12]}…)", isSuccess: true, ct);

        return (meta, content);
    }

    /// <summary>
    /// ใส่ตัวตนของผู้ถามลงใน system prompt ด้วย เพื่อให้ AI ตอบได้ตรงบริบทแผนก
    /// และให้ชัดว่าทุกบทสนทนาผูกกับพนักงานที่ล็อกอินอยู่
    /// </summary>
    /// <summary>
    /// Resolves the mode for this message: an explicit choice wins, otherwise the conversation
    /// keeps the mode it last used, otherwise Chat. Unknown values are rejected by the DTO's
    /// regular expression before reaching here, so this only has to normalise casing.
    /// </summary>
    private async Task<string> ResolveModeAsync(string? requested, Guid sessionId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return ChatModes.Normalise(requested);
        }

        string? lastUsed = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.ChatMode != null)
            .OrderByDescending(m => m.MessageId)
            .Select(m => m.ChatMode)
            .FirstOrDefaultAsync(ct);

        return ChatModes.Normalise(lastUsed);
    }

    private async Task<string> BuildSystemPromptAsync(
        AppUser user, string mode, int? projectId, CancellationToken ct)
    {
        string template = BuildSystemPrompt(user, mode);

        if (projectId is { } id)
        {
            string? instructions = await db.Projects.AsNoTracking()
                .Where(p => p.ProjectId == id)
                .Select(p => p.Instructions)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(instructions))
            {
                // ต่อท้ายเหมือนกับ Chart/Report prompt — คำสั่งของ Project เป็นบริบทเพิ่มเติม
                // ไม่ใช่แทนที่ system prompt เดิม
                template = string.Join("\n\n", template, $"Project instructions: {instructions}");
            }
        }

        return template;
    }

    private string BuildSystemPrompt(AppUser user, string mode)
    {
        // Code mode falls back to the general prompt when CodeSystemPrompt is not configured, so
        // the feature degrades to ordinary chat instead of sending an empty system prompt.
        string configured = mode == ChatModes.Code && !string.IsNullOrWhiteSpace(_options.CodeSystemPrompt)
            ? _options.CodeSystemPrompt
            : _options.SystemPrompt;

        string template = string.IsNullOrWhiteSpace(configured)
            ? "You are an internal company AI assistant. Reply in the same language the user writes in, and be concise and accurate."
            : configured;

        // Charting and report instructions apply to both modes, so they are appended rather
        // than duplicated into each mode's prompt.
        foreach (string extra in new[] { _options.ChartSystemPrompt, _options.ReportSystemPrompt })
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                template = string.Join("\n\n", template, extra);
            }
        }

        return template
            .Replace("{FullName}", user.FullName)
            .Replace("{Username}", user.Username)
            .Replace("{Department}", string.IsNullOrWhiteSpace(user.Department) ? "Unspecified" : user.Department)
            .Replace("{UserRole}", user.UserRole)
            .Replace("{Today}", DateTime.Now.ToString("yyyy-MM-dd"));
    }

    private static string BuildTitle(string content)
    {
        string oneLine = string.Join(' ', content.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return oneLine.Length <= TitleMaxLength ? oneLine : oneLine[..TitleMaxLength] + "…";
    }

    private static ChatMessageDto ToDto(ChatMessage m, List<ChatAttachment>? files = null) => new(
        m.MessageId, m.MessageRole, m.Content, m.QuestionType, m.IsBlocked,
        m.PolicyFlag, m.PolicyRuleName, m.ChatMode, m.ModelName, m.InputTokens, m.OutputTokens,
        m.CacheWriteTokens, m.CacheReadTokens, m.TotalTokens,
        m.TotalCostUsd, m.TotalCostThb, m.LatencyMs,
        files?.Select(ToAttachmentDto).ToList() ?? [],
        m.CreatedAt);

    internal static ChatAttachmentDto ToAttachmentDto(ChatAttachment a) => new(
        a.AttachmentId, a.FileName, a.ContentType, a.FileKind, a.SizeBytes,
        a.IsTextExtracted, a.PolicyScanned, a.Sha256, a.CreatedAt);
}
