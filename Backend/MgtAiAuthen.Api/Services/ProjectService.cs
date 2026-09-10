using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MgtAiAuthen.Api.Services;

public interface IProjectService
{
    /// <summary>Every project the caller owns, plus every project someone else shared.</summary>
    Task<IReadOnlyList<ProjectDto>> GetAllAsync(int userId, bool isAdmin, CancellationToken ct = default);

    Task<ProjectDto> CreateAsync(int userId, ProjectUpsertRequest request, CancellationToken ct = default);

    /// <summary>Owner or Admin only.</summary>
    Task<ProjectDto> UpdateAsync(
        int projectId, int userId, bool isAdmin, ProjectUpsertRequest request, CancellationToken ct = default);

    /// <summary>Soft delete (owner or Admin only) — conversations already under it keep working.</summary>
    Task DeleteAsync(int projectId, int userId, bool isAdmin, CancellationToken ct = default);

    Task<IReadOnlyList<ProjectFileDto>> GetFilesAsync(
        int projectId, int userId, bool isAdmin, CancellationToken ct = default);

    /// <summary>Owner or Admin only — screens content against PolicyRules before accepting the file.</summary>
    Task<ProjectFileDto> UploadFileAsync(
        int projectId, int userId, bool isAdmin, IFormFile file, CancellationToken ct = default);

    Task<(ProjectFile Meta, byte[] Content)> DownloadFileAsync(
        long projectFileId, int userId, bool isAdmin, CancellationToken ct = default);

    Task DeleteFileAsync(long projectFileId, int userId, bool isAdmin, CancellationToken ct = default);
}

public class ProjectService(
    AppDbContext db,
    IAttachmentService attachments,
    IPolicyService policy,
    IAuditService audit,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ProjectService> logger) : IProjectService
{
    public async Task<IReadOnlyList<ProjectDto>> GetAllAsync(
        int userId, bool isAdmin, CancellationToken ct = default)
    {
        // Admin sees everything for oversight — the same rule Admin/Auditor already have over
        // chat logs; everyone else sees their own plus whatever has been shared.
        List<Project> projects = await db.Projects
            .AsNoTracking()
            .Where(p => !p.IsDeleted && (isAdmin || p.OwnerUserId == userId || p.IsShared))
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        if (projects.Count == 0) return [];

        int[] ownerIds = projects.Select(p => p.OwnerUserId).Distinct().ToArray();
        Dictionary<int, AppUser> owners = await db.Users.AsNoTracking()
            .Where(u => ownerIds.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, ct);

        int[] projectIds = projects.Select(p => p.ProjectId).ToArray();

        Dictionary<int, int> fileCounts = await db.ProjectFiles.AsNoTracking()
            .Where(f => projectIds.Contains(f.ProjectId))
            .GroupBy(f => f.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, ct);

        Dictionary<int, int> sessionCounts = await db.ChatSessions.AsNoTracking()
            .Where(s => s.ProjectId != null && projectIds.Contains(s.ProjectId.Value) && !s.IsDeleted)
            .GroupBy(s => s.ProjectId!.Value)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, ct);

        return projects.Select(p => ToDto(p, owners[p.OwnerUserId], userId, isAdmin,
            fileCounts.GetValueOrDefault(p.ProjectId), sessionCounts.GetValueOrDefault(p.ProjectId))).ToList();
    }

    public async Task<ProjectDto> CreateAsync(
        int userId, ProjectUpsertRequest request, CancellationToken ct = default)
    {
        AppUser owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, ct)
            ?? throw AppException.NotFound("User not found");

        var entity = new Project
        {
            Name = request.Name.Trim(),
            Instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions.Trim(),
            OwnerUserId = userId,
            IsShared = request.IsShared,
            CreatedAt = DateTime.Now,
        };

        db.Projects.Add(entity);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Chat, AuditActions.ProjectCreated, userId, owner.Username,
            $"Created project #{entity.ProjectId} \"{entity.Name}\"" + (entity.IsShared ? " (shared)" : ""),
            isSuccess: true, ct);

        return ToDto(entity, owner, userId, isAdmin: false, fileCount: 0, sessionCount: 0);
    }

    public async Task<ProjectDto> UpdateAsync(
        int projectId, int userId, bool isAdmin, ProjectUpsertRequest request, CancellationToken ct = default)
    {
        Project entity = await db.Projects.FirstOrDefaultAsync(p => p.ProjectId == projectId && !p.IsDeleted, ct)
            ?? throw AppException.NotFound("Project not found");

        RequireEditor(entity, userId, isAdmin);

        AppUser owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == entity.OwnerUserId, ct)
            ?? throw AppException.NotFound("Project owner no longer exists");

        string before = $"{entity.Name} ({(entity.IsShared ? "shared" : "private")})";

        entity.Name = request.Name.Trim();
        entity.Instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions.Trim();
        entity.IsShared = request.IsShared;
        entity.UpdatedAt = DateTime.Now;

        await db.SaveChangesAsync(ct);

        string actingUsername = httpContextAccessor.HttpContext?.User.Identity?.Name ?? owner.Username;
        await audit.LogAsync(AuditCategories.Chat, AuditActions.ProjectUpdated, userId, actingUsername,
            $"Updated project #{projectId} from [{before}] to [{entity.Name} ({(entity.IsShared ? "shared" : "private")})]",
            isSuccess: true, ct);

        int fileCount = await db.ProjectFiles.CountAsync(f => f.ProjectId == projectId, ct);
        int sessionCount = await db.ChatSessions.CountAsync(s => s.ProjectId == projectId && !s.IsDeleted, ct);

        return ToDto(entity, owner, userId, isAdmin, fileCount, sessionCount);
    }

    public async Task DeleteAsync(int projectId, int userId, bool isAdmin, CancellationToken ct = default)
    {
        Project entity = await db.Projects.FirstOrDefaultAsync(p => p.ProjectId == projectId && !p.IsDeleted, ct)
            ?? throw AppException.NotFound("Project not found");

        RequireEditor(entity, userId, isAdmin);

        entity.IsDeleted = true;
        entity.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);

        string? actingUsername = httpContextAccessor.HttpContext?.User.Identity?.Name;
        await audit.LogAsync(AuditCategories.Chat, AuditActions.ProjectDeleted, userId, actingUsername,
            $"Deleted project #{projectId} \"{entity.Name}\" — its conversations remain readable for auditing",
            isSuccess: true, ct);
    }

    public async Task<IReadOnlyList<ProjectFileDto>> GetFilesAsync(
        int projectId, int userId, bool isAdmin, CancellationToken ct = default)
    {
        Project project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProjectId == projectId && !p.IsDeleted, ct)
            ?? throw AppException.NotFound("Project not found");

        RequireViewer(project, userId, isAdmin);

        var rows = await db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == projectId)
            .OrderBy(f => f.CreatedAt)
            .Join(db.Users.AsNoTracking(), f => f.UserId, u => u.UserId, (f, u) => new { File = f, u.Username })
            .ToListAsync(ct);

        return rows.Select(r => new ProjectFileDto(
            r.File.ProjectFileId, r.File.FileName, r.File.ContentType, r.File.FileKind, r.File.SizeBytes,
            r.File.IsTextExtracted, r.File.PolicyScanned, r.File.Sha256, r.Username, r.File.CreatedAt)).ToList();
    }

    public async Task<ProjectFileDto> UploadFileAsync(
        int projectId, int userId, bool isAdmin, IFormFile file, CancellationToken ct = default)
    {
        Project project = await db.Projects.FirstOrDefaultAsync(p => p.ProjectId == projectId && !p.IsDeleted, ct)
            ?? throw AppException.NotFound("Project not found");

        RequireEditor(project, userId, isAdmin);

        AppUser user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, ct)
            ?? throw AppException.NotFound("User not found");

        List<StoredFile> stored;
        try
        {
            stored = await attachments.StoreAsync([file], ct);
        }
        catch (AppException ex)
        {
            await audit.LogAsync(AuditCategories.Chat, AuditActions.ProjectFileRejected, userId, user.Username,
                $"Rejected file for project #{projectId} \"{project.Name}\": {ex.Message} ({file.FileName})",
                isSuccess: false, ct);
            throw;
        }

        StoredFile stored1 = stored[0];

        // Proactive gate: a project file is resent into every future conversation under this
        // project, so its content is screened before it is ever accepted — unlike a chat message
        // attachment, which is screened but still stored even when blocked (there the point is an
        // audit trail of what someone tried to send; here nothing should be added at all).
        bool carriesText = FileKinds.CarriesText(stored1.FileKind);
        string policyInput = carriesText
            ? $"{stored1.FileName}\n{stored1.ExtractedText}"
            : stored1.FileName;

        Services.PolicyDecision decision = await policy.EvaluateAsync(policyInput, ct);

        if (decision.IsBlocked)
        {
            attachments.TryDelete(stored1.RelativePath);

            logger.LogInformation(
                "Project file {FileName} for project {ProjectId} blocked by rule {RuleName}",
                stored1.FileName, projectId, decision.RuleName);

            await audit.LogAsync(AuditCategories.Chat, AuditActions.ProjectFileRejected, userId, user.Username,
                $"Blocked file for project #{projectId} \"{project.Name}\" by rule \"{decision.RuleName}\" " +
                $"(severity {decision.Severity}): {stored1.FileName}",
                isSuccess: false, ct);

            throw new AppException(
                decision.Notice ?? "This file was blocked by company data policy and was not added to the project.");
        }

        var entity = new ProjectFile
        {
            ProjectId = projectId,
            FileName = stored1.FileName,
            ContentType = stored1.ContentType,
            FileKind = stored1.FileKind,
            SizeBytes = stored1.SizeBytes,
            Sha256 = stored1.Sha256,
            StoredPath = stored1.RelativePath,
            IsTextExtracted = stored1.ExtractedText is not null,
            ExtractedChars = stored1.ExtractedText?.Length,
            PolicyScanned = carriesText,
            UserId = userId,
            CreatedAt = DateTime.Now,
        };

        db.ProjectFiles.Add(entity);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Chat, AuditActions.ProjectFileUploaded, userId, user.Username,
            $"Added {stored1.FileName} ({stored1.FileKind}, {stored1.SizeBytes / 1024.0:F1} KB) to " +
            $"project #{projectId} \"{project.Name}\"" +
            $" ({(carriesText ? "content scanned" : "file name only scanned")})",
            isSuccess: true, ct);

        return new ProjectFileDto(
            entity.ProjectFileId, entity.FileName, entity.ContentType, entity.FileKind, entity.SizeBytes,
            entity.IsTextExtracted, entity.PolicyScanned, entity.Sha256, user.Username, entity.CreatedAt);
    }

    public async Task<(ProjectFile Meta, byte[] Content)> DownloadFileAsync(
        long projectFileId, int userId, bool isAdmin, CancellationToken ct = default)
    {
        ProjectFile file = await db.ProjectFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.ProjectFileId == projectFileId, ct)
            ?? throw AppException.NotFound("File not found");

        Project project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProjectId == file.ProjectId, ct)
            ?? throw AppException.NotFound("Project not found");

        // Anyone who may use the project (owner, shared, or Admin) may see what informs its
        // answers — transparency about project context matters as much as transparency about a
        // message's own attachments.
        RequireViewer(project, userId, isAdmin);

        byte[] content = await attachments.ReadAsync(file.StoredPath, ct);

        string? actingUsername = httpContextAccessor.HttpContext?.User.Identity?.Name;
        await audit.LogAsync(AuditCategories.Chat, AuditActions.ProjectFileDownloaded, userId, actingUsername,
            $"Downloaded {file.FileName} from project #{file.ProjectId} \"{project.Name}\"",
            isSuccess: true, ct);

        return (file, content);
    }

    public async Task DeleteFileAsync(long projectFileId, int userId, bool isAdmin, CancellationToken ct = default)
    {
        ProjectFile file = await db.ProjectFiles.FirstOrDefaultAsync(f => f.ProjectFileId == projectFileId, ct)
            ?? throw AppException.NotFound("File not found");

        Project project = await db.Projects.FirstOrDefaultAsync(p => p.ProjectId == file.ProjectId, ct)
            ?? throw AppException.NotFound("Project not found");

        RequireEditor(project, userId, isAdmin);

        db.ProjectFiles.Remove(file);
        await db.SaveChangesAsync(ct);
        attachments.TryDelete(file.StoredPath);

        string? actingUsername = httpContextAccessor.HttpContext?.User.Identity?.Name;
        await audit.LogAsync(AuditCategories.Chat, AuditActions.ProjectFileDeleted, userId, actingUsername,
            $"Removed {file.FileName} from project #{file.ProjectId} \"{project.Name}\"",
            isSuccess: true, ct);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Owner or Admin — may edit instructions, manage files, or delete the project.</summary>
    private static void RequireEditor(Project project, int userId, bool isAdmin)
    {
        if (project.OwnerUserId != userId && !isAdmin)
        {
            throw AppException.Forbidden("Only the project's owner or an administrator can change it");
        }
    }

    /// <summary>Owner, Admin, or anyone the project is shared with — may use it and see its files.</summary>
    private static void RequireViewer(Project project, int userId, bool isAdmin)
    {
        if (project.OwnerUserId != userId && !isAdmin && !project.IsShared)
        {
            throw AppException.Forbidden("This project has not been shared with you");
        }
    }

    private static ProjectDto ToDto(
        Project p, AppUser owner, int callerId, bool isAdmin, int fileCount, int sessionCount) => new(
        p.ProjectId, p.Name, p.Instructions, p.OwnerUserId, owner.Username, owner.FullName, p.IsShared,
        CanEdit: p.OwnerUserId == callerId || isAdmin,
        fileCount, sessionCount, p.CreatedAt, p.UpdatedAt);
}
