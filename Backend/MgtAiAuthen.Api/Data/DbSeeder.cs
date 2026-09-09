using MgtAiAuthen.Api.Options;
using MgtAiAuthen.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace MgtAiAuthen.Api.Data;

/// <summary>
/// สร้างผู้ใช้ตั้งต้นตาม section "SeedUsers" ใน appsettings ตอนแอปเริ่มทำงาน
/// ทำงานแบบ idempotent — ถ้ามี username นั้นอยู่แล้วจะไม่แก้ไขข้อมูลหรือรหัสผ่านเดิม
/// </summary>
public class DbSeeder(AppDbContext db, IConfiguration configuration, ILogger<DbSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!await db.Database.CanConnectAsync(ct))
        {
            logger.LogError(
                "Cannot reach the database — check ConnectionStrings:Default and run database/01_schema.sql first");
            return;
        }

        List<SeedUserOptions> seeds = configuration
            .GetSection("SeedUsers")
            .Get<List<SeedUserOptions>>() ?? [];

        if (seeds.Count == 0)
        {
            logger.LogInformation("No SeedUsers in appsettings — skipping initial user creation");
            return;
        }

        List<string> existing = await db.Users.Select(u => u.Username).ToListAsync(ct);
        int created = 0;

        foreach (SeedUserOptions seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed.Username) || string.IsNullOrWhiteSpace(seed.Password))
            {
                logger.LogWarning("Skipping a seed user with no username or password");
                continue;
            }

            if (existing.Contains(seed.Username, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!UserRoles.All.Contains(seed.UserRole))
            {
                logger.LogWarning(
                    "Seed user {Username} has unknown role {Role} — falling back to User",
                    seed.Username, seed.UserRole);
            }

            db.Users.Add(new AppUser
            {
                Username = seed.Username.Trim(),
                Email = seed.Email.Trim(),
                FullName = seed.FullName.Trim(),
                Department = string.IsNullOrWhiteSpace(seed.Department) ? null : seed.Department.Trim(),
                PasswordHash = PasswordHasher.Hash(seed.Password),
                UserRole = UserRoles.All.Contains(seed.UserRole) ? seed.UserRole : UserRoles.User,
                IsActive = true,
                MustChangePassword = false,
                CreatedAt = DateTime.Now,
            });

            created++;
            logger.LogInformation("Created seed user {Username} ({Role})", seed.Username, seed.UserRole);
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Seed users: {Created} created, {Existing} already existed",
            created, seeds.Count - created);
    }
}
