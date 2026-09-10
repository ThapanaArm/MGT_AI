using MgtAiAuthen.Api.Contracts;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Options;
using MgtAiAuthen.Api.Security;
using MgtAiAuthen.Api.Services;
using MgtAiAuthen.Api.Services.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Controllers;

/// <summary>
/// แชทกับ Claude — ทุก endpoint ต้องล็อกอิน และทุกข้อความถูกบันทึกผูกกับ UserID ของผู้ส่ง
/// </summary>
[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController(
    IChatService chat,
    IAiClient ai,
    IReportService reports,
    IOptions<UploadOptions> uploadOptions) : ControllerBase
{
    private readonly UploadOptions _uploads = uploadOptions.Value;

    /// <summary>
    /// ตรวจว่าฝั่งเซิร์ฟเวอร์ตั้งค่า API key ของผู้ให้บริการ AI แล้วหรือยัง
    ///
    /// AiReady = มีอย่างน้อยหนึ่งผู้ให้บริการที่ใช้ได้ (หน้าแชทยังทำงานได้) ส่วน Providers
    /// บอกทีละราย เพราะตั้งค่า Claude ไว้แต่ไม่ได้ตั้ง Gemini เป็นสถานะที่ใช้งานได้จริง
    /// ไม่ใช่ความผิดพลาด
    /// </summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        AiReady = ai.IsConfigured,
        Model = ai.DefaultModel,
        Providers = ai.Providers,
        Message = BuildStatusMessage(),
    });

    private string BuildStatusMessage()
    {
        if (!ai.IsConfigured)
        {
            return "The administrator has not configured an API key for any AI provider";
        }

        string[] missing = ai.Providers.Where(p => !p.Ready).Select(p => p.Provider).ToArray();

        return missing.Length == 0
            ? "Ready"
            : $"Ready — but no API key is configured for {string.Join(", ", missing)}, " +
              "so models from that provider cannot be used";
    }

    /// <summary>ข้อจำกัดการแนบไฟล์ — frontend ใช้ตรวจก่อนอัพโหลด</summary>
    [HttpGet("upload-limits")]
    [ProducesResponseType(typeof(UploadLimitsDto), StatusCodes.Status200OK)]
    public ActionResult<UploadLimitsDto> UploadLimits() => Ok(new UploadLimitsDto(
        _uploads.MaxFileMb,
        _uploads.MaxFilesPerMessage,
        _uploads.MaxTotalMbPerMessage,
        _uploads.EffectiveExtensions,
        // Only these types have their content read and screened against the policy rules.
        [".txt", ".csv", ".md", ".json", ".log", ".xlsx", ".xls"]));

    /// <summary>
    /// Models the signed-in user may pick from — every model with active pricing, cheapest first.
    /// Admins control the list from the model pricing page.
    /// </summary>
    [HttpGet("models")]
    [ProducesResponseType(typeof(IReadOnlyList<AvailableModelDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AvailableModelDto>>> Models(CancellationToken ct)
        => Ok(await chat.GetAvailableModelsAsync(ct));

    /// <summary>รายการบทสนทนาของตัวเอง (ใหม่สุดขึ้นก่อน)</summary>
    [HttpGet("sessions")]
    [ProducesResponseType(typeof(IReadOnlyList<ChatSessionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ChatSessionDto>>> GetSessions(CancellationToken ct)
        => Ok(await chat.GetSessionsAsync(User.GetUserId(), ct));

    /// <summary>ข้อความทั้งหมดในบทสนทนา — Admin/Auditor เปิดดูของพนักงานคนอื่นได้</summary>
    [HttpGet("sessions/{sessionId:guid}/messages")]
    [ProducesResponseType(typeof(IReadOnlyList<ChatMessageDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ChatMessageDto>>> GetMessages(
        Guid sessionId, CancellationToken ct)
        => Ok(await chat.GetMessagesAsync(sessionId, User.GetUserId(), User.CanReadAllLogs(), ct));

    /// <summary>
    /// ส่งข้อความ (ไม่มีไฟล์แนบ) — ระบบจะจัดหมวดคำถาม, คัดกรองตาม PolicyRules, บันทึก log
    /// แล้วจึงเรียก Claude (ถ้าไม่ถูกระงับ)
    /// </summary>
    [HttpPost("messages")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(ChatSendResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ChatSendResponse>> Send(ChatSendRequest request, CancellationToken ct)
        => Ok(await chat.SendAsync(User.GetUserId(), request, files: null, ct));

    /// <summary>
    /// ส่งข้อความพร้อมไฟล์แนบ (multipart/form-data)
    ///
    /// ฟิลด์: <c>message</c> (ว่างได้ถ้ามีไฟล์), <c>sessionId</c> (ว่าง = เริ่มบทสนทนาใหม่),
    /// <c>files</c> (แนบได้หลายไฟล์)
    ///
    /// แยกเป็นอีก action เพราะ ASP.NET ผูก [Consumes] ต่อ action — ทำให้ endpoint JSON เดิม
    /// ยังใช้ได้เหมือนเดิมโดยไม่ต้องแก้ client ที่มีอยู่
    /// </summary>
    [HttpPost("messages")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(64L * 1024 * 1024)]
    [ProducesResponseType(typeof(ChatSendResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ChatSendResponse>> SendWithFiles(
        [FromForm] string? message,
        [FromForm] Guid? sessionId,
        [FromForm] string? model,
        [FromForm] string? mode,
        [FromForm] int? projectId,
        [FromForm] IFormFileCollection? files,
        CancellationToken ct)
    {
        var request = new ChatSendRequest
        {
            Message = message ?? string.Empty,
            SessionId = sessionId,
            Model = model,
            Mode = mode,
            ProjectId = projectId,
        };
        IReadOnlyList<IFormFile> uploaded = files ?? (IReadOnlyList<IFormFile>)[];

        return Ok(await chat.SendAsync(User.GetUserId(), request, uploaded, ct));
    }

    /// <summary>
    /// ดาวน์โหลดไฟล์แนบ — เจ้าของ หรือ Admin/Auditor เพื่อการตรวจสอบ
    /// การเปิดไฟล์ถูกบันทึกลง audit log ทุกครั้ง
    /// </summary>
    [HttpGet("attachments/{attachmentId:long}")]
    public async Task<IActionResult> DownloadAttachment(long attachmentId, CancellationToken ct)
    {
        (ChatAttachment meta, byte[] content) = await chat.GetAttachmentAsync(
            attachmentId, User.GetUserId(), User.CanReadAllLogs(), ct);

        return File(content, meta.ContentType, meta.FileName);
    }

    /// <summary>
    /// ส่งออกคำตอบของ AI เป็นรายงาน — xlsx | pdf | docx | pptx
    ///
    /// เจ้าของบทสนทนา หรือ Admin/Auditor เท่านั้น และการส่งออกทุกครั้งถูกบันทึกลง audit log
    /// (ไฟล์รายงานออกจากระบบไปได้ จึงต้องรู้ว่าใครเอาอะไรออกไปเมื่อไหร่)
    /// </summary>
    [HttpGet("messages/{messageId:long}/export/{format}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ExportReport(long messageId, string format, CancellationToken ct)
    {
        ReportFile file = await reports.ExportMessageAsync(
            messageId, format, User.GetUserId(), User.CanReadAllLogs(), ct);

        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>รูปแบบไฟล์ที่ส่งออกได้ — frontend ใช้สร้างปุ่ม</summary>
    [HttpGet("export-formats")]
    public IActionResult ExportFormats() => Ok(ReportFormats.All);

    /// <summary>Hides one of your own conversations — messages stay in the database for auditing.</summary>
    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> DeleteSession(Guid sessionId, CancellationToken ct)
    {
        await chat.DeleteSessionAsync(sessionId, User.GetUserId(), ct);
        return NoContent();
    }
}
