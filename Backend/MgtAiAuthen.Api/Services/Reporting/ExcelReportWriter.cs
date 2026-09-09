using ClosedXML.Excel;

namespace MgtAiAuthen.Api.Services.Reporting;

/// <summary>
/// Writes the report as .xlsx (ClosedXML, MIT).
///
/// Layout choice: the narrative goes on a "Report" sheet and every table gets a sheet of its own
/// with a real header row. A spreadsheet is opened to work with the numbers — pasting tables into
/// the middle of prose would leave them unsortable and unfilterable, which is the whole reason
/// someone asked for Excel rather than PDF.
/// </summary>
public class ExcelReportWriter : IReportWriter
{
    public string Format => ReportFormats.Excel;

    public byte[] Write(ReportDocument report)
    {
        using var workbook = new XLWorkbook();
        workbook.Properties.Title = report.Title;
        workbook.Properties.Author = report.AuthorName;

        IXLWorksheet sheet = workbook.Worksheets.Add("Report");
        int row = 1;

        sheet.Cell(row, 1).Value = report.Title;
        sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(16);
        row += 2;

        foreach ((string label, string value) in Metadata(report))
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 1).Style.Font.SetBold();
            sheet.Cell(row, 2).Value = value;
            row++;
        }

        row++;
        int tableIndex = 0;

        foreach (ReportBlock block in report.Blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    row++;
                    sheet.Cell(row, 1).Value = h.Text;
                    sheet.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(h.Level == 1 ? 14 : 12);
                    row += 2;
                    break;

                case ParagraphBlock p:
                    sheet.Cell(row, 1).Value = p.Text;
                    sheet.Cell(row, 1).Style.Alignment.SetWrapText();
                    // Merging keeps a long sentence readable instead of running under the columns
                    // to its right.
                    sheet.Range(row, 1, row, 8).Merge();
                    sheet.Row(row).AdjustToContents();
                    row += 2;
                    break;

                case BulletsBlock b:
                    foreach (string item in b.Items)
                    {
                        sheet.Cell(row, 1).Value = $"•  {item}";
                        sheet.Range(row, 1, row, 8).Merge();
                        row++;
                    }
                    row++;
                    break;

                case CodeBlock c:
                    foreach (string line in c.Text.Split('\n'))
                    {
                        sheet.Cell(row, 1).Value = line;
                        sheet.Cell(row, 1).Style.Font.SetFontName("Consolas");
                        row++;
                    }
                    row++;
                    break;

                case TableBlock t:
                    tableIndex++;
                    string name = SheetName(t.Caption ?? $"Table {tableIndex}", tableIndex, workbook);
                    WriteTable(workbook.Worksheets.Add(name), t);

                    sheet.Cell(row, 1).Value = t.Caption is null
                        ? $"Table {tableIndex} → sheet \"{name}\""
                        : $"{t.Caption} → sheet \"{name}\"";
                    sheet.Cell(row, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
                    row += 2;
                    break;
            }
        }

        sheet.Column(1).Width = 90;
        sheet.Column(2).AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteTable(IXLWorksheet sheet, TableBlock table)
    {
        for (int c = 0; c < table.Headers.Count; c++)
        {
            IXLCell cell = sheet.Cell(1, c + 1);
            cell.Value = table.Headers[c];
            cell.Style.Font.SetBold();
            cell.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#F1F5F9"));
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        for (int r = 0; r < table.Rows.Count; r++)
        {
            IReadOnlyList<string> row = table.Rows[r];
            for (int c = 0; c < row.Count && c < table.Headers.Count; c++)
            {
                IXLCell cell = sheet.Cell(r + 2, c + 1);

                // Numbers are written as numbers, not text — otherwise SUM and sorting in the
                // exported file do not work, which defeats exporting to a spreadsheet at all.
                if (table.NumericColumns && c > 0
                    && double.TryParse(row[c], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double number))
                {
                    cell.Value = number;
                    cell.Style.NumberFormat.Format = "#,##0.####";
                }
                else
                {
                    cell.Value = row[c];
                }
            }
        }

        sheet.SheetView.FreezeRows(1);
        sheet.RangeUsed()?.SetAutoFilter();
        sheet.Columns().AdjustToContents(1, 60d);
    }

    /// <summary>Excel sheet names cap at 31 characters and forbid : \ / ? * [ ] and duplicates.</summary>
    private static string SheetName(string wanted, int index, XLWorkbook workbook)
    {
        string clean = new(wanted.Where(c => !":\\/?*[]".Contains(c)).ToArray());
        clean = clean.Trim();
        if (clean.Length == 0) clean = $"Table {index}";
        if (clean.Length > 28) clean = clean[..28].Trim();

        string candidate = clean;
        int suffix = 2;
        while (workbook.Worksheets.Any(w => string.Equals(w.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{clean} {suffix++}";
            if (candidate.Length > 31) candidate = candidate[..31];
        }

        return candidate;
    }

    internal static IEnumerable<(string Label, string Value)> Metadata(ReportDocument r)
    {
        yield return ("Question", r.Question);
        yield return ("Prepared for", string.IsNullOrWhiteSpace(r.Department)
            ? r.AuthorName
            : $"{r.AuthorName} ({r.Department})");
        yield return ("Answered", r.MessageAt.ToString("yyyy-MM-dd HH:mm"));
        yield return ("Exported", r.GeneratedAt.ToString("yyyy-MM-dd HH:mm"));
        if (!string.IsNullOrWhiteSpace(r.ModelName)) yield return ("AI model", r.ModelName!);
    }
}
