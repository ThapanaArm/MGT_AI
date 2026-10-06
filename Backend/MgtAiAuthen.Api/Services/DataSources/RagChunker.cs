using System.Text;
using System.Text.RegularExpressions;

namespace MgtAiAuthen.Api.Services.DataSources;

public record RagPageText(int Page, string Text);

public record RagChunkPiece(string Text, int PageStart, int PageEnd);

/// <summary>
/// Paragraph-aware chunker matching the shape of the MGT_Accounting_Knowledge corpus: ~2,600 chars
/// per chunk (hard max 3,200) with ~420 chars of overlap carried into the next chunk, so a
/// sentence cut at a boundary still has its context on the other side.
/// </summary>
public static class RagChunker
{
    public const int TargetChars = 2600;
    public const int MaxChars = 3200;
    public const int OverlapChars = 420;

    private static readonly Regex ParagraphSplit = new(@"\r?\n\s*\r?\n", RegexOptions.Compiled);

    public static List<RagChunkPiece> Chunk(IEnumerable<RagPageText> pages)
    {
        var paragraphs = new List<(string Text, int Page)>();

        foreach (RagPageText page in pages.OrderBy(p => p.Page))
        {
            foreach (string raw in ParagraphSplit.Split(page.Text))
            {
                string paragraph = raw.Trim();
                if (paragraph.Length == 0) continue;

                foreach (string piece in SplitOversized(paragraph))
                {
                    paragraphs.Add((piece, page.Page));
                }
            }
        }

        var result = new List<RagChunkPiece>();
        var current = new StringBuilder();
        int startPage = 0;
        int endPage = 0;

        void Flush()
        {
            string text = current.ToString().Trim();
            if (text.Length > 0)
            {
                result.Add(new RagChunkPiece(text, startPage, endPage));
            }
        }

        foreach ((string text, int page) in paragraphs)
        {
            // +2 for the blank line that joins paragraphs.
            if (current.Length > 0 && current.Length + text.Length + 2 > TargetChars)
            {
                Flush();
                string overlap = TailForOverlap(current.ToString());
                current.Clear();
                current.Append(overlap);
                startPage = page;
            }

            if (current.Length == 0)
            {
                startPage = page;
            }
            else
            {
                current.Append("\n\n");
            }

            current.Append(text);
            endPage = page;
        }

        // A trailing chunk that is only the overlap carried from the previous one adds nothing.
        if (current.Length > OverlapChars || result.Count == 0)
        {
            Flush();
        }

        return result;
    }

    /// <summary>A paragraph longer than the hard max (e.g. an OCR'd page with no blank lines) is cut at sentence/line/space boundaries.</summary>
    private static IEnumerable<string> SplitOversized(string paragraph)
    {
        if (paragraph.Length <= MaxChars)
        {
            yield return paragraph;
            yield break;
        }

        int start = 0;
        while (start < paragraph.Length)
        {
            int remaining = paragraph.Length - start;
            if (remaining <= MaxChars)
            {
                yield return paragraph[start..].Trim();
                yield break;
            }

            int window = start + TargetChars;
            int cut = paragraph.LastIndexOfAny(['\n', '.', '。', '!', '?'], window, Math.Min(900, window - start));
            if (cut <= start) cut = paragraph.LastIndexOf(' ', window, Math.Min(900, window - start));
            if (cut <= start) cut = window;

            yield return paragraph[start..(cut + 1)].Trim();
            start = cut + 1;
        }
    }

    private static string TailForOverlap(string text)
    {
        if (text.Length <= OverlapChars) return string.Empty;

        string tail = text[^OverlapChars..];
        int firstSpace = tail.IndexOfAny([' ', '\n']);
        return firstSpace > 0 ? tail[(firstSpace + 1)..].TrimStart() : tail;
    }
}
