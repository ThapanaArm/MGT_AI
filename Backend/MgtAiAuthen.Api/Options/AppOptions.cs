namespace MgtAiAuthen.Api.Options;

/// <summary>Settings for issuing and validating JWTs (bound to the "Jwt" section).</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "MgtAiAuthen";
    public string Audience { get; set; } = "MgtAiAuthenClient";

    /// <summary>คีย์ลับสำหรับเซ็น token — ต้องยาวอย่างน้อย 32 characters</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 7;
}

/// <summary>ค่าตั้งสำหรับเรียก Claude API (ผูกกับ section "Claude")</summary>
public class ClaudeOptions
{
    public const string SectionName = "Claude";

    /// <summary>Read from appsettings or the ANTHROPIC_API_KEY environment variable.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "claude-sonnet-5";

    public int MaxTokens { get; set; } = 16000;

    /// <summary>
    /// How hard the model thinks: low | medium | high | max (blank = the model default, high).
    /// งานแชทโต้ตอบสดควรใช้ low หรือ medium เพราะ high/max ทำให้รอนานหลายสิบวินาที
    /// ส่วนงานที่ต้องความแม่นยำสูงจึงค่อยขยับขึ้น
    /// </summary>
    public string Effort { get; set; } = "medium";

    /// <summary>จำนวนข้อความย้อนหลัง (user+assistant) ที่ส่งไปเป็นบริบทของบทสนทนา</summary>
    public int HistoryTurns { get; set; } = 20;

    /// <summary>System prompt for the general Chat mode.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// System prompt for Code mode. Blank falls back to <see cref="SystemPrompt"/>, so the
    /// feature degrades to plain chat rather than breaking if it is not configured.
    /// </summary>
    public string CodeSystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// How to write something meant to be exported as a report, appended like
    /// <see cref="ChartSystemPrompt"/>.
    ///
    /// Needed because the general prompt caps answers at six lines to keep chat fast — a report
    /// asked for on purpose has to be allowed to break that, or the export produces a six-line
    /// document.
    /// </summary>
    public string ReportSystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// How to emit a chart, appended to whichever mode prompt is in use.
    ///
    /// Kept as its own setting rather than pasted into both prompts: the schema has to match
    /// what the frontend parses, and two copies of it would drift the moment one is edited.
    /// Blank disables charting without touching the other prompts.
    /// </summary>
    public string ChartSystemPrompt { get; set; } = string.Empty;
}

/// <summary>
/// ค่าตั้งสำหรับเรียก Google Gemini API (ผูกกับ section "Gemini")
///
/// System prompt, โหมด Code และจำนวนข้อความย้อนหลัง **ใช้ร่วมกันทุกผู้ให้บริการ** และยังอยู่
/// ใน section "Claude" ที่เดียว จะได้ไม่ต้องแก้ prompt สองที่แล้วลืมที่หนึ่ง
/// </summary>
public class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>Read from appsettings or the GEMINI_API_KEY environment variable.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>โมเดลที่ใช้เมื่อผู้เรียกไม่ได้ระบุ — ต้องมีราคาในตาราง ModelPricing ด้วย</summary>
    public string Model { get; set; } = "gemini-3.8-flash";

    public int MaxTokens { get; set; } = 16000;

    /// <summary>
    /// ระดับการคิดของโมเดล: low | high (เว้นว่าง = ใช้ค่าตั้งต้นของโมเดล)
    ///
    /// พารามิเตอร์นี้ตระกูล Gemini 3 รับ แต่ 2.5 ไม่รับ (ตอบ 400) จึงส่งแบบเรียนรู้เอง
    /// เหมือน effort ของ Anthropic — ถูกปฏิเสธครั้งแรกแล้วจำไว้ ไม่ต้องฮาร์ดโค้ดว่ารุ่นไหนรับ
    /// </summary>
    public string ThinkingLevel { get; set; } = "low";

    /// <summary>
    /// Base URL ของ Gemini API — แยกเป็นค่าตั้งเพื่อให้ชี้ไป proxy ภายในองค์กรได้
    /// โดยไม่ต้องแก้โค้ด
    /// </summary>
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>วินาทีที่รอคำตอบก่อนถือว่า timeout</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// ค่าตั้งสำหรับเรียก OpenAI (ChatGPT) ผูกกับ section "OpenAI"
///
/// ใช้ Responses API (/v1/responses) ไม่ใช่ Chat Completions เพราะเป็นพื้นผิวที่ OpenAI
/// แนะนำสำหรับของใหม่ และรองรับทั้งโมเดลที่มี reasoning กับไฟล์แนบ (input_file) ในที่เดียว
/// </summary>
public class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>Read from appsettings or the OPENAI_API_KEY environment variable.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>โมเดลที่ใช้เมื่อผู้เรียกไม่ได้ระบุ — ต้องมีราคาในตาราง ModelPricing ด้วย</summary>
    public string Model { get; set; } = "gpt-5.6-luna";

    public int MaxTokens { get; set; } = 16000;

    /// <summary>
    /// ระดับการคิดของโมเดล: minimal | low | medium | high (เว้นว่าง = ไม่ส่งไปเลย)
    ///
    /// ตระกูล gpt-5 รับพารามิเตอร์นี้ แต่รุ่นเก่าอย่าง gpt-4.1-mini ตอบ 400
    /// unsupported_parameter จึงส่งแบบเรียนรู้เอง เหมือน effort ของ Anthropic
    /// และ thinkingLevel ของ Gemini
    /// </summary>
    public string ReasoningEffort { get; set; } = "low";

    /// <summary>แยกเป็นค่าตั้งเพื่อให้ชี้ไป Azure OpenAI หรือ proxy ภายในองค์กรได้</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>วินาทีที่รอคำตอบก่อนถือว่า timeout</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>นโยบายรหัสผ่านและการล็อกบัญชี (ผูกกับ section "Security")</summary>
public class SecurityOptions
{
    public const string SectionName = "Security";

    public int MaxFailedLoginAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int MinPasswordLength { get; set; } = 8;
}

/// <summary>ค่าตั้งสำหรับไฟล์แนบในการแชท (ผูกกับ section "Uploads")</summary>
public class UploadOptions
{
    public const string SectionName = "Uploads";

    /// <summary>โฟลเดอร์เก็บไฟล์ — เส้นทางแบบ relative จะนับจาก content root ของแอป</summary>
    public string RootPath { get; set; } = "App_Data/uploads";

    public int MaxFileMb { get; set; } = 15;

    public int MaxFilesPerMessage { get; set; } = 5;

    /// <summary>รวมทุกไฟล์ในหนึ่งข้อความต้องไม่เกินค่านี้ (Claude จำกัด request ที่ 32MB)</summary>
    public int MaxTotalMbPerMessage { get; set; } = 25;

    /// <summary>ค่าตั้งต้นเมื่อ appsettings ไม่ได้ระบุ — อย่าใช้เป็นค่าเริ่มต้นของ property ตรง ๆ</summary>
    private static readonly string[] DefaultExtensions =
    [
        ".png", ".jpg", ".jpeg", ".webp", ".gif",
        ".pdf",
        ".txt", ".csv", ".md", ".json", ".log",
        ".xlsx", ".xls",
    ];

    /// <summary>
    /// นามสกุลที่อนุญาต (ต้องขึ้นต้นด้วยจุด) — นอกรายการนี้ถูกปฏิเสธ
    ///
    /// ค่าเริ่มต้นต้องเป็น array ว่าง เพราะ ConfigurationBinder ของ .NET จะ **ต่อท้าย**
    /// ค่าจาก appsettings เข้ากับค่าที่มีอยู่เดิม ไม่ได้แทนที่ — ถ้าใส่ค่าตั้งต้นไว้ที่นี่
    /// the list ends up duplicated and, worse, removing an extension from appsettings has no effect
    /// เพราะค่าตั้งต้นยังอนุญาตอยู่ (ช่องโหว่เงียบด้านความปลอดภัย)
    /// </summary>
    public string[] AllowedExtensions { get; set; } = [];

    /// <summary>รายการที่ใช้จริง — ตกไปใช้ค่าตั้งต้นเมื่อ appsettings ไม่ได้ระบุไว้</summary>
    public string[] EffectiveExtensions => AllowedExtensions.Length > 0
        ? AllowedExtensions
        : DefaultExtensions;

    /// <summary>ตัดเนื้อหาไฟล์ข้อความที่ยาวเกินนี้ (characters) ก่อนส่งให้ AI</summary>
    public int MaxTextChars { get; set; } = 200_000;

    /// <summary>
    /// จำนวนแถวสูงสุดที่อ่านต่อชีต
    ///
    /// .xlsx เป็นไฟล์ zip ที่ขยายตัวได้มาก — ไฟล์ 2 MB อาจกลายเป็นข้อความหลายสิบล้านตัวอักษร
    /// เพดานนี้กันทั้งเรื่องหน่วยความจำและค่า token ที่จะบานปลาย (MaxTextChars กันอีกชั้น)
    /// </summary>
    public int MaxSpreadsheetRows { get; set; } = 2000;

    public long MaxFileBytes => (long)MaxFileMb * 1024 * 1024;
    public long MaxTotalBytesPerMessage => (long)MaxTotalMbPerMessage * 1024 * 1024;
}

/// <summary>ผู้ใช้ตั้งต้นที่จะถูกสร้างให้ตอนแอปเริ่มทำงาน (ผูกกับ section "SeedUsers")</summary>
public class SeedUserOptions
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Department { get; set; }
    public string UserRole { get; set; } = "User";
}
