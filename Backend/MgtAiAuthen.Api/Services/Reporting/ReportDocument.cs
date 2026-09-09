using System.Text;
using System.Text.Json;

namespace MgtAiAuthen.Api.Services.Reporting;

/// <summary>Formats a report can be exported as.</summary>
public static class ReportFormats
{
    public const string Excel = "xlsx";
    public const string Pdf = "pdf";
    public const string Word = "docx";
    public const string PowerPoint = "pptx";

    public static readonly string[] All = [Excel, Pdf, Word, PowerPoint];

    public static bool IsKnown(string? format)
        => !string.IsNullOrWhiteSpace(format)
           && All.Contains(format.Trim().ToLowerInvariant());

    public static string Normalise(string? format)
        => All.FirstOrDefault(f => string.Equals(f, format?.Trim(), StringComparison.OrdinalIgnoreCase))
           ?? Excel;

    public static string ContentType(string format) => format switch
    {
        Excel => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        Word => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        PowerPoint => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/pdf",
    };
}

/// <summary>One piece of a report. The four writers each render every kind.</summary>
public abstract record ReportBlock;

public record HeadingBlock(int Level, string Text) : ReportBlock;

public record ParagraphBlock(string Text) : ReportBlock;

public record BulletsBlock(IReadOnlyList<string> Items) : ReportBlock;

/// <summary>
/// A table. <paramref name="Caption"/> is set when the table came from a chart, so the reader
/// knows the numbers were a picture in the chat.
/// </summary>
public record TableBlock(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string? Caption = null,
    bool NumericColumns = true) : ReportBlock;

public record CodeBlock(string Language, string Text) : ReportBlock;

/// <summary>The whole report, ready for any of the writers.</summary>
public record ReportDocument(
    string Title,
    string AuthorName,
    string? Department,
    string? ModelName,
    DateTime GeneratedAt,
    DateTime MessageAt,
    string Question,
    IReadOnlyList<ReportBlock> Blocks);

/// <summary>
/// Turns an assistant message into report blocks.
///
/// Deliberately the same reading of the text the chat UI does — fenced blocks, simple headings,
/// bullet lines and pipe tables — so the export matches what the employee saw on screen. It is
/// not a full Markdown parser: inline emphasis markers are stripped rather than styled, exactly
/// as MessageContent.jsx leaves them as literal characters.
/// </summary>
public static class ReportContentParser
{
    public static IReadOnlyList<ReportBlock> Parse(string? content)
    {
        var blocks = new List<ReportBlock>();
        if (string.IsNullOrWhiteSpace(content)) return blocks;

        string[] lines = content.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var bullets = new List<string>();
        var tableRows = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            string text = Clean(string.Join(" ", paragraph));
            if (text.Length > 0) blocks.Add(new ParagraphBlock(text));
            paragraph.Clear();
        }

        void FlushBullets()
        {
            if (bullets.Count == 0) return;
            blocks.Add(new BulletsBlock(bullets.Select(Clean).Where(b => b.Length > 0).ToList()));
            bullets.Clear();
        }

        void FlushTable()
        {
            if (tableRows.Count == 0) return;
            if (BuildTable(tableRows) is { } table) blocks.Add(table);
            tableRows.Clear();
        }

        void FlushAll() { FlushParagraph(); FlushBullets(); FlushTable(); }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // ---- fenced block ----
            if (trimmed.StartsWith("```"))
            {
                FlushAll();

                string language = trimmed[3..].Trim().ToLowerInvariant();
                var body = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    body.Add(lines[i]);
                    i++;
                }

                string raw = string.Join("\n", body);

                if (language == "chart")
                {
                    // A chart becomes its data. The reader of an exported report needs the
                    // numbers; redrawing the picture in four different file formats would be a
                    // lot of machinery for a worse artefact than the table.
                    ReportBlock chartBlock = ChartToTable(raw) is { } table
                        ? table
                        : new CodeBlock("chart", raw);
                    blocks.Add(chartBlock);
                }
                else if (raw.Trim().Length > 0)
                {
                    blocks.Add(new CodeBlock(language, raw));
                }

                continue;
            }

            // ---- pipe table ----
            if (trimmed.StartsWith('|') && trimmed.EndsWith('|') && trimmed.Length > 2)
            {
                FlushParagraph();
                FlushBullets();
                tableRows.Add(trimmed);
                continue;
            }
            FlushTable();

            // ---- heading ----
            if (trimmed.StartsWith('#'))
            {
                FlushAll();
                int level = trimmed.TakeWhile(c => c == '#').Count();
                string text = Clean(trimmed[level..]);
                if (text.Length > 0) blocks.Add(new HeadingBlock(Math.Clamp(level, 1, 3), text));
                continue;
            }

            // ---- bullet ----
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ")
                || (trimmed.Length > 2 && char.IsDigit(trimmed[0]) && (trimmed[1] == '.' || trimmed[1] == ')')))
            {
                FlushParagraph();
                bullets.Add(trimmed.StartsWith("- ") || trimmed.StartsWith("* ")
                    ? trimmed[2..]
                    : trimmed[2..].TrimStart());
                continue;
            }

            // ---- blank line ends a run ----
            if (trimmed.Length == 0)
            {
                FlushAll();
                continue;
            }

            FlushBullets();
            paragraph.Add(trimmed);
        }

        FlushAll();
        return blocks;
    }

    /// <summary>
    /// Reads a chart spec into a table: labels down the first column, one column per series.
    /// Returns null when the spec is not the shape the chart renderer accepts, so the caller can
    /// fall back to showing the block verbatim rather than inventing numbers.
    /// </summary>
    private static TableBlock? ChartToTable(string raw)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("labels", out JsonElement labelsEl)
                || labelsEl.ValueKind != JsonValueKind.Array) return null;

            List<string> labels = labelsEl.EnumerateArray()
                .Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : l.ToString())
                .ToList();

            if (!root.TryGetProperty("series", out JsonElement seriesEl)
                || seriesEl.ValueKind != JsonValueKind.Array) return null;

            var names = new List<string>();
            var columns = new List<List<string>>();

            int index = 0;
            foreach (JsonElement s in seriesEl.EnumerateArray())
            {
                index++;
                if (s.ValueKind != JsonValueKind.Object
                    || !s.TryGetProperty("values", out JsonElement valuesEl)
                    || valuesEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                string name = s.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? $"Series {index}"
                    : $"Series {index}";

                names.Add(name);
                columns.Add(valuesEl.EnumerateArray()
                    .Select(v => v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.ToString())
                    .ToList());
            }

            if (labels.Count == 0 || columns.Count == 0) return null;

            var rows = new List<IReadOnlyList<string>>();
            for (int r = 0; r < labels.Count; r++)
            {
                var row = new List<string> { labels[r] };
                row.AddRange(columns.Select(c => r < c.Count ? c[r] : string.Empty));
                rows.Add(row);
            }

            string title = root.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "Chart data"
                : "Chart data";

            string unit = root.TryGetProperty("unit", out JsonElement u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() ?? string.Empty
                : string.Empty;

            var headers = new List<string> { "" };
            headers.AddRange(unit.Length > 0 ? names.Select(n => $"{n} ({unit})") : names);

            return new TableBlock(headers, rows,
                Caption: $"{title} — shown as a chart in the conversation");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TableBlock? BuildTable(List<string> rows)
    {
        List<List<string>> cells = rows
            .Select(r => r.Trim('|').Split('|').Select(c => Clean(c)).ToList())
            .ToList();

        // A markdown table's second row is the |---|---| separator; drop it.
        if (cells.Count >= 2 && cells[1].All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' ')))
        {
            cells.RemoveAt(1);
        }

        if (cells.Count == 0) return null;

        List<string> headers = cells[0];
        var body = cells.Skip(1)
            .Select(r => (IReadOnlyList<string>)PadTo(r, headers.Count))
            .ToList();

        return body.Count == 0 ? null : new TableBlock(headers, body);
    }

    private static List<string> PadTo(List<string> row, int width)
    {
        while (row.Count < width) row.Add(string.Empty);
        return row.Count > width ? row.Take(width).ToList() : row;
    }

    /// <summary>
    /// Drops the inline markers the chat UI does not render either, so the exported text reads
    /// the way it did on screen instead of carrying stray asterisks and backticks.
    /// </summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;

        while (i < text.Length)
        {
            if (text[i] == '*' || text[i] == '`' || text[i] == '_')
            {
                char marker = text[i];
                int run = 0;
                while (i < text.Length && text[i] == marker) { run++; i++; }

                // A lone underscore inside a word (snake_case) is content, not emphasis.
                if (marker == '_' && run == 1 && sb.Length > 0 && i < text.Length
                    && char.IsLetterOrDigit(text[i]) && char.IsLetterOrDigit(sb[^1]))
                {
                    sb.Append('_');
                }
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString().Trim();
    }
}
