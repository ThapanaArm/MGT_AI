using PdfSharp.Fonts;

namespace MgtAiAuthen.Api.Services.Reporting;

public interface IReportFontProvider
{
    IFontResolver Resolver { get; }
    string FamilyName { get; }
    string MonoFamilyName { get; }
}

/// <summary>
/// Supplies the PDF writer with a font that actually has Thai glyphs.
///
/// PDFsharp embeds whatever bytes a resolver hands it and does not look at installed fonts, so
/// without this every Thai character in an exported PDF comes out blank. Sarabun is bundled in
/// the repository under the SIL Open Font License, which permits embedding in documents — the
/// reason for shipping it rather than reaching for a Windows font, whose licence does not
/// clearly cover redistributing it inside files the company sends out. It is also the same
/// typeface the web UI uses, so the export looks like the app.
/// </summary>
public class ReportFontProvider : IReportFontProvider
{
    public const string Family = "Sarabun";
    public const string MonoFamily = "SarabunMono";

    private readonly SarabunResolver _resolver;

    public ReportFontProvider(IHostEnvironment environment, ILogger<ReportFontProvider> logger)
    {
        string directory = Path.Combine(environment.ContentRootPath, "Assets", "fonts");
        _resolver = new SarabunResolver(directory, logger);
    }

    public IFontResolver Resolver => _resolver;
    public string FamilyName => Family;

    /// <summary>
    /// Code blocks map to the same face rather than a monospaced one: a mono font carrying Thai
    /// is not something we can assume, and a fallback that drops Thai glyphs in an exported
    /// report is worse than code that is not monospaced.
    /// </summary>
    public string MonoFamilyName => Family;

    private sealed class SarabunResolver(string directory, ILogger logger) : IFontResolver
    {
        private const string RegularKey = "sarabun#regular";
        private const string BoldKey = "sarabun#bold";

        private readonly Dictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
            => new(isBold ? BoldKey : RegularKey);

        public byte[]? GetFont(string faceName)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(faceName, out byte[]? cached)) return cached;

                string file = faceName == BoldKey ? "Sarabun-Bold.ttf" : "Sarabun-Regular.ttf";
                string path = Path.Combine(directory, file);

                if (!File.Exists(path))
                {
                    // Without the font PDF export cannot produce readable Thai, so say exactly
                    // which file is missing instead of silently emitting blank glyphs.
                    logger.LogError(
                        "Report font {File} is missing from {Directory} — PDF export will fail. " +
                        "Restore it from the repository (Assets/fonts).", file, directory);

                    throw new Infrastructure.AppException(
                        "PDF export is unavailable because the report font is missing on the server. " +
                        "Please ask an administrator to restore Assets/fonts. Excel, Word and " +
                        "PowerPoint export still work.",
                        System.Net.HttpStatusCode.ServiceUnavailable, "REPORT_FONT_MISSING");
                }

                byte[] bytes = File.ReadAllBytes(path);
                _cache[faceName] = bytes;
                return bytes;
            }
        }
    }
}
