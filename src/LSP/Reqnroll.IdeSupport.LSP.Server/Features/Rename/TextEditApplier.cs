using System.Text;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Applies LSP <see cref="TextEdit"/>s to a document's text, the way a client applies a
/// <see cref="WorkspaceEdit"/> to a document.
/// </summary>
internal static class TextEditApplier
{
    /// <summary>
    /// Returns <paramref name="text"/> with <paramref name="edits"/> applied, or <see langword="null"/>
    /// when an edit names a line the text does not have (it was computed against different content),
    /// an edit's end comes before its start, or two edits overlap.
    /// </summary>
    /// <remarks>
    /// Positions are zero-based lines and UTF-16 code-unit offsets, and <c>\n</c>, <c>\r\n</c> and
    /// <c>\r</c> all end a line, as the LSP specification defines them. All edits refer to the
    /// original text, so they are applied from the end of the document backwards.
    /// </remarks>
    public static string? Apply(string text, IReadOnlyCollection<TextEdit> edits)
    {
        var lineStarts = ComputeLineStarts(text);

        var resolved = new List<(int Start, int End, string NewText, int Index)>(edits.Count);
        foreach (var edit in edits)
        {
            var start = ToOffset(text, lineStarts, edit.Range.Start);
            var end   = ToOffset(text, lineStarts, edit.Range.End);
            if (start is null || end is null || end < start)
                return null;
            resolved.Add((start.Value, end.Value, edit.NewText ?? string.Empty, resolved.Count));
        }

        // Back to front. Edits at the same position are applied in reverse array order, so that
        // several inserts at one position end up in array order, as the specification requires.
        resolved.Sort((a, b) =>
            b.Start != a.Start ? b.Start.CompareTo(a.Start)
          : b.End   != a.End   ? b.End.CompareTo(a.End)
          :                      b.Index.CompareTo(a.Index));

        var result = new StringBuilder(text);
        var previousStart = int.MaxValue;
        foreach (var (start, end, newText, _) in resolved)
        {
            if (end > previousStart)
                return null; // overlaps the edit after it

            result.Remove(start, end - start).Insert(start, newText);
            previousStart = start;
        }

        return result.ToString();
    }

    private static List<int> ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts;
    }

    private static int? ToOffset(string text, List<int> lineStarts, Position position)
    {
        if (position.Line < 0 || position.Line >= lineStarts.Count || position.Character < 0)
            return null;

        var lineStart = lineStarts[position.Line];
        var lineEnd   = position.Line + 1 < lineStarts.Count ? lineStarts[position.Line + 1] : text.Length;

        var contentEnd = lineEnd;
        while (contentEnd > lineStart && (text[contentEnd - 1] == '\n' || text[contentEnd - 1] == '\r'))
            contentEnd--;

        // The specification: a character past the end of the line defaults back to the line length.
        return Math.Min(lineStart + position.Character, contentEnd);
    }
}
