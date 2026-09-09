using System.Globalization;
using System.Text;
using ExcelDataReader;
using MgtAiAuthen.Api.Options;
using Microsoft.Extensions.Options;

namespace MgtAiAuthen.Api.Services;

/// <summary>
/// Turns an .xlsx / .xls workbook into plain text.
///
/// Why convert at all: **none of the three AI providers can read a spreadsheet**. Anthropic takes
/// PDF and plain text, Gemini takes PDF and images, OpenAI's input_file takes PDF — hand any of
/// them an .xlsx and the call either fails or the file is ignored. Converting server-side means
/// one code path works for every provider, and it buys something better besides: the cell contents
/// go through PolicyRules like a .csv does, instead of being screened by file name only the way
/// PDFs and images are.
///
/// The original file is still stored on disk untouched, so an auditor downloads the real workbook.
/// </summary>
public interface ISpreadsheetTextExtractor
{
    /// <summary>
    /// Reads every sheet into a pipe-separated text block. Throws <see cref="Infrastructure.AppException"/>
    /// when the bytes are not a workbook at all — which is the real defence against a renamed file,
    /// since an .xlsx is a ZIP and so shares its magic bytes with any other zip-based format.
    /// </summary>
    string Extract(byte[] content, string fileName);
}

public class SpreadsheetTextExtractor : ISpreadsheetTextExtractor
{
    private readonly UploadOptions _options;
    private readonly ILogger<SpreadsheetTextExtractor> _logger;

    static SpreadsheetTextExtractor()
    {
        // Legacy .xls stores strings in a Windows code page rather than Unicode, and .NET Core
        // ships only a handful of encodings by default. Without this, reading a .xls written by
        // a Thai-locale Excel throws NotSupportedException for code page 874.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public SpreadsheetTextExtractor(
        IOptions<UploadOptions> options,
        ILogger<SpreadsheetTextExtractor> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Extract(byte[] content, string fileName)
    {
        var text = new StringBuilder();
        int sheetCount = 0, totalRows = 0;
        bool truncated = false;

        try
        {
            using var stream = new MemoryStream(content, writable: false);

            // CreateReader sniffs the container and picks the .xlsx or .xls implementation itself,
            // so a file with the wrong extension but valid content still reads correctly.
            using IExcelDataReader reader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                sheetCount++;
                string sheetName = string.IsNullOrWhiteSpace(reader.Name) ? $"Sheet{sheetCount}" : reader.Name;

                var rows = new List<string>();
                int rowsInSheet = 0;

                while (reader.Read())
                {
                    if (rowsInSheet >= _options.MaxSpreadsheetRows)
                    {
                        truncated = true;
                        break;
                    }

                    string? row = FormatRow(reader);

                    // Skip blank rows — a workbook often carries thousands of empty trailing rows
                    // and sending them wastes input tokens on nothing.
                    if (row is null)
                    {
                        continue;
                    }

                    rows.Add(row);
                    rowsInSheet++;
                    totalRows++;
                }

                if (rows.Count == 0)
                {
                    continue;
                }

                text.Append("--- Sheet: ").Append(sheetName)
                    .Append(" (").Append(rows.Count).Append(rows.Count == 1 ? " row" : " rows")
                    .Append(truncated ? ", truncated" : string.Empty)
                    .AppendLine(") ---");

                foreach (string row in rows)
                {
                    text.AppendLine(row);

                    if (text.Length > _options.MaxTextChars)
                    {
                        truncated = true;
                        break;
                    }
                }

                text.AppendLine();

                if (text.Length > _options.MaxTextChars)
                {
                    break;
                }
            }
            while (reader.NextResult());
        }
        catch (Exception ex)
        {
            // A renamed .zip, a corrupt workbook, or a password-protected file all land here.
            _logger.LogWarning(ex, "Could not read {FileName} as a spreadsheet", fileName);

            throw new Infrastructure.AppException(
                $"File \"{fileName}\" could not be read as a spreadsheet. It may be corrupt, " +
                "password-protected, or not really an Excel file. Try opening it in Excel and " +
                "re-saving it as .xlsx, or export the sheet as .csv.");
        }

        if (text.Length == 0)
        {
            throw new Infrastructure.AppException(
                $"File \"{fileName}\" contains no data — every sheet is empty.");
        }

        string result = text.ToString().TrimEnd();

        if (result.Length > _options.MaxTextChars)
        {
            result = result[.._options.MaxTextChars] +
                     "\n\n[... the rest of the workbook was cut off at the size limit ...]";
            truncated = true;
        }

        _logger.LogInformation(
            "Extracted {Chars} characters from {Sheets} sheet(s), {Rows} row(s) of {FileName}{Truncated}",
            result.Length, sheetCount, totalRows, fileName, truncated ? " (truncated)" : string.Empty);

        return truncated
            ? result + "\n\n[Note: this workbook was larger than the limit and only the part above " +
                       "was sent to the AI.]"
            : result;
    }

    /// <summary>
    /// One row as pipe-separated cells, or null when the row holds nothing.
    ///
    /// Pipes rather than commas because spreadsheet cells frequently contain commas themselves,
    /// and a model reading "a,b,c" cannot tell a two-column row with a comma in it from a
    /// three-column row.
    /// </summary>
    private static string? FormatRow(IExcelDataReader reader)
    {
        var cells = new List<string>(reader.FieldCount);
        bool any = false;

        for (int i = 0; i < reader.FieldCount; i++)
        {
            string cell = FormatCell(reader.GetValue(i));
            cells.Add(cell);

            if (cell.Length > 0)
            {
                any = true;
            }
        }

        if (!any)
        {
            return null;
        }

        // Drop trailing empty columns so a used range of 3 columns does not print 16,384 pipes.
        int last = cells.FindLastIndex(c => c.Length > 0);
        return string.Join(" | ", cells.Take(last + 1));
    }

    private static string FormatCell(object? value) => value switch
    {
        null => string.Empty,

        // A date-only cell keeps just the date: "2026-09-09 00:00:00" is noise the model has to
        // read past on every row.
        DateTime d => d.TimeOfDay == TimeSpan.Zero
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),

        // "R" would render 120 as "120" but 0.1+0.2 as "0.30000000000000004"; G15 keeps the
        // precision Excel itself shows and never falls into scientific notation for ordinary values.
        double n => n.ToString("G15", CultureInfo.InvariantCulture),

        bool b => b ? "TRUE" : "FALSE",

        _ => value.ToString()?.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim()
             ?? string.Empty,
    };
}
