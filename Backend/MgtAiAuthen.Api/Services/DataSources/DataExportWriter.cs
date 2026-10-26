using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace MgtAiAuthen.Api.Services.DataSources;

/// <summary>One result set of a query - header names and typed cell values, kept for export.</summary>
internal record QueryTable(string[] Columns, List<object?[]> Rows);

/// <summary>Turns the full result of a data-source query into an Excel workbook or a CSV file.</summary>
internal static class DataExportWriter
{
    private const int MaxCellChars = 32_000;

    public static (byte[] Content, string ContentType, string Extension) Build(
        IReadOnlyList<QueryTable> tables, string query, string sourceName, string format, bool truncated, int rowLimit)
    {
        return string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
            ? (Csv(tables), "text/csv; charset=utf-8", "csv")
            : (Excel(tables, query, sourceName, truncated, rowLimit),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx");
    }

    private static byte[] Excel(IReadOnlyList<QueryTable> tables, string query, string sourceName, bool truncated, int rowLimit)
    {
        using var workbook = new XLWorkbook();

        for (int t = 0; t < tables.Count; t++)
        {
            QueryTable table = tables[t];
            IXLWorksheet sheet = workbook.AddWorksheet(tables.Count > 1 ? $"Query {t + 1}" : "Data");

            for (int c = 0; c < table.Columns.Length; c++)
            {
                IXLCell header = sheet.Cell(1, c + 1);
                header.Value = table.Columns[c];
                header.Style.Font.Bold = true;
                header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0");
            }

            for (int r = 0; r < table.Rows.Count; r++)
            {
                object?[] row = table.Rows[r];
                for (int c = 0; c < row.Length; c++)
                {
                    SetCell(sheet.Cell(r + 2, c + 1), row[c]);
                }
            }

            if (table.Columns.Length > 0)
            {
                sheet.SheetView.FreezeRows(1);
                sheet.Range(1, 1, Math.Max(1, table.Rows.Count + 1), table.Columns.Length).SetAutoFilter();
                // Sizing every column of 100,000 rows is slow - the first rows are enough to pick a sensible width.
                sheet.Columns().AdjustToContents(1, Math.Min(table.Rows.Count + 1, 200), 8, 60);
            }
        }

        IXLWorksheet info = workbook.AddWorksheet("Query");
        info.Cell(1, 1).Value = "Source";
        info.Cell(1, 2).Value = sourceName;
        info.Cell(2, 1).Value = "Exported at";
        info.Cell(2, 2).Value = DateTime.Now;
        info.Cell(3, 1).Value = "Rows";
        info.Cell(3, 2).Value = tables.Sum(t => t.Rows.Count);
        if (truncated)
        {
            info.Cell(4, 1).Value = "Note";
            info.Cell(4, 2).Value = $"The result was cut at {rowLimit:N0} rows - narrow the question to get the rest.";
        }

        info.Cell(6, 1).Value = "Query";
        info.Cell(6, 2).Value = query.Length > MaxCellChars ? query[..MaxCellChars] : query;
        info.Column(1).Style.Font.Bold = true;
        info.Column(1).Width = 14;
        info.Column(2).Width = 100;
        info.Cell(6, 2).Style.Alignment.WrapText = true;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void SetCell(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                break;
            case string s:
                cell.Value = s.Length > MaxCellChars ? s[..MaxCellChars] : s;
                break;
            case bool b:
                cell.Value = b;
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                cell.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                break;
            case ulong ul:
                cell.Value = (double)ul;
                break;
            case float or double:
                cell.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                break;
            case decimal d:
                cell.Value = d;
                break;
            case DateTime dt:
                cell.Value = dt;
                cell.Style.DateFormat.Format = dt.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss";
                break;
            case DateTimeOffset dto:
                cell.Value = dto.DateTime;
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                break;
            case DateOnly date:
                cell.Value = date.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "yyyy-mm-dd";
                break;
            case byte[] bytes:
                cell.Value = $"<{bytes.Length} bytes>";
                break;
            case Array array:
                cell.Value = "{" + string.Join(",", array.Cast<object?>().Select(i => Convert.ToString(i, CultureInfo.InvariantCulture))) + "}";
                break;
            default:
                cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                break;
        }
    }

    private static byte[] Csv(IReadOnlyList<QueryTable> tables)
    {
        var sb = new StringBuilder();
        QueryTable? table = tables.FirstOrDefault();

        if (table is not null)
        {
            sb.Append(string.Join(',', table.Columns.Select(Quote))).Append("\r\n");
            foreach (object?[] row in table.Rows)
            {
                sb.Append(string.Join(',', row.Select(v => Quote(Text(v))))).Append("\r\n");
            }
        }

        // UTF-8 with a BOM so Excel opens Thai text correctly.
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Text(object? value) => value switch
    {
        null or DBNull => "",
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        byte[] bytes => $"<{bytes.Length} bytes>",
        Array array => "{" + string.Join(",", array.Cast<object?>().Select(i => Convert.ToString(i, CultureInfo.InvariantCulture))) + "}",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Quote(string value)
    {
        // A cell starting with = + - @ would run as a formula when the CSV is opened in Excel.
        if (value.Length > 0 && value[0] is '=' or '+' or '@' || (value.Length > 1 && value[0] == '-' && !char.IsDigit(value[1])))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
