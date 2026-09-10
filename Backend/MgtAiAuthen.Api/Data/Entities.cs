namespace MgtAiAuthen.Api.Data;

/// <summary>User roles — used as the "role" claim value and in [Authorize(Roles = ...)].</summary>
public static class UserRoles
{
    /// <summary>ดู log ได้ทุกคน + จัดการผู้ใช้และ policy</summary>
    public const string Admin = "Admin";

    /// <summary>ดู log ได้ทุกคน แต่แก้ไขอะไรไม่ได้</summary>
    public const string Auditor = "Auditor";

    /// <summary>ใช้แชทได้ และดูได้แค่ประวัติของตัวเอง</summary>
    public const string User = "User";

    /// <summary>ใช้กับ [Authorize(Roles = ...)] สำหรับ endpoint read log</summary>
    public const string AdminOrAuditor = Admin + "," + Auditor;

    public static readonly string[] All = [Admin, Auditor, User];
}

/// <summary>หมวดของเหตุการณ์ใน AuditLogs</summary>
public static class AuditCategories
{
    public const string Auth = "AUTH";
    public const string Chat = "CHAT";
    public const string Admin = "ADMIN";
    public const string Policy = "POLICY";
}

/// <summary>ชื่อเหตุการณ์ใน AuditLogs</summary>
public static class AuditActions
{
    public const string LoginSuccess = "LOGIN_SUCCESS";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string LoginLockedOut = "LOGIN_LOCKED_OUT";
    public const string Logout = "LOGOUT";
    public const string TokenRefreshed = "TOKEN_REFRESHED";
    public const string PasswordChanged = "PASSWORD_CHANGED";

    public const string ChatSent = "CHAT_SENT";
    public const string ChatBlocked = "CHAT_BLOCKED";
    public const string ChatFlagged = "CHAT_FLAGGED";
    public const string ChatFailed = "CHAT_FAILED";
    public const string SessionDeleted = "SESSION_DELETED";

    public const string FileUploaded = "FILE_UPLOADED";
    public const string FileRejected = "FILE_REJECTED";
    public const string FileDownloaded = "FILE_DOWNLOADED";
    public const string ReportExported = "REPORT_EXPORTED";

    public const string ChatLogSearched = "CHAT_LOG_SEARCHED";
    public const string ChatLogExported = "CHAT_LOG_EXPORTED";
    public const string AuditLogSearched = "AUDIT_LOG_SEARCHED";
    public const string CostReportViewed = "COST_REPORT_VIEWED";

    public const string PricingCreated = "PRICING_CREATED";
    public const string PricingUpdated = "PRICING_UPDATED";

    public const string UserCreated = "USER_CREATED";
    public const string UserUpdated = "USER_UPDATED";
    public const string UserPasswordReset = "USER_PASSWORD_RESET";

    public const string PolicyCreated = "POLICY_CREATED";
    public const string PolicyUpdated = "POLICY_UPDATED";
    public const string PolicyDeleted = "POLICY_DELETED";

    public const string DataSourceCreated = "DATASOURCE_CREATED";
    public const string DataSourceUpdated = "DATASOURCE_UPDATED";
    public const string DataSourceTested = "DATASOURCE_TESTED";
    public const string DataSourceGranted = "DATASOURCE_GRANTED";
    public const string DataSourceRevoked = "DATASOURCE_REVOKED";
    public const string DataSourceFetched = "DATASOURCE_FETCHED";
    public const string DataSourceFetchFailed = "DATASOURCE_FETCH_FAILED";

    public const string ProjectCreated = "PROJECT_CREATED";
    public const string ProjectUpdated = "PROJECT_UPDATED";
    public const string ProjectDeleted = "PROJECT_DELETED";
    public const string ProjectFileUploaded = "PROJECT_FILE_UPLOADED";
    public const string ProjectFileRejected = "PROJECT_FILE_REJECTED";
    public const string ProjectFileDownloaded = "PROJECT_FILE_DOWNLOADED";
    public const string ProjectFileDeleted = "PROJECT_FILE_DELETED";

    public const string SkillCreated = "SKILL_CREATED";
    public const string SkillUpdated = "SKILL_UPDATED";
    public const string SkillDeleted = "SKILL_DELETED";
}

/// <summary>ผลของการคัดกรองข้อความตาม PolicyRules</summary>
public static class PolicyFlags
{
    /// <summary>ไม่ส่งให้ AI เลย</summary>
    public const string Block = "Block";

    /// <summary>ส่งให้ AI แต่เตือนผู้ใช้และติดธงไว้</summary>
    public const string Warn = "Warn";

    /// <summary>ส่งตามปกติ ติดธงไว้ให้ผู้ตรวจสอบเห็น</summary>
    public const string Audit = "Audit";
}

/// <summary>The role of a message within a conversation (column ChatMessages.MessageRole).</summary>
public static class MessageRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
}

/// <summary>
/// How the assistant should behave for a message (column ChatMessages.ChatMode).
/// Stored per message so a user can switch mid-conversation and the audit trail still shows
/// which mode produced each answer.
/// </summary>
public static class ChatModes
{
    /// <summary>General assistant — short prose answers.</summary>
    public const string Chat = "Chat";

    /// <summary>Programming assistant — complete, runnable code in fenced blocks.</summary>
    public const string Code = "Code";

    public static readonly string[] All = [Chat, Code];

    /// <summary>Normalises casing, so "code" and "CODE" both store as "Code".</summary>
    public static string Normalise(string? mode)
        => All.FirstOrDefault(m => string.Equals(m, mode?.Trim(), StringComparison.OrdinalIgnoreCase))
           ?? Chat;

    public static bool IsKnown(string? mode)
        => string.IsNullOrWhiteSpace(mode)
           || All.Contains(mode.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>dbo.Users</summary>
public class AppUser
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Department { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public string UserRole { get; set; } = UserRoles.User;
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<ChatSession> Sessions { get; set; } = new List<ChatSession>();
}

/// <summary>dbo.RefreshTokens</summary>
public class RefreshToken
{
    public long TokenId { get; set; }
    public int UserId { get; set; }

    /// <summary>SHA-256 (base64) ของ refresh token — ไม่เก็บค่าจริง</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string? CreatedByIp { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    public AppUser? User { get; set; }
}

/// <summary>dbo.PolicyRules</summary>
public class PolicyRule
{
    public int RuleId { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>"Keyword" หรือ "Regex"</summary>
    public string MatchType { get; set; } = "Keyword";

    public string Pattern { get; set; } = string.Empty;

    /// <summary>"Block" / "Warn" / "Audit" — ดู <see cref="PolicyFlags"/></summary>
    public string ActionType { get; set; } = PolicyFlags.Warn;

    public string Severity { get; set; } = "Medium";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>dbo.ChatSessions</summary>
public class ChatSession
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public int UserId { get; set; }
    public string Title { get; set; } = "New conversation";
    public int MessageCount { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>Set only when the session is created; never changed afterwards — see Project.</summary>
    public int? ProjectId { get; set; }

    /// <summary>Set only when the session is created; never changed afterwards — see DataSource.</summary>
    public int? DataSourceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public AppUser? User { get; set; }
    public Project? Project { get; set; }
    public DataSource? DataSource { get; set; }
    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}

/// <summary>
/// dbo.ChatSessionDataFetches — one row per conversation, the cached result of pulling data from
/// that session's DataSource. Fetched once when the conversation starts; "Refresh" overwrites this
/// same row rather than keeping a history (the audit log already records every fetch/refresh via
/// AuditActions.DataSourceFetched/DataSourceFetchFailed).
/// </summary>
public class ChatSessionDataFetch
{
    public Guid SessionId { get; set; }
    public int SourceId { get; set; }

    public DateTime FetchedAt { get; set; } = DateTime.Now;
    public int FetchedByUserId { get; set; }

    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>Null when Success is false — nothing to send to the AI.</summary>
    public string? ContentText { get; set; }
    public int CharCount { get; set; }
    public bool Truncated { get; set; }

    public ChatSession? Session { get; set; }
    public DataSource? Source { get; set; }
}

/// <summary>
/// dbo.Projects — a self-service workspace: standing instructions appended to the system prompt
/// of every message in every conversation under it, plus reference files resent every turn the
/// same way a chat attachment is. Unlike the data source registry, any employee creates and owns
/// these; IsShared only widens who may *use* one (start a chat in it), never who may edit it.
/// </summary>
public class Project
{
    public int ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Instructions { get; set; }
    public int OwnerUserId { get; set; }
    public bool IsShared { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>dbo.ProjectFiles — reference material attached at the project level, not one message.</summary>
public class ProjectFile
{
    public long ProjectFileId { get; set; }
    public int ProjectId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string FileKind { get; set; } = FileKinds.Text;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public bool IsTextExtracted { get; set; }
    public int? ExtractedChars { get; set; }
    public bool PolicyScanned { get; set; }
    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// dbo.Skills — a personal, reusable prompt snippet. Purely text: clicking one inserts its Body
/// into the compose box, nothing more. It never reaches the model on its own; the employee still
/// reviews and sends the message themselves.
/// </summary>
public class Skill
{
    public int SkillId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public int OwnerUserId { get; set; }
    public bool IsShared { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>dbo.ChatMessages — log ของทุกข้อความที่พนักงานถามและที่ AI ตอบ</summary>
public class ChatMessage
{
    public long MessageId { get; set; }
    public Guid SessionId { get; set; }
    public int UserId { get; set; }
    public string MessageRole { get; set; } = MessageRoles.User;
    public string Content { get; set; } = string.Empty;

    /// <summary>Question type — ดู <see cref="Services.QuestionClassifier"/></summary>
    public string? QuestionType { get; set; }

    /// <summary>Chat or Code — see <see cref="ChatModes"/>.</summary>
    public string? ChatMode { get; set; }

    public string? ModelName { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }

    /// <summary>token ที่เขียนลง prompt cache (คิดราคา 1.25x ของ input)</summary>
    public int? CacheWriteTokens { get; set; }

    /// <summary>Tokens read from the prompt cache (priced at 0.1x input).</summary>
    public int? CacheReadTokens { get; set; }

    /// <summary>Computed in the database — read-only, never written.</summary>
    public int TotalTokens { get; private set; }

    public decimal? InputCostUsd { get; set; }
    public decimal? OutputCostUsd { get; set; }
    public decimal? CacheCostUsd { get; set; }
    public decimal? TotalCostUsd { get; set; }
    public decimal? TotalCostThb { get; set; }

    /// <summary>เรตที่ใช้ตอนคำนวณ เก็บไว้ให้ตรวจย้อนหลังได้</summary>
    public decimal? UsdToThbRate { get; set; }

    /// <summary>แถวราคาที่ใช้คำนวณ</summary>
    public int? PricingId { get; set; }

    public int? LatencyMs { get; set; }
    public bool IsBlocked { get; set; }
    public string? PolicyFlag { get; set; }
    public int? PolicyRuleId { get; set; }
    public string? PolicyRuleName { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>จำนวนไฟล์แนบ — เก็บซ้ำไว้เพื่อกรอง "ข้อความที่มีไฟล์" ได้เร็ว</summary>
    public int AttachmentCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ChatSession? Session { get; set; }
    public AppUser? User { get; set; }
    public ICollection<ChatAttachment> Attachments { get; set; } = new List<ChatAttachment>();
}

/// <summary>ชนิดไฟล์แนบ — กำหนดว่าจะส่งให้ Claude เป็น content block แบบใด</summary>
public static class FileKinds
{
    /// <summary>รูปภาพ — ส่งเป็น image block</summary>
    public const string Image = "Image";

    /// <summary>PDF — ส่งเป็น document block แบบ base64</summary>
    public const string Pdf = "Pdf";

    /// <summary>Text file — content can be screened against policy and is sent as a plain-text document.</summary>
    public const string Text = "Text";

    /// <summary>
    /// Excel workbook (.xlsx/.xls) — no provider can read one, so the cells are converted to text
    /// server-side. That makes it behave like a text file from here on: screened by content, and
    /// sent to the model as a plain-text document.
    /// </summary>
    public const string Spreadsheet = "Spreadsheet";

    /// <summary>
    /// Kinds whose content is turned into text, and therefore screened against PolicyRules by
    /// content rather than by file name only.
    ///
    /// A helper rather than a comparison repeated at each call site: adding this kind touched
    /// six places, and the equivalent drift when a third AI provider was added left a validation
    /// attribute behind that silently rejected it.
    /// </summary>
    public static bool CarriesText(string? fileKind)
        => fileKind is Text or Spreadsheet;
}

/// <summary>
/// dbo.ChatAttachments — ไฟล์ที่พนักงานแนบมากับข้อความ
/// ตัวไฟล์อยู่บน disk ตารางนี้เก็บ metadata + SHA-256 ไว้ตรวจสอบย้อนหลัง
/// </summary>
public class ChatAttachment
{
    public long AttachmentId { get; set; }
    public long MessageId { get; set; }
    public Guid SessionId { get; set; }
    public int UserId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;

    /// <summary>ดู <see cref="FileKinds"/></summary>
    public string FileKind { get; set; } = FileKinds.Text;

    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>เส้นทางแบบ relative จาก Uploads:RootPath</summary>
    public string StoredPath { get; set; } = string.Empty;

    public bool IsTextExtracted { get; set; }
    public int? ExtractedChars { get; set; }

    /// <summary>false = only the file name was screened (PDF/image); the content was not read.</summary>
    public bool PolicyScanned { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ChatMessage? Message { get; set; }
}

/// <summary>
/// dbo.ModelPricing — per-million-token price for each model.
/// Used to compute the cost when a message is stored, then frozen onto that row.
/// </summary>
public class ModelPricing
{
    public int PricingId { get; set; }

    /// <summary>Which vendor answers this model — see <see cref="AiProviders"/>.</summary>
    public string Provider { get; set; } = AiProviders.Anthropic;

    public string ModelName { get; set; } = string.Empty;
    public decimal InputUsdPerMTok { get; set; }
    public decimal OutputUsdPerMTok { get; set; }
    public decimal CacheWriteUsdPerMTok { get; set; }
    public decimal CacheReadUsdPerMTok { get; set; }
    public decimal UsdToThbRate { get; set; } = 36.5m;
    public DateTime EffectiveFrom { get; set; } = DateTime.Now;
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// AI vendors the system can call. The value is stored in <c>ModelPricing.Provider</c> and a
/// CHECK constraint in the database allows only these names, so a typo fails at write time
/// instead of leaving a model that no provider claims at runtime.
///
/// Adding a vendor means: a class implementing <c>IAiProvider</c>, one DI line in Program.cs,
/// a constant here, and widening the CHECK constraint (see database/08_openai_provider.sql).
/// </summary>
public static class AiProviders
{
    public const string Anthropic = "Anthropic";
    public const string Google = "Google";
    public const string OpenAi = "OpenAI";

    public static readonly string[] All = [Anthropic, Google, OpenAi];

    /// <summary>Maps any casing onto the canonical value; anything unknown falls back to Anthropic.</summary>
    public static string Normalise(string? provider)
        => All.FirstOrDefault(p => string.Equals(p, provider?.Trim(), StringComparison.OrdinalIgnoreCase))
           ?? Anthropic;

    public static bool IsKnown(string? provider)
        => !string.IsNullOrWhiteSpace(provider)
           && All.Contains(provider.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// ชนิดแหล่งข้อมูลภายนอกที่ทะเบียนรู้จัก — ผูกกับ CHECK constraint ของ DataSources.SourceType
/// เพิ่มชนิดใหม่ต้องแก้ทั้งสองที่ (บทเรียนจาก AiProviders: enum-like ต้องไล่ทุกสำเนาของรายการเดิม)
/// </summary>
public static class DataSourceTypes
{
    public const string Api = "Api";
    public const string DataLake = "DataLake";
    public const string LocalFolder = "LocalFolder";
    public const string SharePoint = "SharePoint";

    public static readonly string[] All = [Api, DataLake, LocalFolder, SharePoint];

    public static bool IsKnown(string? type)
        => !string.IsNullOrWhiteSpace(type) && All.Contains(type.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>ขอบเขตสิทธิ์ของ DataSourceGrant — ผูกกับ CHECK constraint ของ ScopeType</summary>
public static class DataSourceScopeTypes
{
    /// <summary>อ่านได้ทั้งหมดที่แหล่งข้อมูลนั้นมี</summary>
    public const string Full = "Full";

    /// <summary>จำกัดตาม Users.Department ของพนักงานคนนั้นเองโดยอัตโนมัติ</summary>
    public const string OwnDepartment = "OwnDepartment";

    /// <summary>แอดมินกำหนดเงื่อนไขเอง (endpoint/field ที่อ่านได้ ฯลฯ)</summary>
    public const string Custom = "Custom";

    public static readonly string[] All = [Full, OwnDepartment, Custom];

    public static bool IsKnown(string? type)
        => !string.IsNullOrWhiteSpace(type) && All.Contains(type.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// dbo.DataSources — ทะเบียนแหล่งข้อมูลภายนอกที่แอดมิน/IT ตั้งค่าไว้
///
/// Phase 2: ผูกกับแชทได้แล้ว — ผู้ใช้ที่มี DataSourceGrant ที่ยัง active เลือกแหล่งข้อมูลนี้
/// ตอนเริ่มบทสนทนาใหม่ได้ (ดู ChatSession.DataSourceId และ ChatSessionDataFetch)
/// </summary>
public class DataSource
{
    public int SourceId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = DataSourceTypes.Api;
    public string? Description { get; set; }

    /// <summary>ค่าที่ไม่ใช่ความลับ (URL, path, tenant id) เก็บเป็น JSON ดิบ รูปแบบต่างกันตาม SourceType</summary>
    public string? ConfigJson { get; set; }

    /// <summary>เข้ารหัสด้วย Data Protection ก่อนเก็บเสมอ — ไม่เคยเป็น plain text ในคอลัมน์นี้</summary>
    public string? EncryptedSecret { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>dbo.DataSourceGrants — สิทธิ์การเข้าถึง DataSource ของพนักงานแต่ละคน</summary>
public class DataSourceGrant
{
    public long GrantId { get; set; }
    public int SourceId { get; set; }
    public int UserId { get; set; }

    public string ScopeType { get; set; } = DataSourceScopeTypes.Full;

    /// <summary>ใช้เมื่อ ScopeType = Custom เท่านั้น — ข้อความอิสระที่แอดมินพิมพ์เอง</summary>
    public string? ScopeFilter { get; set; }
    public string? Notes { get; set; }

    public DateTime GrantedAt { get; set; } = DateTime.Now;
    public string? GrantedBy { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
}

/// <summary>dbo.AuditLogs</summary>
public class AuditLog
{
    public long AuditId { get; set; }
    public int? UserId { get; set; }
    public string? Username { get; set; }
    public string Category { get; set; } = AuditCategories.Auth;
    public string Action { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public bool IsSuccess { get; set; } = true;
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
