using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MgtAiAuthen.Api.Services.Reporting;

/// <summary>
/// Writes the report as .docx (DocumentFormat.OpenXml, MIT).
///
/// Built with the raw OpenXML SDK rather than a friendlier wrapper because the wrappers with
/// good ergonomics carry licences that need checking, and a document of headings, paragraphs,
/// bullets and tables needs little more than this file contains.
/// </summary>
public class WordReportWriter : IReportWriter
{
    public string Format => ReportFormats.Word;

    /// <summary>Thai text needs the East-Asian/complex-script slots set, not just ascii.</summary>
    private const string FontName = "Sarabun";

    public byte[] Write(ReportDocument report)
    {
        using var stream = new MemoryStream();

        using (WordprocessingDocument doc = WordprocessingDocument.Create(
            stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            MainDocumentPart main = doc.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());

            AddStyles(main);
            AddNumbering(main);

            body.AppendChild(Heading(report.Title, 28, before: 0));

            foreach ((string label, string value) in ExcelReportWriter.Metadata(report))
            {
                body.AppendChild(MetaLine(label, value));
            }

            body.AppendChild(new Paragraph(new ParagraphProperties(
                new ParagraphBorders(new BottomBorder
                {
                    Val = BorderValues.Single, Color = "D8DCE4", Size = 6,
                }),
                new SpacingBetweenLines { After = "240" })));

            foreach (ReportBlock block in report.Blocks)
            {
                switch (block)
                {
                    case HeadingBlock h:
                        body.AppendChild(Heading(h.Text, h.Level == 1 ? 26 : 22));
                        break;

                    case ParagraphBlock p:
                        body.AppendChild(Body(p.Text));
                        break;

                    case BulletsBlock b:
                        foreach (string item in b.Items) body.AppendChild(Bullet(item));
                        break;

                    case CodeBlock c:
                        foreach (string line in c.Text.Split('\n')) body.AppendChild(Mono(line));
                        break;

                    case TableBlock t:
                        if (t.Caption is not null) body.AppendChild(Caption(t.Caption));
                        body.AppendChild(BuildTable(t));
                        body.AppendChild(Body(string.Empty));
                        break;
                }
            }
        }

        return stream.ToArray();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        StyleDefinitionsPart part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(
                    new RunPropertiesBaseStyle(
                        new RunFonts
                        {
                            Ascii = FontName, HighAnsi = FontName,
                            // Thai is a complex script; Word picks the Cs slot for it, and
                            // leaving it unset makes the document render Thai in a fallback face.
                            ComplexScript = FontName, EastAsia = FontName,
                        },
                        new FontSize { Val = "22" },
                        new FontSizeComplexScript { Val = "22" }))));
        part.Styles.Save();
    }

    /// <summary>Real bullet glyphs need a numbering definition; without one Word shows plain text.</summary>
    private static void AddNumbering(MainDocumentPart main)
    {
        NumberingDefinitionsPart part = main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering = new Numbering(
            new AbstractNum(
                new Level(
                    new NumberingFormat { Val = NumberFormatValues.Bullet },
                    new LevelText { Val = "•" },
                    new ParagraphProperties(new Indentation { Left = "420", Hanging = "220" }))
                { LevelIndex = 0 })
            { AbstractNumberId = 1 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });
        part.Numbering.Save();
    }

    private static Paragraph Heading(string text, int halfPoints, int before = 240) => new(
        new ParagraphProperties(
            new SpacingBetweenLines { Before = before.ToString(), After = "120" }),
        Run(text, halfPoints, bold: true));

    private static Paragraph Body(string text) => new(
        new ParagraphProperties(new SpacingBetweenLines { After = "120" }),
        Run(text, 22));

    private static Paragraph Caption(string text) => new(
        new ParagraphProperties(new SpacingBetweenLines { After = "60" }),
        Run(text, 18, italic: true, color: "6B7280"));

    private static Paragraph Mono(string text) => new(
        new ParagraphProperties(
            new Shading { Val = ShadingPatternValues.Clear, Fill = "F8FAFC" },
            new SpacingBetweenLines { After = "0" }),
        new Run(
            new RunProperties(
                new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = FontName },
                new FontSize { Val = "18" }),
            new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph MetaLine(string label, string value) => new(
        new ParagraphProperties(new SpacingBetweenLines { After = "0" }),
        Run($"{label}: ", 18, bold: true, color: "6B7280"),
        Run(value, 18));

    private static Paragraph Bullet(string text) => new(
        new ParagraphProperties(
            new NumberingProperties(
                new NumberingLevelReference { Val = 0 },
                new NumberingId { Val = 1 }),
            new SpacingBetweenLines { After = "60" }),
        Run(text, 22));

    private static Run Run(string text, int halfPoints, bool bold = false, bool italic = false,
        string? color = null)
    {
        var properties = new RunProperties(
            new RunFonts
            {
                Ascii = FontName, HighAnsi = FontName,
                ComplexScript = FontName, EastAsia = FontName,
            },
            new FontSize { Val = halfPoints.ToString() },
            new FontSizeComplexScript { Val = halfPoints.ToString() });

        if (bold) { properties.AppendChild(new Bold()); properties.AppendChild(new BoldComplexScript()); }
        if (italic) properties.AppendChild(new Italic());
        if (color is not null) properties.AppendChild(new Color { Val = color });

        return new Run(properties, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Table BuildTable(TableBlock block)
    {
        var table = new Table(new TableProperties(
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Color = "E2E8F0", Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Color = "E2E8F0", Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Color = "E2E8F0", Size = 4 },
                new RightBorder { Val = BorderValues.Single, Color = "E2E8F0", Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Color = "E2E8F0", Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Color = "E2E8F0", Size = 4 })));

        var headerRow = new TableRow(new TableRowProperties(new TableHeader()));
        foreach (string header in block.Headers)
        {
            headerRow.AppendChild(new TableCell(
                new TableCellProperties(
                    new Shading { Val = ShadingPatternValues.Clear, Fill = "F1F5F9" }),
                new Paragraph(
                    new ParagraphProperties(new SpacingBetweenLines { After = "0" }),
                    Run(header, 20, bold: true))));
        }
        table.AppendChild(headerRow);

        foreach (IReadOnlyList<string> row in block.Rows)
        {
            var tableRow = new TableRow();
            for (int c = 0; c < block.Headers.Count; c++)
            {
                string value = c < row.Count ? row[c] : string.Empty;

                bool numeric = c > 0 && double.TryParse(value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _);

                tableRow.AppendChild(new TableCell(new Paragraph(
                    new ParagraphProperties(
                        new SpacingBetweenLines { After = "0" },
                        new Justification
                        {
                            // Figures line up on their digits, as in the spreadsheet export.
                            Val = numeric ? JustificationValues.Right : JustificationValues.Left,
                        }),
                    Run(value, 20))));
            }
            table.AppendChild(tableRow);
        }

        return table;
    }
}
