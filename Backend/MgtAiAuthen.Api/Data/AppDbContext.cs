using Microsoft.EntityFrameworkCore;

namespace MgtAiAuthen.Api.Data;

/// <summary>
/// EF Core context แบบ database-first — ตารางถูกสร้างจาก database/01_schema.sql
/// ไม่ได้ใช้ migration เพื่อให้ DDL อยู่ในสคริปต์เดียวที่ DBA ตรวจได้
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PolicyRule> PolicyRules => Set<PolicyRule>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ModelPricing> ModelPricing => Set<ModelPricing>();
    public DbSet<DataSource> DataSources => Set<DataSource>();
    public DbSet<DataSourceGrant> DataSourceGrants => Set<DataSourceGrant>();
    public DbSet<ChatAttachment> ChatAttachments => Set<ChatAttachment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.ToTable("Users");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasColumnName("UserID");
            e.Property(x => x.Username).HasColumnName("Username").HasMaxLength(100).IsRequired();
            e.Property(x => x.Email).HasColumnName("Email").HasMaxLength(200).IsRequired();
            e.Property(x => x.FullName).HasColumnName("FullName").HasMaxLength(200).IsRequired();
            e.Property(x => x.Department).HasColumnName("Department").HasMaxLength(100);
            e.Property(x => x.PasswordHash).HasColumnName("PasswordHash").HasMaxLength(400).IsRequired();
            e.Property(x => x.UserRole).HasColumnName("UserRole").HasMaxLength(20).IsRequired();
            e.Property(x => x.IsActive).HasColumnName("IsActive");
            e.Property(x => x.MustChangePassword).HasColumnName("MustChangePassword");
            e.Property(x => x.FailedLoginCount).HasColumnName("FailedLoginCount");
            e.Property(x => x.LockoutUntil).HasColumnName("LockoutUntil");
            e.Property(x => x.LastLoginAt).HasColumnName("LastLoginAt");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt");
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasKey(x => x.TokenId);
            e.Property(x => x.TokenId).HasColumnName("TokenID");
            e.Property(x => x.UserId).HasColumnName("UserID");
            e.Property(x => x.TokenHash).HasColumnName("TokenHash").HasMaxLength(200).IsRequired();
            e.Property(x => x.ExpiresAt).HasColumnName("ExpiresAt");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
            e.Property(x => x.CreatedByIp).HasColumnName("CreatedByIp").HasMaxLength(64);
            e.Property(x => x.RevokedAt).HasColumnName("RevokedAt");
            e.Property(x => x.RevokedReason).HasColumnName("RevokedReason").HasMaxLength(200);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.TokenHash);
        });

        b.Entity<PolicyRule>(e =>
        {
            e.ToTable("PolicyRules");
            e.HasKey(x => x.RuleId);
            e.Property(x => x.RuleId).HasColumnName("RuleID");
            e.Property(x => x.RuleName).HasColumnName("RuleName").HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasColumnName("Description").HasMaxLength(500);
            e.Property(x => x.MatchType).HasColumnName("MatchType").HasMaxLength(20).IsRequired();
            e.Property(x => x.Pattern).HasColumnName("Pattern").HasMaxLength(500).IsRequired();
            e.Property(x => x.ActionType).HasColumnName("ActionType").HasMaxLength(20).IsRequired();
            e.Property(x => x.Severity).HasColumnName("Severity").HasMaxLength(20).IsRequired();
            e.Property(x => x.IsActive).HasColumnName("IsActive");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
            e.Property(x => x.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(100);
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt");
            e.Property(x => x.UpdatedBy).HasColumnName("UpdatedBy").HasMaxLength(100);
        });

        b.Entity<ChatSession>(e =>
        {
            e.ToTable("ChatSessions");
            e.HasKey(x => x.SessionId);
            e.Property(x => x.SessionId).HasColumnName("SessionID").ValueGeneratedNever();
            e.Property(x => x.UserId).HasColumnName("UserID");
            e.Property(x => x.Title).HasColumnName("Title").HasMaxLength(300).IsRequired();
            e.Property(x => x.MessageCount).HasColumnName("MessageCount");
            e.Property(x => x.IsDeleted).HasColumnName("IsDeleted");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt");
            e.HasOne(x => x.User).WithMany(u => u.Sessions).HasForeignKey(x => x.UserId);
        });

        b.Entity<ChatMessage>(e =>
        {
            e.ToTable("ChatMessages");
            e.HasKey(x => x.MessageId);
            e.Property(x => x.MessageId).HasColumnName("MessageID");
            e.Property(x => x.SessionId).HasColumnName("SessionID");
            e.Property(x => x.UserId).HasColumnName("UserID");
            e.Property(x => x.MessageRole).HasColumnName("MessageRole").HasMaxLength(20).IsRequired();
            e.Property(x => x.Content).HasColumnName("Content").IsRequired();
            e.Property(x => x.QuestionType).HasColumnName("QuestionType").HasMaxLength(20);
            e.Property(x => x.ChatMode).HasColumnName("ChatMode").HasMaxLength(20);
            e.Property(x => x.ModelName).HasColumnName("ModelName").HasMaxLength(100);
            e.Property(x => x.InputTokens).HasColumnName("InputTokens");
            e.Property(x => x.OutputTokens).HasColumnName("OutputTokens");
            e.Property(x => x.CacheWriteTokens).HasColumnName("CacheWriteTokens");
            e.Property(x => x.CacheReadTokens).HasColumnName("CacheReadTokens");

            // Computed in SQL Server — EF must read it back but never write it.
            e.Property(x => x.TotalTokens)
                .HasColumnName("TotalTokens")
                .ValueGeneratedOnAddOrUpdate()
                .Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);

            e.Property(x => x.InputCostUsd).HasColumnName("InputCostUsd").HasPrecision(18, 8);
            e.Property(x => x.OutputCostUsd).HasColumnName("OutputCostUsd").HasPrecision(18, 8);
            e.Property(x => x.CacheCostUsd).HasColumnName("CacheCostUsd").HasPrecision(18, 8);
            e.Property(x => x.TotalCostUsd).HasColumnName("TotalCostUsd").HasPrecision(18, 8);
            e.Property(x => x.TotalCostThb).HasColumnName("TotalCostThb").HasPrecision(18, 6);
            e.Property(x => x.UsdToThbRate).HasColumnName("UsdToThbRate").HasPrecision(12, 4);
            e.Property(x => x.PricingId).HasColumnName("PricingID");
            e.Property(x => x.LatencyMs).HasColumnName("LatencyMs");
            e.Property(x => x.IsBlocked).HasColumnName("IsBlocked");
            e.Property(x => x.PolicyFlag).HasColumnName("PolicyFlag").HasMaxLength(20);
            e.Property(x => x.PolicyRuleId).HasColumnName("PolicyRuleID");
            e.Property(x => x.PolicyRuleName).HasColumnName("PolicyRuleName").HasMaxLength(200);
            e.Property(x => x.ClientIp).HasColumnName("ClientIp").HasMaxLength(64);
            e.Property(x => x.UserAgent).HasColumnName("UserAgent").HasMaxLength(400);
            e.Property(x => x.AttachmentCount).HasColumnName("AttachmentCount");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
            e.HasOne(x => x.Session).WithMany(s => s.Messages).HasForeignKey(x => x.SessionId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        b.Entity<ChatAttachment>(e =>
        {
            e.ToTable("ChatAttachments");
            e.HasKey(x => x.AttachmentId);
            e.Property(x => x.AttachmentId).HasColumnName("AttachmentID");
            e.Property(x => x.MessageId).HasColumnName("MessageID");
            e.Property(x => x.SessionId).HasColumnName("SessionID");
            e.Property(x => x.UserId).HasColumnName("UserID");
            e.Property(x => x.FileName).HasColumnName("FileName").HasMaxLength(300).IsRequired();
            e.Property(x => x.ContentType).HasColumnName("ContentType").HasMaxLength(150).IsRequired();
            e.Property(x => x.FileKind).HasColumnName("FileKind").HasMaxLength(20).IsRequired();
            e.Property(x => x.SizeBytes).HasColumnName("SizeBytes");
            e.Property(x => x.Sha256).HasColumnName("Sha256").HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.StoredPath).HasColumnName("StoredPath").HasMaxLength(500).IsRequired();
            e.Property(x => x.IsTextExtracted).HasColumnName("IsTextExtracted");
            e.Property(x => x.ExtractedChars).HasColumnName("ExtractedChars");
            e.Property(x => x.PolicyScanned).HasColumnName("PolicyScanned");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
            e.HasOne(x => x.Message).WithMany(m => m.Attachments).HasForeignKey(x => x.MessageId);
            e.HasIndex(x => x.MessageId);
        });

        b.Entity<ModelPricing>(e =>
        {
            e.ToTable("ModelPricing");
            e.HasKey(x => x.PricingId);
            e.Property(x => x.PricingId).HasColumnName("PricingID");
            e.Property(x => x.Provider).HasColumnName("Provider").HasMaxLength(20).IsRequired();
            e.Property(x => x.ModelName).HasColumnName("ModelName").HasMaxLength(100).IsRequired();
            e.Property(x => x.InputUsdPerMTok).HasColumnName("InputUsdPerMTok").HasPrecision(12, 4);
            e.Property(x => x.OutputUsdPerMTok).HasColumnName("OutputUsdPerMTok").HasPrecision(12, 4);
            e.Property(x => x.CacheWriteUsdPerMTok).HasColumnName("CacheWriteUsdPerMTok").HasPrecision(12, 4);
            e.Property(x => x.CacheReadUsdPerMTok).HasColumnName("CacheReadUsdPerMTok").HasPrecision(12, 4);
            e.Property(x => x.UsdToThbRate).HasColumnName("UsdToThbRate").HasPrecision(12, 4);
            e.Property(x => x.EffectiveFrom).HasColumnName("EffectiveFrom");
            e.Property(x => x.IsActive).HasColumnName("IsActive");
            e.Property(x => x.Notes).HasColumnName("Notes").HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
            e.Property(x => x.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(100);
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt");
            e.Property(x => x.UpdatedBy).HasColumnName("UpdatedBy").HasMaxLength(100);
            e.HasIndex(x => x.ModelName).IsUnique();
        });

        b.Entity<DataSource>(e =>
        {
            e.ToTable("DataSources");
            e.HasKey(x => x.SourceId);
            e.Property(x => x.SourceId).HasColumnName("SourceId");
            e.Property(x => x.SourceName).HasColumnName("SourceName").HasMaxLength(150).IsRequired();
            e.Property(x => x.SourceType).HasColumnName("SourceType").HasMaxLength(20).IsRequired();
            e.Property(x => x.Description).HasColumnName("Description").HasMaxLength(500);
            e.Property(x => x.ConfigJson).HasColumnName("ConfigJson");
            e.Property(x => x.EncryptedSecret).HasColumnName("EncryptedSecret");
            e.Property(x => x.IsActive).HasColumnName("IsActive");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
            e.Property(x => x.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(100);
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt");
            e.Property(x => x.UpdatedBy).HasColumnName("UpdatedBy").HasMaxLength(100);
            e.HasIndex(x => x.SourceName).IsUnique();
        });

        b.Entity<DataSourceGrant>(e =>
        {
            e.ToTable("DataSourceGrants");
            e.HasKey(x => x.GrantId);
            e.Property(x => x.GrantId).HasColumnName("GrantId");
            e.Property(x => x.SourceId).HasColumnName("SourceId");
            e.Property(x => x.UserId).HasColumnName("UserId");
            e.Property(x => x.ScopeType).HasColumnName("ScopeType").HasMaxLength(20).IsRequired();
            e.Property(x => x.ScopeFilter).HasColumnName("ScopeFilter").HasMaxLength(1000);
            e.Property(x => x.Notes).HasColumnName("Notes").HasMaxLength(500);
            e.Property(x => x.GrantedAt).HasColumnName("GrantedAt");
            e.Property(x => x.GrantedBy).HasColumnName("GrantedBy").HasMaxLength(100);
            e.Property(x => x.IsActive).HasColumnName("IsActive");
            e.Property(x => x.RevokedAt).HasColumnName("RevokedAt");
            e.Property(x => x.RevokedBy).HasColumnName("RevokedBy").HasMaxLength(100);

            // ไม่ผูก navigation ไปยัง AppUser/DataSource โดยเจตนา — ตามแบบ ChatMessages ที่ไม่ผูก
            // navigation กับ Users เพื่อไม่ให้ EF พยายาม cascade delete ข้ามตารางสิทธิ์
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DataSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLogs");
            e.HasKey(x => x.AuditId);
            e.Property(x => x.AuditId).HasColumnName("AuditID");
            e.Property(x => x.UserId).HasColumnName("UserID");
            e.Property(x => x.Username).HasColumnName("Username").HasMaxLength(100);
            e.Property(x => x.Category).HasColumnName("Category").HasMaxLength(30).IsRequired();
            e.Property(x => x.Action).HasColumnName("Action").HasMaxLength(60).IsRequired();
            e.Property(x => x.Detail).HasColumnName("Detail");
            e.Property(x => x.IsSuccess).HasColumnName("IsSuccess");
            e.Property(x => x.ClientIp).HasColumnName("ClientIp").HasMaxLength(64);
            e.Property(x => x.UserAgent).HasColumnName("UserAgent").HasMaxLength(400);
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt");
        });
    }
}
