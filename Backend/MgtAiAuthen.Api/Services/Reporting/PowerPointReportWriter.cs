using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace MgtAiAuthen.Api.Services.Reporting;

/// <summary>
/// Writes the report as .pptx (DocumentFormat.OpenXml, MIT).
///
/// A deck is not a document with page breaks: text that runs long has to be split across slides
/// or it silently overflows the shape and is invisible in presentation mode. So this writer
/// re-flows the report — a heading opens a slide, bullets fill it up to a fixed count, and each
/// table becomes its own slide (chunked when it has more rows than a slide can show).
/// </summary>
public class PowerPointReportWriter : IReportWriter
{
    public string Format => ReportFormats.PowerPoint;

    private const string FontName = "Sarabun";

    // 16:9 at 914400 EMU per inch.
    private const long SlideWidth = 12192000;
    private const long SlideHeight = 6858000;

    private const int MaxBulletsPerSlide = 7;
    private const int MaxTableRowsPerSlide = 10;

    public byte[] Write(ReportDocument report)
    {
        using var stream = new MemoryStream();

        using (PresentationDocument doc = PresentationDocument.Create(
            stream, PresentationDocumentType.Presentation, autoSave: true))
        {
            PresentationPart presentationPart = doc.AddPresentationPart();
            presentationPart.Presentation = new Presentation
            {
                SlideSize = new SlideSize { Cx = (int)SlideWidth, Cy = (int)SlideHeight },
                NotesSize = new NotesSize { Cx = (int)SlideHeight, Cy = (int)SlideWidth },
            };

            SlideMasterPart masterPart = PowerPointScaffold.Build(presentationPart);
            var slideIdList = new SlideIdList();
            uint slideId = 256;

            void AddSlide(Slide slide)
            {
                SlidePart part = presentationPart.AddNewPart<SlidePart>();
                part.Slide = slide;
                part.AddPart(masterPart.SlideLayoutParts.First());
                slideIdList.Append(new SlideId
                {
                    Id = slideId++, RelationshipId = presentationPart.GetIdOfPart(part),
                });
            }

            // ---- title slide ----
            AddSlide(BuildSlide(report.Title,
                ExcelReportWriter.Metadata(report).Select(m => $"{m.Label}: {m.Value}").ToList(),
                titleSlide: true));

            // ---- content ----
            string currentHeading = "Summary";
            var pending = new List<string>();

            void Flush()
            {
                if (pending.Count == 0) return;

                foreach (List<string> chunk in Chunk(pending, MaxBulletsPerSlide))
                {
                    AddSlide(BuildSlide(currentHeading, chunk));
                }
                pending.Clear();
            }

            foreach (ReportBlock block in report.Blocks)
            {
                switch (block)
                {
                    case HeadingBlock h:
                        Flush();
                        currentHeading = h.Text;
                        break;

                    case ParagraphBlock p:
                        // A paragraph on a slide is a bullet; a wall of prose is not a slide.
                        pending.Add(Shorten(p.Text, 220));
                        break;

                    case BulletsBlock b:
                        pending.AddRange(b.Items.Select(i => Shorten(i, 180)));
                        break;

                    case CodeBlock c:
                        Flush();
                        foreach (List<string> chunk in Chunk(
                            c.Text.Split('\n').Where(l => l.Trim().Length > 0).ToList(), 12))
                        {
                            AddSlide(BuildSlide(currentHeading, chunk, mono: true));
                        }
                        break;

                    case TableBlock t:
                        Flush();
                        List<IReadOnlyList<string>> rows = t.Rows.ToList();
                        int part = 0;
                        foreach (List<IReadOnlyList<string>> chunk in Chunk(rows, MaxTableRowsPerSlide))
                        {
                            part++;
                            string title = t.Caption ?? currentHeading;
                            if (rows.Count > MaxTableRowsPerSlide) title += $" ({part})";
                            AddSlide(BuildTableSlide(title, t.Headers, chunk));
                        }
                        break;
                }
            }

            Flush();

            presentationPart.Presentation.Append(
                new SlideMasterIdList(new SlideMasterId
                {
                    Id = 2147483648U, RelationshipId = presentationPart.GetIdOfPart(masterPart),
                }),
                slideIdList);

            presentationPart.Presentation.Save();
        }

        return stream.ToArray();
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> items, int size)
    {
        if (items.Count == 0) yield break;
        for (int i = 0; i < items.Count; i += size)
        {
            yield return items.GetRange(i, Math.Min(size, items.Count - i));
        }
    }

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private static Slide BuildSlide(string title, IReadOnlyList<string> bullets,
        bool titleSlide = false, bool mono = false)
    {
        var shapes = new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()));

        shapes.Append(TextShape(2, "Title", title,
            x: 685800, y: titleSlide ? 2000000 : 480000,
            cx: SlideWidth - 685800 * 2, cy: 1100000,
            size: titleSlide ? 4000 : 2800, bold: true));

        if (bullets.Count > 0)
        {
            shapes.Append(TextShape(3, "Body", bullets,
                x: 685800, y: titleSlide ? 3300000 : 1750000,
                cx: SlideWidth - 685800 * 2, cy: SlideHeight - (titleSlide ? 3300000 : 1750000) - 480000,
                size: mono ? 1400 : 1800, bullet: !titleSlide && !mono, mono: mono));
        }

        return new Slide(new CommonSlideData(shapes), new ColorMapOverride(new A.MasterColorMapping()));
    }

    private static Slide BuildTableSlide(string title, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var shapes = new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()));

        shapes.Append(TextShape(2, "Title", title,
            x: 685800, y: 400000, cx: SlideWidth - 685800 * 2, cy: 900000, size: 2400, bold: true));

        long tableWidth = SlideWidth - 685800 * 2;
        long columnWidth = tableWidth / Math.Max(1, headers.Count);

        var grid = new A.TableGrid();
        for (int i = 0; i < headers.Count; i++)
        {
            grid.Append(new A.GridColumn { Width = columnWidth });
        }

        var table = new A.Table(new A.TableProperties { FirstRow = true, BandRow = true }, grid);
        table.Append(TableRow(headers.Select(h => h).ToList(), header: true));

        foreach (IReadOnlyList<string> row in rows)
        {
            var cells = new List<string>();
            for (int c = 0; c < headers.Count; c++) cells.Add(c < row.Count ? row[c] : string.Empty);
            table.Append(TableRow(cells, header: false));
        }

        shapes.Append(new GraphicFrame(
            new NonVisualGraphicFrameProperties(
                new NonVisualDrawingProperties { Id = 4, Name = "Table" },
                new NonVisualGraphicFrameDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new Transform(
                new A.Offset { X = 685800, Y = 1500000 },
                new A.Extents { Cx = tableWidth, Cy = 400000 * (rows.Count + 1) }),
            new A.Graphic(new A.GraphicData(table)
            {
                Uri = "http://schemas.openxmlformats.org/drawingml/2006/table",
            })));

        return new Slide(new CommonSlideData(shapes), new ColorMapOverride(new A.MasterColorMapping()));
    }

    private static A.TableRow TableRow(IReadOnlyList<string> cells, bool header)
    {
        var row = new A.TableRow { Height = 370000 };

        for (int i = 0; i < cells.Count; i++)
        {
            bool numeric = i > 0 && double.TryParse(cells[i],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _);

            row.Append(new A.TableCell(
                new A.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(
                        new A.ParagraphProperties
                        {
                            Alignment = numeric ? A.TextAlignmentTypeValues.Right
                                                : A.TextAlignmentTypeValues.Left,
                        },
                        Text(cells[i], header ? 1400 : 1300, header))),
                new A.TableCellProperties()));
        }

        return row;
    }

    private static Shape TextShape(uint id, string name, string text, long x, long y,
        long cx, long cy, int size, bool bold = false)
        => TextShape(id, name, [text], x, y, cx, cy, size, bold);

    private static Shape TextShape(uint id, string name, IReadOnlyList<string> lines, long x, long y,
        long cx, long cy, int size, bool bold = false, bool bullet = false, bool mono = false)
    {
        var body = new TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
            new A.ListStyle());

        foreach (string line in lines)
        {
            var properties = new A.ParagraphProperties();
            if (bullet)
            {
                properties.Append(new A.CharacterBullet { Char = "•" });
                properties.Indent = -220000;
                properties.LeftMargin = 320000;
            }
            else
            {
                properties.Append(new A.NoBullet());
            }

            body.Append(new A.Paragraph(properties, Text(line, size, bold, mono)));
        }

        return new Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = id, Name = name },
                new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            body);
    }

    private static A.Run Text(string text, int size, bool bold = false, bool mono = false)
        => new(
            new A.RunProperties
            {
                Language = "th-TH", FontSize = size, Bold = bold, Dirty = false,
            }.Append2(mono ? "Consolas" : FontName),
            new A.Text(text));
}

internal static class OpenXmlDrawingExtensions
{
    /// <summary>
    /// Sets the latin *and* complex-script font on a run.
    ///
    /// PowerPoint chooses the complex-script slot for Thai; setting only the latin font leaves
    /// Thai text in whatever the theme falls back to, which on another machine may not have Thai
    /// glyphs at all.
    /// </summary>
    internal static A.RunProperties Append2(this A.RunProperties properties, string fontName)
    {
        properties.Append(new A.LatinFont { Typeface = fontName });
        properties.Append(new A.ComplexScriptFont { Typeface = fontName });
        properties.Append(new A.EastAsianFont { Typeface = fontName });
        return properties;
    }
}
