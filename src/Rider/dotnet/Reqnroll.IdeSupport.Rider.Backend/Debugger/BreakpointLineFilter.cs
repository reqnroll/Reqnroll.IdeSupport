using System;

namespace Reqnroll.IdeSupport.Rider.Backend.Debugger;

/// <summary>
/// Decides whether a breakpoint is worth offering on a <c>.feature</c> line, by refusing the lines that never have
/// generated code of their own.
/// <para>
/// This matters because Rider binds a line breakpoint on a line without code to the next line that has some: a
/// breakpoint on a tag line stops on the <c>Scenario:</c> line below it, and one on a table row stops on the next
/// step. Refused: blank lines, comments (<c>#</c>), tag lines (<c>@</c>), table rows (<c>|</c>, which also covers
/// <c>Examples</c> rows), and doc strings including their delimiters.
/// </para>
/// <para>
/// Deliberately keyword-free, so it needs no Gherkin dialect: free-form description lines and the <c>Rule:</c>,
/// <c>Background:</c> and <c>Examples:</c> header lines are still offered.
/// </para>
/// </summary>
public static class BreakpointLineFilter
{
    /// <summary>Whether zero-based line <paramref name="line"/> of a document with <paramref name="lineCount"/> lines is a candidate.</summary>
    /// <param name="getLine">Returns the text of a zero-based line, without its line break.</param>
    public static bool IsBreakpointCandidate(Func<int, string> getLine, int lineCount, int line)
    {
        if (line < 0 || line >= lineCount)
            return false;

        if (IsInDocString(getLine, line))
            return false;

        return IsBreakpointCandidate(getLine(line));
    }

    /// <summary>The single-line part of the decision; doc strings also need the preceding lines (see the other overload).</summary>
    public static bool IsBreakpointCandidate(string lineText)
    {
        if (string.IsNullOrWhiteSpace(lineText))
            return false;

        var trimmed = lineText.TrimStart();
        switch (trimmed[0])
        {
            case '#':
            case '@':
            case '|':
                return false;
        }

        return DocStringDelimiter(trimmed) == null;
    }

    /// <summary>
    /// Whether <paramref name="line"/> is a doc string delimiter or lies between two. Gherkin opens a doc string with
    /// <c>"""</c> or <c>```</c> and closes it with the same delimiter.
    /// </summary>
    private static bool IsInDocString(Func<int, string> getLine, int line)
    {
        string open = null;
        for (var i = 0; i < line; i++)
        {
            var delimiter = DocStringDelimiter((getLine(i) ?? string.Empty).TrimStart());
            if (open == null)
                open = delimiter;
            else if (delimiter == open)
                open = null;
        }

        return open != null || DocStringDelimiter((getLine(line) ?? string.Empty).TrimStart()) != null;
    }

    private static string DocStringDelimiter(string trimmedLine)
    {
        if (trimmedLine.StartsWith("\"\"\"", StringComparison.Ordinal))
            return "\"\"\"";
        if (trimmedLine.StartsWith("```", StringComparison.Ordinal))
            return "```";
        return null;
    }
}
