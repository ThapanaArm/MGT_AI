using System.Net;
using MgtAiAuthen.Api.Contracts;
using Microsoft.AspNetCore.Diagnostics;

namespace MgtAiAuthen.Api.Infrastructure;

/// <summary>
/// ข้อผิดพลาดที่คาดไว้ล่วงหน้าและอยากส่งข้อความให้ผู้ใช้เห็นตรง ๆ
/// (ต่างจาก exception ที่ไม่คาดคิด ซึ่งจะถูกกลบเป็นข้อความกลางเพื่อไม่รั่วรายละเอียดระบบ)
/// </summary>
public class AppException(string message, HttpStatusCode statusCode = HttpStatusCode.BadRequest, string? code = null)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Code { get; } = code;

    public static AppException Unauthorized(string message) => new(message, HttpStatusCode.Unauthorized);
    public static AppException Forbidden(string message) => new(message, HttpStatusCode.Forbidden);
    public static AppException NotFound(string message) => new(message, HttpStatusCode.NotFound);
    public static AppException Conflict(string message) => new(message, HttpStatusCode.Conflict);
}

/// <summary>
/// ตรวจว่าชื่อผู้ให้บริการ AI เป็นค่าที่ระบบเรียกได้จริง (ว่างได้ = ไม่เปลี่ยน/ใช้ค่าตั้งต้น)
///
/// อ่านรายชื่อจาก <see cref="Data.AiProviders.All"/> ตรง ๆ แทนการฮาร์ดโค้ดใน RegularExpression
/// เพราะตอนเพิ่ม OpenAI เข้ามา รายชื่อในโค้ดกับในฐานข้อมูลถูกอัปเดตแต่ attribute ถูกลืม
/// ทำให้แก้ราคาโมเดล OpenAI ไม่ได้เลยโดยขึ้น error ว่า "must be Anthropic or Google"
/// ผูกกับแหล่งเดียวแล้วผู้ให้บริการรายที่สี่จะไม่ต้องแก้ไฟล์นี้อีก
/// </summary>
public class AiProviderAttribute : System.ComponentModel.DataAnnotations.ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if (value is null or "")
        {
            return true;
        }

        return value is string text && Data.AiProviders.IsKnown(text);
    }

    public override string FormatErrorMessage(string name)
        => $"Provider must be one of: {string.Join(", ", Data.AiProviders.All)}";
}

/// <summary>แปลง exception ทุกชนิดให้เป็น <see cref="ApiError"/> รูปแบบเดียว</summary>
public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        (HttpStatusCode status, string message, string? code) = exception switch
        {
            AppException app => (app.StatusCode, app.Message, app.Code),
            // Provider-neutral codes: the same four categories are raised whether Anthropic or
            // Google answered, and the message already names the vendor where it matters.
            Services.AiUnavailableException => (HttpStatusCode.ServiceUnavailable, exception.Message, "AI_NOT_CONFIGURED"),
            Services.AiOverloadedException => (HttpStatusCode.ServiceUnavailable, exception.Message, "AI_OVERLOADED"),
            Services.AiInvalidInputException => (HttpStatusCode.BadRequest, exception.Message, "AI_INVALID_INPUT"),
            Services.AiBillingException => (HttpStatusCode.ServiceUnavailable, exception.Message, "AI_BILLING"),
            OperationCanceledException => ((HttpStatusCode)499, "The request was cancelled", "CANCELLED"),
            _ => (HttpStatusCode.InternalServerError, "An internal error occurred. Please try again or contact your system administrator.", "INTERNAL_ERROR"),
        };

        if (status == HttpStatusCode.InternalServerError)
        {
            logger.LogError(exception, "Failed to handle request {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogInformation("{Method} {Path} -> {Status}: {Message}",
                httpContext.Request.Method, httpContext.Request.Path, (int)status, message);
        }

        httpContext.Response.StatusCode = (int)status;
        await httpContext.Response.WriteAsJsonAsync(new ApiError(message, code), cancellationToken);
        return true;
    }
}
