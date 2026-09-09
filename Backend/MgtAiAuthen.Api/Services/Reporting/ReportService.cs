using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MgtAiAuthen.Api.Data;
using MgtAiAuthen.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using A = DocumentFormat.OpenXml.Drawing;

namespace MgtAiAuthen.Api.Services.Reporting;

/// <summary>One output format. Adding a format is a new class plus one DI line.</summary>
public interface IReportWriter
{
    /// <summary>Must match a value from <see cref="ReportFormats"/>.</summary>
    string Format { get; }

    byte[] Write(ReportDocument report);
}

/// <summary>The generated file plus the name it should download as.</summary>
public record ReportFile(string FileName, string ContentType, byte[] Content);

public interface IReportService
{
    /// <summary>
    /// Builds a report from one assistant message. The caller's own id and whether they may read
    /// everyone's logs decide access, the same rule attachments use.
    /// </summary>
    Task<ReportFile> ExportMessageAsync(
        long messageId, string format, int requestedByUserId, bool canReadAll,
        CancellationToken ct = default);
}

public class ReportService(
    AppDbContext db,
    IEnumerable<IReportWriter> writers,
    IAuditService audit,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ReportService> logger) : IReportService
{
    public async Task<ReportFile> ExportMessageAsync(
        long messageId, string format, int requestedByUserId, bool canReadAll,
        CancellationToken ct = default)
    {
        if (!ReportFormats.IsKnown(format))
        {
            throw new AppException(
                $"\"{format}\" is not an export format. Choose one of: {string.Join(", ", ReportFormats.All)}");
        }

        string wanted = ReportFormats.Normalise(format);

        IReportWriter writer = writers.FirstOrDefault(w =>
            string.Equals(w.Format, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppException($"No writer is registered for \"{wanted}\"");

        ChatMessage message = await db.ChatMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MessageId == messageId, ct)
            ?? throw AppException.NotFound("Message not found");

        // An employee exports their own conversations; auditors and admins export anyone's.
        if (message.UserId != requestedByUserId && !canReadAll)
        {
            throw AppException.Forbidden("You may only export your own conversations");
        }

        if (message.MessageRole != MessageRoles.Assistant)
        {
            throw new AppException(
                "Only an AI answer can be exported as a report — pick the answer, not the question.");
        }

        if (message.IsBlocked)
        {
            // A blocked message never reached the AI, so there is nothing to report on, and
            // exporting one would put the blocked content into a shareable file.
            throw new AppException("This message was blocked by policy and cannot be exported.");
        }

        AppUser owner = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == message.UserId, ct)
            ?? throw AppException.NotFound("The employee who owns this message no longer exists");

        // The question gives the report its title; without it a reader sees an answer to nothing.
        string question = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == message.SessionId
                        && m.MessageRole == MessageRoles.User
                        && m.MessageId < message.MessageId)
            .OrderByDescending(m => m.MessageId)
            .Select(m => m.Content)
            .FirstOrDefaultAsync(ct) ?? "(no question recorded)";

        var report = new ReportDocument(
            Title: BuildTitle(question),
            AuthorName: owner.FullName,
            Department: owner.Department,
            ModelName: message.ModelName,
            GeneratedAt: DateTime.Now,
            MessageAt: message.CreatedAt,
            Question: question.Trim(),
            Blocks: ReportContentParser.Parse(message.Content));

        byte[] content = writer.Write(report);

        string fileName = $"report-{message.MessageId}-{DateTime.Now:yyyyMMdd-HHmm}.{wanted}";

        await audit.LogAsync(AuditCategories.Chat, AuditActions.ReportExported,
            requestedByUserId, httpContextAccessor.HttpContext?.User.Identity?.Name,
            $"Exported message {message.MessageId} of {owner.Username} as {wanted.ToUpperInvariant()} " +
            $"({content.Length / 1024.0:F1} KB, {report.Blocks.Count} block(s))",
            isSuccess: true, ct);

        logger.LogInformation(
            "Exported message {MessageId} as {Format} ({Bytes} bytes) for user {UserId}",
            messageId, wanted, content.Length, requestedByUserId);

        return new ReportFile(fileName, ReportFormats.ContentType(wanted), content);
    }

    /// <summary>The question, trimmed to something that fits a title line.</summary>
    private static string BuildTitle(string question)
    {
        string clean = question.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (clean.Length == 0) return "AI report";
        return clean.Length <= 90 ? clean : clean[..89].TrimEnd() + "…";
    }
}

/// <summary>
/// The slide master, layout and theme a .pptx needs before PowerPoint will open it.
///
/// Split out of the writer because none of it is about the report — it is the minimum package
/// structure the format demands, and a deck without it is reported as corrupt.
/// </summary>
public static class PowerPointScaffold
{
    public static SlideMasterPart Build(PresentationPart presentationPart)
    {
        SlideMasterPart masterPart = presentationPart.AddNewPart<SlideMasterPart>();
        SlideLayoutPart layoutPart = masterPart.AddNewPart<SlideLayoutPart>();

        layoutPart.SlideLayout = new SlideLayout(
            new CommonSlideData(new ShapeTree(
                new NonVisualGroupShapeProperties(
                    new NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                    new NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new A.TransformGroup()))),
            new ColorMapOverride(new A.MasterColorMapping()))
        {
            Type = SlideLayoutValues.Blank,
        };

        masterPart.SlideMaster = new SlideMaster(
            new CommonSlideData(new ShapeTree(
                new NonVisualGroupShapeProperties(
                    new NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                    new NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new A.TransformGroup()))),
            new ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
            },
            new SlideLayoutIdList(new SlideLayoutId
            {
                Id = 2147483649U, RelationshipId = masterPart.GetIdOfPart(layoutPart),
            }));

        ThemePart themePart = masterPart.AddNewPart<ThemePart>();
        themePart.Theme = BuildTheme();

        return masterPart;
    }

    private static A.Theme BuildTheme()
    {
        A.SchemeColor Scheme(string hex) => new(new A.RgbColorModelHex { Val = hex });

        return new A.Theme(
            new A.ThemeElements(
                new A.ColorScheme(
                    new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText }),
                    new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window }),
                    new A.Dark2Color(new A.RgbColorModelHex { Val = "1E293B" }),
                    new A.Light2Color(new A.RgbColorModelHex { Val = "F8FAFC" }),
                    new A.Accent1Color(new A.RgbColorModelHex { Val = "0F766E" }),
                    new A.Accent2Color(new A.RgbColorModelHex { Val = "2A78D6" }),
                    new A.Accent3Color(new A.RgbColorModelHex { Val = "EB6834" }),
                    new A.Accent4Color(new A.RgbColorModelHex { Val = "1BAF7A" }),
                    new A.Accent5Color(new A.RgbColorModelHex { Val = "EDA100" }),
                    new A.Accent6Color(new A.RgbColorModelHex { Val = "E87BA4" }),
                    new A.Hyperlink(new A.RgbColorModelHex { Val = "1D4ED8" }),
                    new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "6B7280" }))
                { Name = "Office" },
                new A.FontScheme(
                    new A.MajorFont(
                        new A.LatinFont { Typeface = "Sarabun" },
                        new A.EastAsianFont { Typeface = "Sarabun" },
                        new A.ComplexScriptFont { Typeface = "Sarabun" }),
                    new A.MinorFont(
                        new A.LatinFont { Typeface = "Sarabun" },
                        new A.EastAsianFont { Typeface = "Sarabun" },
                        new A.ComplexScriptFont { Typeface = "Sarabun" }))
                { Name = "Office" },
                new A.FormatScheme(
                    new A.FillStyleList(
                        new A.SolidFill(Scheme("FFFFFF")),
                        new A.SolidFill(Scheme("FFFFFF")),
                        new A.SolidFill(Scheme("FFFFFF"))),
                    new A.LineStyleList(
                        new A.Outline(new A.SolidFill(Scheme("E2E8F0"))) { Width = 9525 },
                        new A.Outline(new A.SolidFill(Scheme("E2E8F0"))) { Width = 9525 },
                        new A.Outline(new A.SolidFill(Scheme("E2E8F0"))) { Width = 9525 }),
                    new A.EffectStyleList(
                        new A.EffectStyle(new A.EffectList()),
                        new A.EffectStyle(new A.EffectList()),
                        new A.EffectStyle(new A.EffectList())),
                    new A.BackgroundFillStyleList(
                        new A.SolidFill(Scheme("FFFFFF")),
                        new A.SolidFill(Scheme("FFFFFF")),
                        new A.SolidFill(Scheme("FFFFFF"))))
                { Name = "Office" }))
        { Name = "MgtAiAuthen" };
    }
}
