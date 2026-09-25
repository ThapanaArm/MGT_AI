using System.ComponentModel.DataAnnotations;

namespace MgtAiAuthen.Api.Contracts;

// ---------------------------------------------------------------- ทั่วไป

/// <summary>ผลลัพธ์แบบแบ่งหน้า ใช้กับทุก endpoint ที่คืนรายการยาว</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>รูปแบบ error ที่ API ตอบกลับเสมอ เพื่อให้ frontend จัดการที่เดียว</summary>
public record ApiError(string Message, string? Code = null, IDictionary<string, string[]>? Errors = null);

// ---------------------------------------------------------------- Auth

public record LoginRequest
{
    [Required(ErrorMessage = "Username is required")]
    public string Username { get; init; } = string.Empty;

    [Required(ErrorMessage = "Password is required")]
    public string Password { get; init; } = string.Empty;
}

public record RefreshRequest
{
    [Required]
    public string RefreshToken { get; init; } = string.Empty;
}

public record ChangePasswordRequest
{
    [Required(ErrorMessage = "Current password is required")]
    public string CurrentPassword { get; init; } = string.Empty;

    [Required(ErrorMessage = "New password is required")]
    public string NewPassword { get; init; } = string.Empty;
}

public record UserProfileDto(
    int UserId,
    string Username,
    string FullName,
    string Email,
    string? Department,
    string UserRole,
    bool MustChangePassword,
    DateTime? LastLoginAt);

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresAt,
    UserProfileDto User);

// ---------------------------------------------------------------- Chat

public record ChatSendRequest
{
    /// <summary>ว่างไว้ = เริ่มบทสนทนาใหม่</summary>
    public Guid? SessionId { get; init; }

    /// <summary>
    /// ว่างได้เมื่อมีไฟล์แนบ (ระบบจะใส่คำสั่ง "ช่วยวิเคราะห์ไฟล์" ให้)
    /// จึงไม่ใส่ [Required] — ChatService ตรวจว่าต้องมีข้อความหรือไฟล์อย่างน้อยหนึ่งอย่าง
    /// </summary>
    [StringLength(20000, ErrorMessage = "Message exceeds 20,000 characters")]
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Model to answer with. Blank keeps whatever the conversation already used, falling back to
    /// the configured default. Must be a model that has active pricing.
    /// </summary>
    [StringLength(100)]
    public string? Model { get; init; }

    /// <summary>
    /// "Chat" (default) or "Code". Code mode swaps in a programming-oriented system prompt.
    /// Blank keeps the mode the conversation last used.
    /// </summary>
    [RegularExpression("^(?i)(Chat|Code)$", ErrorMessage = "Mode must be Chat or Code")]
    public string? Mode { get; init; }

    /// <summary>
    /// Ties a brand-new conversation to a Project — ignored when SessionId is set, because a
    /// session's project is fixed at creation (see Project.cs for why).
    /// </summary>
    public int? ProjectId { get; init; }

    /// <summary>
    /// Ties a brand-new conversation to a Data Source the caller has an active grant for —
    /// ignored when SessionId is set, same immutable-at-creation rule as ProjectId.
    /// </summary>
    public int? DataSourceId { get; init; }
}

/// <summary>One model the user may pick, with enough pricing context to choose sensibly.</summary>
public record AvailableModelDto(
    string Name,
    /// <summary>Anthropic | Google | OpenAI — the vendor that answers this model.</summary>
    string Provider,
    /// <summary>false = that vendor has no API key, so picking this model returns 503.</summary>
    bool ProviderReady,
    bool IsDefault,
    decimal InputUsdPerMTok,
    decimal OutputUsdPerMTok,
    decimal UsdToThbRate,
    string? Notes,
    /// <summary>Indicative cost in THB for a typical 700-in / 600-out question.</summary>
    decimal SampleCostThb);

/// <summary>One attachment — metadata only, the file content is never returned here.</summary>
public record ChatAttachmentDto(
    long AttachmentId,
    string FileName,
    string ContentType,
    string FileKind,
    long SizeBytes,
    bool IsTextExtracted,
    /// <summary>false = คัดกรอง policy ได้แค่ชื่อไฟล์ (PDF/รูปภาพ)</summary>
    bool PolicyScanned,
    string Sha256,
    DateTime CreatedAt);

public record ChatMessageDto(
    long MessageId,
    string MessageRole,
    string Content,
    string? QuestionType,
    bool IsBlocked,
    string? PolicyFlag,
    string? PolicyRuleName,
    string? ChatMode,
    string? ModelName,
    int? InputTokens,
    int? OutputTokens,
    int? CacheWriteTokens,
    int? CacheReadTokens,
    int TotalTokens,
    decimal? TotalCostUsd,
    decimal? TotalCostThb,
    int? LatencyMs,
    IReadOnlyList<ChatAttachmentDto> Attachments,
    DateTime CreatedAt);

/// <summary>ข้อจำกัดการอัพโหลด — frontend ใช้ตรวจก่อนส่งเพื่อไม่ให้ผู้ใช้รอเสียเวลา</summary>
public record UploadLimitsDto(
    int MaxFileMb,
    int MaxFilesPerMessage,
    int MaxTotalMbPerMessage,
    IReadOnlyList<string> AllowedExtensions,
    IReadOnlyList<string> PolicyScannableExtensions);

public record ChatSendResponse(
    Guid SessionId,
    string SessionTitle,
    ChatMessageDto UserMessage,
    ChatMessageDto? AssistantMessage,
    bool Blocked,
    string? PolicyNotice);

public record ChatSessionDto(
    Guid SessionId,
    string Title,
    int MessageCount,
    int? ProjectId,
    string? ProjectName,
    int? DataSourceId,
    string? DataSourceName,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Moves an existing conversation into a project the caller owns — see ChatService.SetSessionProjectAsync.</summary>
public record SetSessionProjectRequest(int ProjectId);

// ---------------------------------------------------------------- ค้นหา Chat log

/// <summary>
/// เงื่อนไขค้นหา log การแชท — ทุกฟิลด์เป็น optional และรวมกันแบบ AND
/// ผูกจาก query string เช่น
/// ?keyword=ที่ไหน&amp;questionType=WHERE&amp;from=2026-09-01&amp;userId=2
/// </summary>
public class ChatLogQuery
{
    /// <summary>ค้นคำในเนื้อหาข้อความ (LIKE %...%)</summary>
    public string? Keyword { get; set; }

    public int? UserId { get; set; }

    /// <summary>ค้นบางส่วนของชื่อผู้ใช้ / ชื่อ-นามสกุล</summary>
    public string? User { get; set; }

    public string? Department { get; set; }

    /// <summary>"user" = เฉพาะที่พนักงานถาม, "assistant" = เฉพาะที่ AI ตอบ</summary>
    public string? MessageRole { get; set; }

    /// <summary>WHAT / WHERE / HOW / WHY / WHEN / WHO / HOWMUCH / OTHER</summary>
    public string? QuestionType { get; set; }

    /// <summary>Block / Warn / Audit</summary>
    public string? PolicyFlag { get; set; }

    public bool? OnlyBlocked { get; set; }

    /// <summary>true = เฉพาะข้อความที่มีไฟล์แนบ</summary>
    public bool? HasAttachments { get; set; }

    /// <summary>Chat | Code</summary>
    public string? ChatMode { get; set; }

    public Guid? SessionId { get; set; }

    public DateTime? From { get; set; }

    /// <summary>รวมทั้งวันของวันที่ระบุ (ระบบจะขยายไปถึง 23:59:59 ให้เอง)</summary>
    public DateTime? To { get; set; }

    /// <summary>CreatedAt | Username | QuestionType | OutputTokens | LatencyMs</summary>
    public string SortBy { get; set; } = "CreatedAt";

    /// <summary>asc | desc</summary>
    public string SortDir { get; set; } = "desc";

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public record ChatLogItemDto(
    long MessageId,
    Guid SessionId,
    string SessionTitle,
    int UserId,
    string Username,
    string FullName,
    string? Department,
    string MessageRole,
    string Content,
    string? QuestionType,
    bool IsBlocked,
    string? PolicyFlag,
    string? PolicyRuleName,
    string? ChatMode,
    string? ModelName,
    int? InputTokens,
    int? OutputTokens,
    int? CacheWriteTokens,
    int? CacheReadTokens,
    int TotalTokens,
    decimal? InputCostUsd,
    decimal? OutputCostUsd,
    decimal? CacheCostUsd,
    decimal? TotalCostUsd,
    decimal? TotalCostThb,
    decimal? UsdToThbRate,
    int? LatencyMs,
    string? ClientIp,
    int AttachmentCount,
    IReadOnlyList<ChatAttachmentDto> Attachments,
    int? ProjectId,
    string? ProjectName,
    int? DataSourceId,
    string? DataSourceName,
    DateTime CreatedAt);

public record NamedCount(string Key, string Label, int Count);

/// <summary>Cost and token totals for one grouping (per user / per day / per model).</summary>
public record CostBucket(
    string Key,
    string Label,
    int MessageCount,
    long InputTokens,
    long OutputTokens,
    long CacheWriteTokens,
    long CacheReadTokens,
    long TotalTokens,
    decimal TotalCostUsd,
    decimal TotalCostThb);

public record ChatLogStats(
    int TotalMessages,
    int QuestionCount,
    int AnswerCount,
    int BlockedCount,
    int FlaggedCount,
    int DistinctUsers,
    int DistinctSessions,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheWriteTokens,
    long TotalCacheReadTokens,
    long TotalTokens,
    decimal TotalCostUsd,
    decimal TotalCostThb,
    IReadOnlyList<NamedCount> ByQuestionType,
    IReadOnlyList<NamedCount> ByUser,
    IReadOnlyList<NamedCount> ByDay);

/// <summary>Cost report — accepts the same filters as /logs/chat.</summary>
public record CostReport(
    CostBucket Total,
    IReadOnlyList<CostBucket> ByUser,
    IReadOnlyList<CostBucket> ByModel,
    IReadOnlyList<CostBucket> ByDay,
    IReadOnlyList<CostBucket> ByDepartment,
    decimal AverageCostThbPerQuestion,
    decimal MaxCostThbSingleMessage);

public record UserOption(int UserId, string Username, string FullName, string? Department, string UserRole);

/// <summary>ค่าที่ใช้เติม dropdown ตัวกรองบนหน้าค้นหา log — โหลดครั้งเดียวตอนเปิดหน้า</summary>
public record LogFilterOptions(
    IReadOnlyList<UserOption> Users,
    IReadOnlyList<string> Departments,
    IReadOnlyList<NamedCount> QuestionTypes,
    IReadOnlyList<string> PolicyFlags,
    IReadOnlyList<string> AuditCategories,
    IReadOnlyList<string> AuditActions);

// ---------------------------------------------------------------- ค้นหา Audit log

public class AuditLogQuery
{
    public string? Keyword { get; set; }
    public int? UserId { get; set; }
    public string? User { get; set; }

    /// <summary>AUTH / CHAT / ADMIN / POLICY</summary>
    public string? Category { get; set; }

    public string? Action { get; set; }
    public bool? OnlyFailed { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public record AuditLogItemDto(
    long AuditId,
    int? UserId,
    string? Username,
    string Category,
    string Action,
    string? Detail,
    bool IsSuccess,
    string? ClientIp,
    DateTime CreatedAt);

// ---------------------------------------------------------------- Policy rules

public record PolicyRuleDto(
    int RuleId,
    string RuleName,
    string? Description,
    string MatchType,
    string Pattern,
    string ActionType,
    string Severity,
    bool IsActive,
    DateTime CreatedAt,
    string? CreatedBy,
    DateTime? UpdatedAt,
    string? UpdatedBy);

public record PolicyRuleUpsertRequest
{
    [Required(ErrorMessage = "Rule name is required")]
    [StringLength(200)]
    public string RuleName { get; init; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; init; }

    [Required]
    [RegularExpression("^(Keyword|Regex)$", ErrorMessage = "MatchType must be Keyword or Regex")]
    public string MatchType { get; init; } = "Keyword";

    [Required(ErrorMessage = "Enter the keyword or pattern to detect")]
    [StringLength(500)]
    public string Pattern { get; init; } = string.Empty;

    [Required]
    [RegularExpression("^(Block|Warn|Audit)$", ErrorMessage = "ActionType must be Block, Warn or Audit")]
    public string ActionType { get; init; } = "Warn";

    [Required]
    [RegularExpression("^(Low|Medium|High)$", ErrorMessage = "Severity must be Low, Medium or High")]
    public string Severity { get; init; } = "Medium";

    public bool IsActive { get; init; } = true;
}

/// <summary>ใช้ทดลองยิงข้อความผ่านชุดเงื่อนไขก่อนเปิดใช้จริง</summary>
public record PolicyTestRequest
{
    [Required]
    public string Message { get; init; } = string.Empty;
}

public record PolicyTestResponse(
    string? Flag,
    int? RuleId,
    string? RuleName,
    string? Severity,
    string? Notice,
    string QuestionType);

// ---------------------------------------------------------------- Model pricing

public record ModelPricingDto(
    int PricingId,
    string Provider,
    string ModelName,
    decimal InputUsdPerMTok,
    decimal OutputUsdPerMTok,
    decimal CacheWriteUsdPerMTok,
    decimal CacheReadUsdPerMTok,
    decimal UsdToThbRate,
    DateTime EffectiveFrom,
    bool IsActive,
    string? Notes,
    int MessageCount,
    decimal TotalCostThb,
    DateTime CreatedAt,
    string? CreatedBy,
    DateTime? UpdatedAt,
    string? UpdatedBy);

public record ModelPricingUpsertRequest
{
    [Required(ErrorMessage = "Model name is required")]
    [StringLength(100)]
    public string ModelName { get; init; } = string.Empty;

    /// <summary>
    /// Which vendor answers this model. On create, blank means Anthropic so requests written
    /// before Gemini support still work; on update, blank leaves the current provider alone.
    /// An unknown name is rejected rather than silently reassigned, because a wrong provider
    /// means the call goes to the wrong account.
    /// </summary>
    [MgtAiAuthen.Api.Infrastructure.AiProvider]
    public string? Provider { get; init; }

    [Range(0, 10000, ErrorMessage = "Input price must not be negative")]
    public decimal InputUsdPerMTok { get; init; }

    [Range(0, 10000, ErrorMessage = "Output price must not be negative")]
    public decimal OutputUsdPerMTok { get; init; }

    [Range(0, 10000, ErrorMessage = "Cache write price must not be negative")]
    public decimal CacheWriteUsdPerMTok { get; init; }

    [Range(0, 10000, ErrorMessage = "Cache read price must not be negative")]
    public decimal CacheReadUsdPerMTok { get; init; }

    [Range(0.0001, 10000, ErrorMessage = "Exchange rate must be greater than 0")]
    public decimal UsdToThbRate { get; init; } = 36.5m;

    [StringLength(500)]
    public string? Notes { get; init; }

    public bool IsActive { get; init; } = true;
}

// ---------------------------------------------------------------- จัดการผู้ใช้

public record AdminUserDto(
    int UserId,
    string Username,
    string FullName,
    string Email,
    string? Department,
    string UserRole,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LastLoginAt,
    DateTime? LockoutUntil,
    int FailedLoginCount,
    int MessageCount,
    DateTime CreatedAt);

public record CreateUserRequest
{
    [Required(ErrorMessage = "Username is required")]
    [StringLength(100, MinimumLength = 3)]
    [RegularExpression("^[A-Za-z0-9._-]+$", ErrorMessage = "Username may only contain A-Z a-z 0-9 . _ -")]
    public string Username { get; init; } = string.Empty;

    [Required(ErrorMessage = "Password is required")]
    public string Password { get; init; } = string.Empty;

    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    [StringLength(200)]
    public string Email { get; init; } = string.Empty;

    [Required(ErrorMessage = "Full name is required")]
    [StringLength(200)]
    public string FullName { get; init; } = string.Empty;

    [StringLength(100)]
    public string? Department { get; init; }

    [Required]
    [RegularExpression("^(Admin|Auditor|User)$", ErrorMessage = "Role must be Admin, Auditor or User")]
    public string UserRole { get; init; } = "User";

    public bool MustChangePassword { get; init; } = true;
}

public record UpdateUserRequest
{
    [Required]
    [EmailAddress]
    [StringLength(200)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string FullName { get; init; } = string.Empty;

    [StringLength(100)]
    public string? Department { get; init; }

    [Required]
    [RegularExpression("^(Admin|Auditor|User)$")]
    public string UserRole { get; init; } = "User";

    public bool IsActive { get; init; } = true;
}

public record ResetPasswordRequest
{
    [Required(ErrorMessage = "New password is required")]
    public string NewPassword { get; init; } = string.Empty;

    public bool MustChangePassword { get; init; } = true;
}

// ---------------------------------------------------------------- ทะเบียนแหล่งข้อมูล (Phase 1: ทะเบียน+สิทธิ์ ยังไม่ต่อกับแชท)

/// <summary>
/// หนึ่งแหล่งข้อมูล — ไม่คืนความลับ (secret) กลับไปเลย มีแค่ <see cref="HasSecret"/> บอกว่าตั้งไว้แล้วหรือยัง
/// </summary>
public record DataSourceDto(
    int SourceId,
    string SourceName,
    string SourceType,
    string? Description,
    /// <summary>ค่าที่ไม่ใช่ความลับ เช่น baseUrl, path, tenantId — รูปแบบคีย์ต่างกันตาม SourceType</summary>
    Dictionary<string, string> Config,
    bool HasSecret,
    bool IsActive,
    int ActiveGrantCount,
    DateTime CreatedAt,
    string? CreatedBy,
    DateTime? UpdatedAt,
    string? UpdatedBy);

public record DataSourceUpsertRequest
{
    [Required(ErrorMessage = "Source name is required")]
    [StringLength(150)]
    public string SourceName { get; init; } = string.Empty;

    [Required(ErrorMessage = "Source type is required")]
    [MgtAiAuthen.Api.Infrastructure.DataSourceType]
    public string SourceType { get; init; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; init; }

    /// <summary>ค่าที่ไม่ใช่ความลับ — คีย์ที่ต้องมีต่างกันตาม SourceType (ดู DataSourceConfig.RequiredKeys)</summary>
    public Dictionary<string, string> Config { get; init; } = new();

    /// <summary>
    /// API key / client secret / app secret เป็น plain text ตอนส่งเข้ามาเท่านั้น — เข้ารหัสก่อน
    /// เก็บเสมอ ไม่ส่งมา (null) ตอนแก้ไข = คงค่าความลับเดิมไว้ ส่งสตริงว่าง = ล้างความลับทิ้ง
    /// </summary>
    public string? Secret { get; init; }

    public bool IsActive { get; init; } = true;
}

public record DataSourceTestResult(bool Success, string Message, DateTime TestedAt);

/// <summary>สิทธิ์การเข้าถึงหนึ่งแหล่งข้อมูลของพนักงานหนึ่งคน</summary>
public record DataSourceGrantDto(
    long GrantId,
    int SourceId,
    string SourceName,
    string SourceType,
    int UserId,
    string Username,
    string FullName,
    string? Department,
    string ScopeType,
    string? ScopeFilter,
    string? Notes,
    DateTime GrantedAt,
    string? GrantedBy,
    bool IsActive,
    DateTime? RevokedAt,
    string? RevokedBy);

public record DataSourceGrantRequest
{
    [Required]
    public int SourceId { get; init; }

    [Required]
    public int UserId { get; init; }

    [Required(ErrorMessage = "Scope type is required")]
    [MgtAiAuthen.Api.Infrastructure.DataSourceScopeType]
    public string ScopeType { get; init; } = "Full";

    [StringLength(1000)]
    public string? ScopeFilter { get; init; }

    [StringLength(500)]
    public string? Notes { get; init; }
}

// ---------------------------------------------------------------- ดึงข้อมูลจากแหล่งข้อมูลเข้าแชท (Phase 2)

/// <summary>
/// One data source the caller may attach to a brand-new conversation — only sources they hold an
/// active grant for, never the full registry (that stays admin-only via AdminDataSourcesController).
/// </summary>
public record AvailableDataSourceDto(
    int SourceId,
    string SourceName,
    string SourceType,
    string? Description,
    string ScopeType,
    string? ScopeFilter);

/// <summary>
/// The cached result of pulling data for one conversation's Data Source — null fields mean nothing
/// has been fetched yet (e.g. the session predates this feature, or has no Data Source at all).
/// </summary>
public record DataSourceFetchStatusDto(
    int? SourceId,
    string? SourceName,
    bool? Success,
    string? Message,
    DateTime? FetchedAt,
    int? CharCount,
    bool? Truncated);

// ---------------------------------------------------------------- Projects (self-service)

public record ProjectDto(
    int ProjectId,
    string Name,
    string? Instructions,
    int OwnerUserId,
    string OwnerUsername,
    string OwnerFullName,
    bool IsShared,
    /// <summary>true = the caller may edit instructions, add/remove files, or delete this project.</summary>
    bool CanEdit,
    int FileCount,
    int SessionCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record ProjectUpsertRequest
{
    [Required(ErrorMessage = "Project name is required")]
    [StringLength(150)]
    public string Name { get; init; } = string.Empty;

    [StringLength(20000, ErrorMessage = "Instructions exceed 20,000 characters")]
    public string? Instructions { get; init; }

    public bool IsShared { get; init; }
}

public record ProjectFileDto(
    long ProjectFileId,
    string FileName,
    string ContentType,
    string FileKind,
    long SizeBytes,
    bool IsTextExtracted,
    /// <summary>false = policy screened by file name only (PDF/images) — same limitation as chat attachments.</summary>
    bool PolicyScanned,
    string Sha256,
    string UploadedByUsername,
    DateTime CreatedAt);

// ---------------------------------------------------------------- Skills (self-service)

public record SkillDto(
    int SkillId,
    string Name,
    string Body,
    int OwnerUserId,
    string OwnerUsername,
    string OwnerFullName,
    bool IsShared,
    bool CanEdit,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record SkillUpsertRequest
{
    [Required(ErrorMessage = "Skill name is required")]
    [StringLength(150)]
    public string Name { get; init; } = string.Empty;

    [Required(ErrorMessage = "Skill body is required")]
    [StringLength(20000, ErrorMessage = "Skill body exceeds 20,000 characters")]
    public string Body { get; init; } = string.Empty;

    public bool IsShared { get; init; }
}
