using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MgtAiAuthen.Api.Services.DataSources;

public interface IDataSourceService
{
    Task<IReadOnlyList<DataSourceDto>> GetAllAsync(CancellationToken ct = default);

    Task<DataSourceDto> CreateAsync(DataSourceUpsertRequest request, string? createdBy, CancellationToken ct = default);

    Task<DataSourceDto> UpdateAsync(
        int sourceId, DataSourceUpsertRequest request, string? updatedBy, CancellationToken ct = default);

    /// <summary>Hard delete — only when nothing has ever been granted against this source (active or revoked).</summary>
    Task DeleteAsync(int sourceId, string? deletedBy, CancellationToken ct = default);

    Task<DataSourceTestResult> TestConnectionAsync(int sourceId, string? testedBy, CancellationToken ct = default);

    // ---- grants ----

    Task<IReadOnlyList<DataSourceGrantDto>> GetGrantsAsync(
        int? sourceId, int? userId, CancellationToken ct = default);

    Task<DataSourceGrantDto> GrantAsync(
        DataSourceGrantRequest request, string? grantedBy, CancellationToken ct = default);

    Task RevokeAsync(long grantId, string? revokedBy, CancellationToken ct = default);

    // ---- chat integration (Phase 2) ----

    /// <summary>Sources the given user may attach to a brand-new conversation — active grant + active source only.</summary>
    Task<IReadOnlyList<AvailableDataSourceDto>> GetAvailableForUserAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// Pulls data for one conversation's Data Source and caches the result on
    /// <see cref="ChatSessionDataFetch"/> (first call inserts, later calls overwrite it — called
    /// fresh on every message now, not just once at session start, so the data a question gets
    /// answered from is never more than one turn stale).
    /// Throws <see cref="AppException"/> if the user has no active grant for the session's source.
    /// </summary>
    /// <param name="preferredQueryOverride">
    /// Which of the source's own configured quer(y/ies) is relevant to the current question — see
    /// ChatService.ResolvePreferredQueryAsync. Ignored when the grant itself carries a scope
    /// filter (that is a security boundary and always wins); null runs every configured query.
    /// </param>
    /// <param name="question">
    /// The user's message - only used by a source in "Generated" query mode, where the AI turns it
    /// into the query to run (see GeneratedQueryPlanner).
    /// </param>
    Task<DataSourceFetchStatusDto> FetchForSessionAsync(
        Guid sessionId, int sourceId, int userId, string? username, CancellationToken ct = default,
        string? preferredQueryOverride = null, DataSourceQuestion? question = null);

    Task<DataSourceFetchStatusDto> GetFetchStatusAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Admin tool: runs one read-only query against a Supabase / Fabric Warehouse source and returns the first rows.</summary>
    Task<DataSourcePreviewResult> PreviewQueryAsync(int sourceId, string query, int? maxRows, string? username, CancellationToken ct = default);

    /// <summary>
    /// "Download full data" for an answer whose chat result was cut off: re-runs the query stored with that
    /// answer without the chat's row limit and returns it as an Excel / CSV file. Only the owner of the
    /// conversation, and only while they still hold an active grant on the source.
    /// </summary>
    Task<Reporting.ReportFile> ExportMessageDataAsync(
        long messageId, int userId, string? username, string format, CancellationToken ct = default);
}

public class DataSourceService(
    AppDbContext db,
    IEnumerable<IDataSourceConnectionTester> testers,
    IEnumerable<IDataSourceFetcher> fetchers,
    IDataProtectionProvider dataProtection,
    IQueryPlanner planner,
    IHttpClientFactory httpClientFactory,
    IAuditService audit,
    ILogger<DataSourceService> logger) : IDataSourceService
{
    /// <summary>
    /// Purpose string scopes the derived key to exactly this use — the same Data Protection key
    /// ring used elsewhere in the app cannot decrypt this column and vice versa. If the app is
    /// ever scaled to more than one machine, the default key ring (one file per machine) must be
    /// replaced with a shared store (e.g. a network path or Azure Key Vault) or a second instance
    /// cannot decrypt secrets the first one wrote — noted in README 6.22.
    /// </summary>
    private readonly IDataProtector _protector =
        dataProtection.CreateProtector("MgtAiAuthen.DataSourceSecret.v1");

    private readonly Dictionary<string, IDataSourceConnectionTester> _testers =
        testers.ToDictionary(t => t.SourceType, StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IDataSourceFetcher> _fetchers =
        fetchers.ToDictionary(f => f.SourceType, StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<DataSourceDto>> GetAllAsync(CancellationToken ct = default)
    {
        List<DataSource> sources = await db.DataSources
            .AsNoTracking()
            .OrderBy(s => s.SourceType).ThenBy(s => s.SourceName)
            .ToListAsync(ct);

        // One query for every source's active-grant count rather than N+1 per row.
        Dictionary<int, int> grantCounts = await db.DataSourceGrants
            .AsNoTracking()
            .Where(g => g.IsActive)
            .GroupBy(g => g.SourceId)
            .Select(g => new { SourceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SourceId, x => x.Count, ct);

        return sources.Select(s => ToDto(s, grantCounts.GetValueOrDefault(s.SourceId))).ToList();
    }

    public async Task<DataSourceDto> CreateAsync(
        DataSourceUpsertRequest request, string? createdBy, CancellationToken ct = default)
    {
        string sourceType = DataSourceTypes.All
            .First(t => string.Equals(t, request.SourceType, StringComparison.OrdinalIgnoreCase));
        string name = request.SourceName.Trim();

        if (await db.DataSources.AnyAsync(s => s.SourceName == name, ct))
        {
            throw AppException.Conflict($"A data source named \"{name}\" already exists");
        }

        DataSourceConfig.Validate(sourceType, request.Config);
        await ValidateCategoryAsync(request.CategoryId, request.SubCategoryId, ct);

        var entity = new DataSource
        {
            SourceName = name,
            SourceType = sourceType,
            Description = request.Description?.Trim(),
            ConfigJson = DataSourceConfig.Serialize(request.Config),
            EncryptedSecret = Protect(request.Secret),
            IsActive = request.IsActive,
            CreatedAt = DateTime.Now,
            CreatedBy = createdBy,
            CategoryId = request.CategoryId,
            SubCategoryId = request.CategoryId is null ? null : request.SubCategoryId,
        };

        db.DataSources.Add(entity);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Admin, AuditActions.DataSourceCreated, null, createdBy,
            $"Registered data source #{entity.SourceId} \"{entity.SourceName}\" ({entity.SourceType})",
            isSuccess: true, ct);

        return ToDto(entity, activeGrantCount: 0);
    }

    private async Task ValidateCategoryAsync(int? categoryId, int? subCategoryId, CancellationToken ct)
    {
        if (categoryId is null)
        {
            if (subCategoryId is not null) throw new AppException("Choose a category before a sub category.");
            return;
        }

        if (!await db.DataSourceCategories.AnyAsync(c => c.CategoryId == categoryId, ct))
        {
            throw new AppException("That category does not exist.");
        }

        if (subCategoryId is { } subId
            && !await db.DataSourceSubCategories.AnyAsync(s => s.SubCategoryId == subId && s.CategoryId == categoryId, ct))
        {
            throw new AppException("That sub category does not belong to the chosen category.");
        }
    }

    public async Task<DataSourceDto> UpdateAsync(
        int sourceId, DataSourceUpsertRequest request, string? updatedBy, CancellationToken ct = default)
    {
        DataSource entity = await db.DataSources.FirstOrDefaultAsync(s => s.SourceId == sourceId, ct)
            ?? throw AppException.NotFound("Data source not found");

        string sourceType = DataSourceTypes.All
            .First(t => string.Equals(t, request.SourceType, StringComparison.OrdinalIgnoreCase));
        string name = request.SourceName.Trim();

        if (await db.DataSources.AnyAsync(s => s.SourceName == name && s.SourceId != sourceId, ct))
        {
            throw AppException.Conflict($"A data source named \"{name}\" already exists");
        }

        DataSourceConfig.Validate(sourceType, request.Config);
        await ValidateCategoryAsync(request.CategoryId, request.SubCategoryId, ct);

        string before = $"{entity.SourceName} ({entity.SourceType}, {(entity.IsActive ? "active" : "inactive")})";

        entity.SourceName = name;
        entity.SourceType = sourceType;
        entity.Description = request.Description?.Trim();
        entity.ConfigJson = DataSourceConfig.Serialize(request.Config);
        entity.CategoryId = request.CategoryId;
        entity.SubCategoryId = request.CategoryId is null ? null : request.SubCategoryId;

        // null = leave the existing secret alone (the frontend never receives it back to resend);
        // "" = clear it; anything else = replace it. Mirrors the model-pricing Provider field fix.
        if (request.Secret is not null)
        {
            entity.EncryptedSecret = Protect(request.Secret);
        }

        entity.IsActive = request.IsActive;
        entity.UpdatedAt = DateTime.Now;
        entity.UpdatedBy = updatedBy;

        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Admin, AuditActions.DataSourceUpdated, null, updatedBy,
            $"Updated data source #{sourceId} from [{before}] to " +
            $"[{entity.SourceName} ({entity.SourceType}, {(entity.IsActive ? "active" : "inactive")})]",
            isSuccess: true, ct);

        int grantCount = await db.DataSourceGrants.CountAsync(g => g.SourceId == sourceId && g.IsActive, ct);
        return ToDto(entity, grantCount);
    }

    public async Task DeleteAsync(int sourceId, string? deletedBy, CancellationToken ct = default)
    {
        DataSource entity = await db.DataSources.FirstOrDefaultAsync(s => s.SourceId == sourceId, ct)
            ?? throw AppException.NotFound("Data source not found");

        // A grant row is never deleted (see RevokeAsync), so if one exists — active or revoked —
        // deleting the source would strand history that still names it. Deactivating is the right
        // move there instead.
        bool hasAnyGrant = await db.DataSourceGrants.AnyAsync(g => g.SourceId == sourceId, ct);
        if (hasAnyGrant)
        {
            throw new AppException(
                "This data source has grant history (current or past) and cannot be deleted. " +
                "Revoke all grants first, or set it to inactive instead of deleting it.");
        }

        bool hasAnySession = await db.ChatSessions.AnyAsync(s => s.DataSourceId == sourceId, ct);
        if (hasAnySession)
        {
            throw new AppException(
                "One or more conversations were started under this data source and cannot be " +
                "orphaned. Set it to inactive instead of deleting it.");
        }

        db.DataSources.Remove(entity);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Admin, AuditActions.DataSourceUpdated, null, deletedBy,
            $"Deleted data source #{sourceId} \"{entity.SourceName}\" (no grants had ever been made)",
            isSuccess: true, ct);
    }

    public async Task<DataSourceTestResult> TestConnectionAsync(
        int sourceId, string? testedBy, CancellationToken ct = default)
    {
        DataSource entity = await db.DataSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SourceId == sourceId, ct)
            ?? throw AppException.NotFound("Data source not found");

        if (!_testers.TryGetValue(entity.SourceType, out IDataSourceConnectionTester? tester))
        {
            // Reachable only if a row exists for a type this build has no tester registered for.
            throw new AppException(
                $"No connection tester is registered for source type \"{entity.SourceType}\".");
        }

        Dictionary<string, string> config = DataSourceConfig.Parse(entity.ConfigJson);
        string? secret = Unprotect(entity.EncryptedSecret);

        // RagConnectionTester has nothing of its own in config (a Rag source's real "config" is
        // its imported RagChunks rows, not a URL/tenant/etc.) but still needs to know which
        // source it's being asked about — smuggled in under a key no real config ever uses rather
        // than widening IDataSourceConnectionTester's signature for every other type's benefit.
        config["__sourceId"] = sourceId.ToString();

        DataSourceTestResult result = await tester.TestAsync(config, secret, ct);

        await audit.LogAsync(AuditCategories.Admin, AuditActions.DataSourceTested, null, testedBy,
            $"Tested data source #{sourceId} \"{entity.SourceName}\": " +
            $"{(result.Success ? "OK" : "FAILED")} — {result.Message}",
            isSuccess: result.Success, ct);

        return result;
    }

    // ---------------------------------------------------------------- grants

    public async Task<IReadOnlyList<DataSourceGrantDto>> GetGrantsAsync(
        int? sourceId, int? userId, CancellationToken ct = default)
    {
        IQueryable<DataSourceGrant> q = db.DataSourceGrants.AsNoTracking();
        if (sourceId is { } s) q = q.Where(g => g.SourceId == s);
        if (userId is { } u) q = q.Where(g => g.UserId == u);

        var rows = await q
            .OrderByDescending(g => g.GrantedAt)
            .Join(db.DataSources.AsNoTracking(), g => g.SourceId, src => src.SourceId,
                (g, src) => new { Grant = g, src.SourceName, src.SourceType })
            .Join(db.Users.AsNoTracking(), x => x.Grant.UserId, u => u.UserId,
                (x, u) => new { x.Grant, x.SourceName, x.SourceType, u.Username, u.FullName, u.Department })
            .ToListAsync(ct);

        return rows.Select(r => new DataSourceGrantDto(
            r.Grant.GrantId, r.Grant.SourceId, r.SourceName, r.SourceType,
            r.Grant.UserId, r.Username, r.FullName, r.Department,
            r.Grant.ScopeType, r.Grant.ScopeFilter, r.Grant.Notes,
            r.Grant.GrantedAt, r.Grant.GrantedBy,
            r.Grant.IsActive, r.Grant.RevokedAt, r.Grant.RevokedBy)).ToList();
    }

    public async Task<DataSourceGrantDto> GrantAsync(
        DataSourceGrantRequest request, string? grantedBy, CancellationToken ct = default)
    {
        DataSource source = await db.DataSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SourceId == request.SourceId, ct)
            ?? throw AppException.NotFound("Data source not found");

        AppUser user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == request.UserId, ct)
            ?? throw AppException.NotFound("User not found");

        string scopeType = DataSourceScopeTypes.All
            .First(t => string.Equals(t, request.ScopeType, StringComparison.OrdinalIgnoreCase));

        if (scopeType == DataSourceScopeTypes.Custom && string.IsNullOrWhiteSpace(request.ScopeFilter))
        {
            throw new AppException("Scope type Custom requires a scope filter describing the restriction");
        }

        if (scopeType == DataSourceScopeTypes.OwnDepartment && string.IsNullOrWhiteSpace(user.Department))
        {
            // A grant that silently means "no restriction, because there is nothing to restrict
            // to" is worse than refusing it — the admin's intent (limit to their department) would
            // quietly become "Full" the moment the employee has no department set.
            throw new AppException(
                $"{user.FullName} has no Department set, so \"own department\" cannot be applied. " +
                "Set their department first, or grant Full/Custom scope instead.");
        }

        // ปิด grant เดิมที่ยัง active อยู่ก่อน — คนหนึ่งมีสิทธิ์ "เปิดอยู่" ต่อแหล่งข้อมูลหนึ่งได้แถวเดียว
        // เพื่อไม่ให้ตีความยากว่าใช้ scope ไหนเป็นตัวจริงเมื่อมีมากกว่าหนึ่งแถว
        DataSourceGrant? existing = await db.DataSourceGrants.FirstOrDefaultAsync(
            g => g.SourceId == request.SourceId && g.UserId == request.UserId && g.IsActive, ct);

        if (existing is not null)
        {
            existing.IsActive = false;
            existing.RevokedAt = DateTime.Now;
            existing.RevokedBy = grantedBy;
        }

        var grant = new DataSourceGrant
        {
            SourceId = request.SourceId,
            UserId = request.UserId,
            ScopeType = scopeType,
            ScopeFilter = scopeType == DataSourceScopeTypes.Custom ? request.ScopeFilter?.Trim() : null,
            Notes = request.Notes?.Trim(),
            GrantedAt = DateTime.Now,
            GrantedBy = grantedBy,
            IsActive = true,
        };

        db.DataSourceGrants.Add(grant);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Admin, AuditActions.DataSourceGranted, null, grantedBy,
            $"Granted {user.Username} {scopeType} access to \"{source.SourceName}\"" +
            (grant.ScopeFilter is null ? "" : $" (filter: {grant.ScopeFilter})") +
            (existing is null ? "" : " — replaced a previous grant"),
            isSuccess: true, ct);

        return new DataSourceGrantDto(
            grant.GrantId, source.SourceId, source.SourceName, source.SourceType,
            user.UserId, user.Username, user.FullName, user.Department,
            grant.ScopeType, grant.ScopeFilter, grant.Notes,
            grant.GrantedAt, grant.GrantedBy, grant.IsActive, grant.RevokedAt, grant.RevokedBy);
    }

    public async Task RevokeAsync(long grantId, string? revokedBy, CancellationToken ct = default)
    {
        DataSourceGrant grant = await db.DataSourceGrants.FirstOrDefaultAsync(g => g.GrantId == grantId, ct)
            ?? throw AppException.NotFound("Grant not found");

        if (!grant.IsActive)
        {
            throw new AppException("This grant is already revoked.");
        }

        grant.IsActive = false;
        grant.RevokedAt = DateTime.Now;
        grant.RevokedBy = revokedBy;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Admin, AuditActions.DataSourceRevoked, null, revokedBy,
            $"Revoked grant #{grantId} (source #{grant.SourceId}, user #{grant.UserId})",
            isSuccess: true, ct);
    }

    // ---------------------------------------------------------------- chat integration (Phase 2)

    public async Task<IReadOnlyList<AvailableDataSourceDto>> GetAvailableForUserAsync(
        int userId, CancellationToken ct = default)
    {
        var rows = await db.DataSourceGrants.AsNoTracking()
            .Where(g => g.UserId == userId && g.IsActive)
            .Join(db.DataSources.AsNoTracking().Where(s => s.IsActive), g => g.SourceId, s => s.SourceId,
                (g, s) => new { g.ScopeType, g.ScopeFilter, s.SourceId, s.SourceName, s.SourceType, s.Description })
            .OrderBy(x => x.SourceName)
            .ToListAsync(ct);

        return rows.Select(r => new AvailableDataSourceDto(
            r.SourceId, r.SourceName, r.SourceType, r.Description, r.ScopeType, r.ScopeFilter)).ToList();
    }

    public async Task<DataSourceFetchStatusDto> FetchForSessionAsync(
        Guid sessionId, int sourceId, int userId, string? username, CancellationToken ct = default,
        string? preferredQueryOverride = null, DataSourceQuestion? question = null)
    {
        DataSourceGrant grant = await db.DataSourceGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.SourceId == sourceId && g.UserId == userId && g.IsActive, ct)
            ?? throw AppException.Forbidden("You do not have access to this data source.");

        DataSource source = await db.DataSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SourceId == sourceId, ct)
            ?? throw AppException.NotFound("Data source not found");

        if (!source.IsActive)
        {
            throw new AppException("This data source has been deactivated by an administrator.");
        }

        if (!_fetchers.TryGetValue(source.SourceType, out IDataSourceFetcher? fetcher))
        {
            throw new AppException($"No fetcher is registered for source type \"{source.SourceType}\".");
        }

        Dictionary<string, string> config = DataSourceConfig.Parse(source.ConfigJson);
        string? secret = Unprotect(source.EncryptedSecret);

        // A grant's own scope filter is a security boundary (restricts what this specific user may
        // see) and always wins when set. preferredQueryOverride is purely an efficiency choice (which
        // of the admin's own configured queries is relevant to the current question) and only ever
        // applies for a "Full" grant that has no such restriction to begin with.
        string? effectiveOverride = !string.IsNullOrWhiteSpace(grant.ScopeFilter)
            ? grant.ScopeFilter
            : preferredQueryOverride;

        // "Generated" mode: the AI writes the query for THIS question. A grant's own scope filter still
        // wins (it is a security boundary), so a restricted user runs the admin's query instead.
        DataSourceFetchResult result = planner.IsGenerated(source.SourceType, config)
                                       && string.IsNullOrWhiteSpace(grant.ScopeFilter)
            ? await FetchGeneratedAsync(source, config, secret, fetcher, question, ct)
            : await fetcher.FetchAsync(config, secret, effectiveOverride, ct);

        ChatSessionDataFetch? existing = await db.ChatSessionDataFetches
            .FirstOrDefaultAsync(f => f.SessionId == sessionId, ct);

        DateTime fetchedAt = DateTime.Now;

        if (existing is null)
        {
            db.ChatSessionDataFetches.Add(new ChatSessionDataFetch
            {
                SessionId = sessionId,
                SourceId = sourceId,
                FetchedAt = fetchedAt,
                FetchedByUserId = userId,
                Success = result.Success,
                Message = result.Message,
                ContentText = result.ContentText,
                CharCount = result.CharCount,
                Truncated = result.Truncated,
            });
        }
        else
        {
            existing.SourceId = sourceId;
            existing.FetchedAt = fetchedAt;
            existing.FetchedByUserId = userId;
            existing.Success = result.Success;
            existing.Message = result.Message;
            existing.ContentText = result.ContentText;
            existing.CharCount = result.CharCount;
            existing.Truncated = result.Truncated;
        }

        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Chat,
            result.Success ? AuditActions.DataSourceFetched : AuditActions.DataSourceFetchFailed,
            userId, username,
            $"{(existing is null ? "Fetched" : "Refreshed")} data from \"{source.SourceName}\" for session " +
            $"{sessionId}: {result.Message}", isSuccess: result.Success, ct);

        return new DataSourceFetchStatusDto(
            sourceId, source.SourceName, result.Success, result.Message, fetchedAt, result.CharCount, result.Truncated,
            result.Query);
    }

    private async Task<DataSourceFetchResult> FetchGeneratedAsync(
        DataSource source, Dictionary<string, string> config, string? secret, IDataSourceFetcher fetcher,
        DataSourceQuestion? question, CancellationToken ct)
    {
        if (question is null || string.IsNullOrWhiteSpace(question.Text))
        {
            // The refresh button has no question to write a query for.
            return new DataSourceFetchResult(true,
                "A query is written for each question - nothing to refresh until you ask one.", "", 0, false);
        }

        QueryPlan plan = await planner.PlanAsync(source, config, secret, question, ct);

        if (plan.NoDataNeeded)
        {
            return new DataSourceFetchResult(true, plan.Message, "", 0, false);
        }

        if (!plan.Success)
        {
            return new DataSourceFetchResult(false, plan.Message, null, 0, false, plan.Draft);
        }

        DataSourceFetchResult result = await fetcher.FetchAsync(config, secret, plan.Query, ct);

        if (!result.Success)
        {
            // AI-written SQL fails now and then (a wrong column name, say). One repair attempt with the
            // database's own error message fixes most of them for the price of one small extra call.
            QueryPlan repaired = await planner.RepairAsync(source, config, secret, question, plan.Query!, result.Message, ct);
            if (repaired.Success && repaired.Query is not null)
            {
                plan = repaired;
                result = await fetcher.FetchAsync(config, secret, plan.Query, ct);
            }
        }

        if (!result.Success)
        {
            return result with { Message = $"{result.Message} (query: {Shorten(plan.Query!)})", Query = plan.Query };
        }

        // Tell the model which query produced these numbers - it answers more accurately with that
        // stated, and the admin can read it in the fetch message.
        string content = $"Query used:\n{plan.Query}\n\nResult:\n{result.ContentText}";
        return result with
        {
            Message = $"{result.Message} Query: {Shorten(plan.Query!)}",
            ContentText = content,
            CharCount = content.Length,
            Query = plan.Query,
        };
    }

    private static string Shorten(string query)
    {
        string single = string.Join(' ', query.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return single.Length > 300 ? single[..300] + "..." : single;
    }

    public async Task<Reporting.ReportFile> ExportMessageDataAsync(
        long messageId, int userId, string? username, string format, CancellationToken ct = default)
    {
        if (!string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("Choose xlsx or csv.");
        }

        ChatMessage message = await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.MessageId == messageId, ct)
            ?? throw AppException.NotFound("Message not found");

        ChatSession session = await db.ChatSessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == message.SessionId, ct)
            ?? throw AppException.NotFound("Conversation not found");

        if (session.UserId != userId)
        {
            throw AppException.Forbidden("Only the person who asked can download the data behind this answer.");
        }

        if (string.IsNullOrWhiteSpace(message.DataExportQuery) || session.DataSourceId is not { } sourceId)
        {
            throw AppException.NotFound("This answer has no extra data to download.");
        }

        DataSourceGrant grant = await db.DataSourceGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.SourceId == sourceId && g.UserId == userId && g.IsActive, ct)
            ?? throw AppException.Forbidden("You no longer have access to this data source.");

        if (!string.IsNullOrWhiteSpace(grant.ScopeFilter))
        {
            throw AppException.Forbidden("Your access to this data source is restricted - full downloads are not available.");
        }

        DataSource source = await db.DataSources.AsNoTracking().FirstOrDefaultAsync(s => s.SourceId == sourceId, ct)
            ?? throw AppException.NotFound("Data source not found");

        if (!source.IsActive) throw new AppException("This data source has been deactivated by an administrator.");

        Dictionary<string, string> config = DataSourceConfig.Parse(source.ConfigJson);
        string? secret = Unprotect(source.EncryptedSecret);
        bool rest = string.Equals(source.SourceType, DataSourceTypes.Supabase, StringComparison.OrdinalIgnoreCase)
                    && SupabaseMode.Resolve(config) == SupabaseMode.Rest;

        // The stored query was checked when it was written; check it again before it runs a second time.
        HashSet<string> allowed = QuerySchema.ParseAllowed(config.GetValueOrDefault("allowedTables", ""));
        (string? query, string? problem) = rest
            ? QueryValidator.ValidateRest(message.DataExportQuery, allowed, new HashSet<string>(), DataSourceFetchLimits.ExportMaxRows)
            : QueryValidator.ValidateSql(message.DataExportQuery, allowed);

        if (query is null) throw new AppException($"This query can no longer be run: {problem}");

        // The chat limits this query to a few hundred rows; the download should not.
        if (!rest) query = StripRowLimit(query);

        var exportConfig = new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase)
        {
            ["queryMode"] = "Fixed",
            [DataSourceFetchLimits.ExportMarker] = "1",
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(5, ApiConnectionTester.ResolveTimeout(config).TotalMinutes)));

        FabricWarehouseQueryResult result;
        try
        {
            if (string.Equals(source.SourceType, DataSourceTypes.Supabase, StringComparison.OrdinalIgnoreCase))
            {
                result = await SupabaseQuery.ExecuteAsync(
                    httpClientFactory.CreateClient(ApiConnectionTester.HttpClientName), exportConfig, secret, query, timeoutCts.Token);
            }
            else if (string.Equals(source.SourceType, DataSourceTypes.DataLake, StringComparison.OrdinalIgnoreCase)
                     && DataLakeMode.Resolve(config) == DataLakeMode.Warehouse)
            {
                if (string.IsNullOrWhiteSpace(secret)) throw new AppException("No client secret is configured for this data source.");

                result = await FabricWarehouseQuery.ExecuteAsync(
                    httpClientFactory.CreateClient(PowerBiConnectionTester.HttpClientName),
                    config.GetValueOrDefault("tenantId", "").Trim(), config.GetValueOrDefault("clientId", "").Trim(),
                    secret, exportConfig, query, timeoutCts.Token);
            }
            else
            {
                throw new AppException("This type of data source does not support full downloads.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AppException("The query took too long to run for a full download. Narrow the question and try again.");
        }

        if (!result.Success || result.Tables is null)
        {
            throw new AppException($"The download could not be prepared: {result.Message}");
        }

        (byte[] content, string contentType, string extension) = DataExportWriter.Build(
            result.Tables, query, source.SourceName, format, result.Truncated, DataSourceFetchLimits.ExportMaxRows);

        int rows = result.Tables.Sum(t => t.Rows.Count);

        await audit.LogAsync(AuditCategories.Chat, AuditActions.DataSourceExported, userId, username,
            $"Downloaded {rows:N0} row(s) from \"{source.SourceName}\" as {extension} (answer {messageId})",
            isSuccess: true, ct);

        string safeName = new string(source.SourceName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        if (safeName.Length == 0) safeName = "data";

        return new Reporting.ReportFile($"{safeName}-{DateTime.Now:yyyyMMdd-HHmm}.{extension}", contentType, content);
    }

    /// <summary>Removes a trailing LIMIT n or a leading TOP n from a single SELECT so the download is not capped.</summary>
    private static string StripRowLimit(string sql)
    {
        sql = System.Text.RegularExpressions.Regex.Replace(
            sql, @"\s+LIMIT\s+\d+\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(
            sql, @"(?<=\bSELECT\s)(\s*DISTINCT\s+)?TOP\s*\(?\s*\d+\s*\)?\s+", "$1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public async Task<DataSourcePreviewResult> PreviewQueryAsync(
        int sourceId, string query, int? maxRows, string? username, CancellationToken ct = default)
    {
        DataSource source = await db.DataSources.AsNoTracking().FirstOrDefaultAsync(s => s.SourceId == sourceId, ct)
            ?? throw AppException.NotFound("Data source not found");

        Dictionary<string, string> config = DataSourceConfig.Parse(source.ConfigJson);

        bool supported = string.Equals(source.SourceType, DataSourceTypes.Supabase, StringComparison.OrdinalIgnoreCase)
                         && SupabaseMode.Resolve(config) == SupabaseMode.Sql
                         || string.Equals(source.SourceType, DataSourceTypes.DataLake, StringComparison.OrdinalIgnoreCase)
                            && DataLakeMode.Resolve(config) == DataLakeMode.Warehouse;

        if (!supported)
        {
            throw new AppException("Query preview is available for Supabase (SQL mode) and Fabric Warehouse sources.");
        }

        string? problem = QueryValidator.ValidateAdminSql(query);
        if (problem is not null) throw new AppException($"The query was not run: {problem}");

        if (!_fetchers.TryGetValue(source.SourceType, out IDataSourceFetcher? fetcher))
        {
            throw new AppException($"No fetcher is registered for source type \"{source.SourceType}\".");
        }

        int rows = Math.Clamp(maxRows ?? 30, 1, 200);
        var previewConfig = new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase)
        {
            ["queryMode"] = "Generated",
            ["generatedMaxRows"] = rows.ToString(),
            ["generatedMaxChars"] = "60000",
        };

        DataSourceFetchResult result = await fetcher.FetchAsync(previewConfig, Unprotect(source.EncryptedSecret), query.Trim().TrimEnd(';'), ct);

        await audit.LogAsync(AuditCategories.Admin, AuditActions.DataSourceTested, null, username,
            $"Previewed a query on \"{source.SourceName}\" (#{sourceId}): {result.Message}", result.Success, ct);

        return new DataSourcePreviewResult(
            result.Success, result.Message, result.ContentText, result.Success ? CountRows(result.ContentText) : null, result.Truncated);
    }

    private static int CountRows(string? markdown)
        => string.IsNullOrEmpty(markdown) ? 0 : Math.Max(0, markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.StartsWith("| ")) - 2);

    public async Task<DataSourceFetchStatusDto> GetFetchStatusAsync(Guid sessionId, CancellationToken ct = default)
    {
        var row = await db.ChatSessionDataFetches.AsNoTracking()
            .Where(f => f.SessionId == sessionId)
            .Join(db.DataSources.AsNoTracking(), f => f.SourceId, s => s.SourceId,
                (f, s) => new { f.SourceId, s.SourceName, f.Success, f.Message, f.FetchedAt, f.CharCount, f.Truncated })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? new DataSourceFetchStatusDto(null, null, null, null, null, null, null)
            : new DataSourceFetchStatusDto(
                row.SourceId, row.SourceName, row.Success, row.Message, row.FetchedAt, row.CharCount, row.Truncated);
    }

    // ---------------------------------------------------------------- helpers

    private string? Protect(string? plainSecret)
    {
        if (string.IsNullOrEmpty(plainSecret)) return null;

        try
        {
            return _protector.Protect(plainSecret);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encrypt a data source secret");
            throw new AppException("Could not encrypt the secret. Please try again.");
        }
    }

    private string? Unprotect(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;

        try
        {
            return _protector.Unprotect(encrypted);
        }
        catch (Exception ex)
        {
            // The key ring rotated or the row was written by a different machine's key store.
            // Failing the connection test with a clear reason beats throwing a raw crypto
            // exception up to the controller.
            logger.LogError(ex, "Failed to decrypt a data source secret");
            return null;
        }
    }

    private static DataSourceDto ToDto(DataSource s, int activeGrantCount) => new(
        s.SourceId, s.SourceName, s.SourceType, s.Description,
        DataSourceConfig.Parse(s.ConfigJson),
        HasSecret: !string.IsNullOrEmpty(s.EncryptedSecret),
        s.IsActive, activeGrantCount,
        s.CreatedAt, s.CreatedBy, s.UpdatedAt, s.UpdatedBy,
        s.CategoryId, s.SubCategoryId);
}
