using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace MgtAiAuthen.Api.Services.Reporting;

/// <summary>
/// Writes the report as PDF (PDFsharp 6, MIT).
///
/// PDFsharp rather than QuestPDF, which is the more convenient library but whose Community
/// licence is free only below a company revenue threshold — not a question worth leaving
/// unanswered inside a public company's codebase. PDFsharp is MIT with no such condition; the
/// cost is laying the page out by hand, which this class does.
/// </summary>
public class PdfReportWriter : IReportWriter
{
    public string Format => ReportFormats.Pdf;

    private const double Margin = 50;
    private const double LineGap = 4;

    private readonly XFont _body;
    private readonly XFont _bold;
    private readonly XFont _h1;
    private readonly XFont _h2;
    private readonly XFont _small;
    private readonly XFont _mono;

    public PdfReportWriter(IReportFontProvider fonts)
    {
        // Must be set before any XFont is constructed, and only once per process.
        GlobalFontSettings.FontResolver ??= fonts.Resolver;

        _h1 = new XFont(fonts.FamilyName, 18, XFontStyleEx.Bold);
        _h2 = new XFont(fonts.FamilyName, 13, XFontStyleEx.Bold);
        _bold = new XFont(fonts.FamilyName, 10.5, XFontStyleEx.Bold);
        _body = new XFont(fonts.FamilyName, 10.5, XFontStyleEx.Regular);
        _small = new XFont(fonts.FamilyName, 8.5, XFontStyleEx.Regular);
        _mono = new XFont(fonts.MonoFamilyName, 9, XFontStyleEx.Regular);
    }

    public byte[] Write(ReportDocument report)
    {
        using var document = new PdfDocument();
        document.Info.Title = report.Title;
        document.Info.Author = report.AuthorName;

        var page = new Page(document, _small);

        page.Text(report.Title, _h1, XBrushes.Black);
        page.Space(6);

        foreach ((string label, string value) in ExcelReportWriter.Metadata(report))
        {
            page.LabelValue(label, value, _bold, _small);
        }

        page.Rule();

        foreach (ReportBlock block in report.Blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    page.Space(8);
                    page.Text(h.Text, h.Level == 1 ? _h2 : _bold, XBrushes.Black);
                    page.Space(2);
                    break;

                case ParagraphBlock p:
                    page.Text(p.Text, _body, XBrushes.Black);
                    page.Space(6);
                    break;

                case BulletsBlock b:
                    foreach (string item in b.Items)
                    {
                        page.Bullet(item, _body);
                    }
                    page.Space(6);
                    break;

                case CodeBlock c:
                    page.Code(c.Text, _mono);
                    page.Space(6);
                    break;

                case TableBlock t:
                    if (t.Caption is not null)
                    {
                        page.Text(t.Caption, _small, XBrushes.Gray);
                        page.Space(2);
                    }
                    page.Table(t, _bold, _body);
                    page.Space(8);
                    break;
            }
        }

        page.Finish();

        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// A cursor over a growing set of pages. Every draw call asks for room first and starts a
    /// new page when it does not fit, so nothing is ever clipped at the bottom edge.
    /// </summary>
    private sealed class Page
    {
        private readonly PdfDocument _document;
        private readonly XFont _footer;
        private PdfPage _page = null!;
        private XGraphics _gfx = null!;
        private double _y;
        private int _number;

        public Page(PdfDocument document, XFont footer)
        {
            _document = document;
            _footer = footer;
            NewPage();
        }

        private double Width => _page.Width.Point - Margin * 2;

        private void NewPage()
        {
            _gfx?.Dispose();
            _page = _document.AddPage();
            _page.Size = PdfSharp.PageSize.A4;
            _gfx = XGraphics.FromPdfPage(_page);
            _y = Margin;
            _number++;
        }

        private void Ensure(double height)
        {
            if (_y + height <= _page.Height.Point - Margin - 24) return;
            StampFooter();
            NewPage();
        }

        private void StampFooter()
        {
            _gfx.DrawString($"Page {_number}", _footer, XBrushes.Gray,
                new XRect(Margin, _page.Height.Point - Margin - 12, Width, 12),
                XStringFormats.BottomRight);
        }

        public void Finish() { StampFooter(); _gfx.Dispose(); }

        public void Space(double h) => _y += h;

        public void Rule()
        {
            Ensure(12);
            _y += 6;
            _gfx.DrawLine(new XPen(XColor.FromArgb(210, 216, 224), 0.8),
                Margin, _y, Margin + Width, _y);
            _y += 10;
        }

        public void Text(string text, XFont font, XBrush brush, double indent = 0)
        {
            foreach (string line in Wrap(text, font, Width - indent))
            {
                double h = font.GetHeight() + LineGap;
                Ensure(h);
                _gfx.DrawString(line, font, brush, new XPoint(Margin + indent, _y + font.GetHeight()));
                _y += h;
            }
        }

        public void LabelValue(string label, string value, XFont bold, XFont body)
        {
            double h = body.GetHeight() + LineGap;
            Ensure(h);
            _gfx.DrawString($"{label}:", bold, XBrushes.Gray, new XPoint(Margin, _y + body.GetHeight()));
            double offset = 90;
            foreach (string line in Wrap(value, body, Width - offset))
            {
                Ensure(h);
                _gfx.DrawString(line, body, XBrushes.Black, new XPoint(Margin + offset, _y + body.GetHeight()));
                _y += h;
            }
        }

        public void Bullet(string text, XFont font)
        {
            double h = font.GetHeight() + LineGap;
            Ensure(h);
            _gfx.DrawString("•", font, XBrushes.Black, new XPoint(Margin + 4, _y + font.GetHeight()));
            Text(text, font, XBrushes.Black, indent: 18);
        }

        public void Code(string code, XFont mono)
        {
            string[] lines = code.Split('\n');
            double lineHeight = mono.GetHeight() + 2;

            foreach (string raw in lines)
            {
                // Code is not re-wrapped — a broken line of code is worse than a truncated one,
                // so long lines are cut with an ellipsis and the file stays honest about it.
                string line = raw;
                while (_gfx.MeasureString(line, mono).Width > Width - 12 && line.Length > 4)
                {
                    line = line[..^2];
                }
                if (line.Length < raw.Length) line += "…";

                Ensure(lineHeight);
                _gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(248, 250, 252)),
                    Margin, _y, Width, lineHeight);
                _gfx.DrawString(line, mono, XBrushes.Black, new XPoint(Margin + 6, _y + mono.GetHeight()));
                _y += lineHeight;
            }
        }

        public void Table(TableBlock table, XFont header, XFont body)
        {
            int columns = table.Headers.Count;
            if (columns == 0) return;

            // First column carries the labels and gets more room; the rest split what is left.
            double firstWidth = columns == 1 ? Width : Math.Min(Width * 0.4, Width / columns * 1.8);
            double otherWidth = columns == 1 ? 0 : (Width - firstWidth) / (columns - 1);
            double[] widths = Enumerable.Range(0, columns)
                .Select(i => i == 0 ? firstWidth : otherWidth).ToArray();

            void Row(IReadOnlyList<string> cells, XFont font, bool shade)
            {
                var wrapped = new List<string[]>();
                for (int c = 0; c < columns; c++)
                {
                    string value = c < cells.Count ? cells[c] : string.Empty;
                    wrapped.Add(Wrap(value, font, widths[c] - 10).ToArray());
                }

                int tallest = Math.Max(1, wrapped.Max(w => w.Length));
                double rowHeight = tallest * (font.GetHeight() + 2) + 6;
                Ensure(rowHeight);

                if (shade)
                {
                    _gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(241, 245, 249)),
                        Margin, _y, Width, rowHeight);
                }

                double x = Margin;
                for (int c = 0; c < columns; c++)
                {
                    // Right-align numbers so a column of figures lines up on its digits.
                    bool numeric = c > 0 && double.TryParse(
                        c < cells.Count ? cells[c] : "",
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out _);

                    for (int l = 0; l < wrapped[c].Length; l++)
                    {
                        var rect = new XRect(x + 5, _y + 3 + l * (font.GetHeight() + 2),
                            widths[c] - 10, font.GetHeight());
                        _gfx.DrawString(wrapped[c][l], font, XBrushes.Black, rect,
                            numeric ? XStringFormats.TopRight : XStringFormats.TopLeft);
                    }
                    x += widths[c];
                }

                var pen = new XPen(XColor.FromArgb(226, 232, 240), 0.6);
                _gfx.DrawLine(pen, Margin, _y + rowHeight, Margin + Width, _y + rowHeight);
                _y += rowHeight;
            }

            Row(table.Headers, header, shade: true);
            foreach (IReadOnlyList<string> row in table.Rows) Row(row, body, shade: false);
        }

        private IEnumerable<string> Wrap(string text, XFont font, double max)
        {
            if (string.IsNullOrEmpty(text)) return [string.Empty];
            if (_gfx.MeasureString(text, font).Width <= max) return [text];

            var lines = new List<string>();
            var current = new System.Text.StringBuilder();

            foreach (string word in text.Split(' '))
            {
                string candidate = current.Length == 0 ? word : $"{current} {word}";

                if (_gfx.MeasureString(candidate, font).Width <= max)
                {
                    current.Clear().Append(candidate);
                    continue;
                }

                if (current.Length > 0) { lines.Add(current.ToString()); current.Clear(); }

                // Thai runs have no spaces to break on, so an over-long "word" is split by
                // measurement rather than left to overflow the page.
                string rest = word;
                while (_gfx.MeasureString(rest, font).Width > max && rest.Length > 1)
                {
                    int take = rest.Length;
                    while (take > 1 && _gfx.MeasureString(rest[..take], font).Width > max) take--;
                    lines.Add(rest[..take]);
                    rest = rest[take..];
                }
                current.Append(rest);
            }

            if (current.Length > 0) lines.Add(current.ToString());
            return lines;
        }
    }
}
