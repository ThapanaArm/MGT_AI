using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MgtAiAuthen.Api.Services;

/// <summary>
/// A personal library of reusable prompt snippets. Deliberately the simplest possible shape:
/// a name and a body of text. There is no execution, no files, and no effect on the model by
/// itself — the frontend inserts Body into the compose box and the employee still reviews and
/// sends the message, exactly like pasting from their own notes.
/// </summary>
public interface ISkillService
{
    Task<IReadOnlyList<SkillDto>> GetAllAsync(int userId, bool isAdmin, CancellationToken ct = default);

    Task<SkillDto> CreateAsync(int userId, SkillUpsertRequest request, CancellationToken ct = default);

    Task<SkillDto> UpdateAsync(
        int skillId, int userId, bool isAdmin, SkillUpsertRequest request, CancellationToken ct = default);

    Task DeleteAsync(int skillId, int userId, bool isAdmin, CancellationToken ct = default);
}

public class SkillService(AppDbContext db, IAuditService audit) : ISkillService
{
    public async Task<IReadOnlyList<SkillDto>> GetAllAsync(
        int userId, bool isAdmin, CancellationToken ct = default)
    {
        List<Skill> skills = await db.Skills
            .AsNoTracking()
            .Where(s => !s.IsDeleted && (isAdmin || s.OwnerUserId == userId || s.IsShared))
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        if (skills.Count == 0) return [];

        int[] ownerIds = skills.Select(s => s.OwnerUserId).Distinct().ToArray();
        Dictionary<int, AppUser> owners = await db.Users.AsNoTracking()
            .Where(u => ownerIds.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, ct);

        return skills.Select(s => ToDto(s, owners[s.OwnerUserId], userId, isAdmin)).ToList();
    }

    public async Task<SkillDto> CreateAsync(
        int userId, SkillUpsertRequest request, CancellationToken ct = default)
    {
        AppUser owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, ct)
            ?? throw AppException.NotFound("User not found");

        var entity = new Skill
        {
            Name = request.Name.Trim(),
            Body = request.Body.Trim(),
            OwnerUserId = userId,
            IsShared = request.IsShared,
            CreatedAt = DateTime.Now,
        };

        db.Skills.Add(entity);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Chat, AuditActions.SkillCreated, userId, owner.Username,
            $"Created skill #{entity.SkillId} \"{entity.Name}\"" + (entity.IsShared ? " (shared)" : ""),
            isSuccess: true, ct);

        return ToDto(entity, owner, userId, isAdmin: false);
    }

    public async Task<SkillDto> UpdateAsync(
        int skillId, int userId, bool isAdmin, SkillUpsertRequest request, CancellationToken ct = default)
    {
        Skill entity = await db.Skills.FirstOrDefaultAsync(s => s.SkillId == skillId && !s.IsDeleted, ct)
            ?? throw AppException.NotFound("Skill not found");

        RequireEditor(entity, userId, isAdmin);

        AppUser owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == entity.OwnerUserId, ct)
            ?? throw AppException.NotFound("Skill owner no longer exists");

        entity.Name = request.Name.Trim();
        entity.Body = request.Body.Trim();
        entity.IsShared = request.IsShared;
        entity.UpdatedAt = DateTime.Now;

        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Chat, AuditActions.SkillUpdated, userId, owner.Username,
            $"Updated skill #{skillId} \"{entity.Name}\"", isSuccess: true, ct);

        return ToDto(entity, owner, userId, isAdmin);
    }

    public async Task DeleteAsync(int skillId, int userId, bool isAdmin, CancellationToken ct = default)
    {
        Skill entity = await db.Skills.FirstOrDefaultAsync(s => s.SkillId == skillId && !s.IsDeleted, ct)
            ?? throw AppException.NotFound("Skill not found");

        RequireEditor(entity, userId, isAdmin);

        entity.IsDeleted = true;
        entity.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Chat, AuditActions.SkillDeleted, userId, null,
            $"Deleted skill #{skillId} \"{entity.Name}\"", isSuccess: true, ct);
    }

    private static void RequireEditor(Skill skill, int userId, bool isAdmin)
    {
        if (skill.OwnerUserId != userId && !isAdmin)
        {
            throw AppException.Forbidden("Only the skill's owner or an administrator can change it");
        }
    }

    private static SkillDto ToDto(Skill s, AppUser owner, int callerId, bool isAdmin) => new(
        s.SkillId, s.Name, s.Body, s.OwnerUserId, owner.Username, owner.FullName, s.IsShared,
        CanEdit: s.OwnerUserId == callerId || isAdmin,
        s.CreatedAt, s.UpdatedAt);
}
