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
    /// <see cref="ChatSessionDataFetch"/> (first call inserts, later calls — "Refresh" — overwrite it).
    /// Throws <see cref="AppException"/> if the user has no active grant for the session's source.
    /// </summary>
    Task<DataSourceFetchStatusDto> FetchForSessionAsync(
        Guid sessionId, int sourceId, int userId, string? username, CancellationToken ct = default);

    Task<DataSourceFetchStatusDto> GetFetchStatusAsync(Guid sessionId, CancellationToken ct = default);
}

public class DataSourceService(
    AppDbContext db,
    IEnumerable<IDataSourceConnectionTester> testers,
    IEnumerable<IDataSourceFetcher> fetchers,
    IDataProtectionProvider dataProtection,
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
        };

        db.DataSources.Add(entity);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Admin, AuditActions.DataSourceCreated, null, createdBy,
            $"Registered data source #{entity.SourceId} \"{entity.SourceName}\" ({entity.SourceType})",
            isSuccess: true, ct);

        return ToDto(entity, activeGrantCount: 0);
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

        string before = $"{entity.SourceName} ({entity.SourceType}, {(entity.IsActive ? "active" : "inactive")})";

        entity.SourceName = name;
        entity.SourceType = sourceType;
        entity.Description = request.Description?.Trim();
        entity.ConfigJson = DataSourceConfig.Serialize(request.Config);

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
        Guid sessionId, int sourceId, int userId, string? username, CancellationToken ct = default)
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

        DataSourceFetchResult result = await fetcher.FetchAsync(config, secret, grant.ScopeFilter, ct);

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
            sourceId, source.SourceName, result.Success, result.Message, fetchedAt, result.CharCount, result.Truncated);
    }

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
        s.CreatedAt, s.CreatedBy, s.UpdatedAt, s.UpdatedBy);
}
