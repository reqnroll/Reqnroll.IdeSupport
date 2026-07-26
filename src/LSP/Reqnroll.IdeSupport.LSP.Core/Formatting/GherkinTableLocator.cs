using Gherkin.Ast;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Formatting;

/// <summary>
/// Locates a Gherkin table (a <c>DataTable</c> or Scenario Outline <c>Examples</c> block) by
/// cursor position — either from raw text (for the on-type table-alignment trigger, before a
/// fresh parse is available) or from the parsed AST (once one is). Used by completion/on-type
/// formatting callers; has no formatting logic of its own, so it's kept separate from
/// <see cref="GherkinDocumentFormatter"/>, which rewrites text rather than locating it.
/// </summary>
public static class GherkinTableLocator
{
    /// <summary>
    /// Scans <paramref name="lines"/> to find the zero-based line range of the Gherkin table
    /// that contains or is adjacent to <paramref name="cursorLine0Based"/>.
    /// Returns <see langword="null"/> when the cursor is not on a table row or on a blank
    /// line immediately following one (which occurs with the <c>\n</c> on-type trigger).
    /// </summary>
    public static (int Start, int End)? FindTableLineRange(string[] lines, int cursorLine0Based)
    {
        int anchor = -1;

        if (cursorLine0Based >= 0 && cursorLine0Based < lines.Length
            && IsTableRow(lines[cursorLine0Based]))
            anchor = cursorLine0Based;
        else if (cursorLine0Based > 0 && cursorLine0Based <= lines.Length
            && IsTableRow(lines[cursorLine0Based - 1]))
            anchor = cursorLine0Based - 1;

        if (anchor < 0) return null;

        int start = anchor;
        while (start > 0 && IsTableRow(lines[start - 1]))
            start--;

        int end = anchor;
        while (end < lines.Length - 1 && IsTableRow(lines[end + 1]))
            end++;

        return (start, end);
    }

    private static bool IsTableRow(string line) => line.TrimStart().StartsWith("|");

    /// <summary>
    /// Walks the parsed AST to find the <see cref="IHasRows"/> node (DataTable or Examples)
    /// whose first row starts at <paramref name="startLine0Based"/> (zero-based).
    /// Returns <see langword="null"/> when no table is found at that line.
    /// </summary>
    public static IHasRows? FindTableAtLine(DeveroomGherkinDocument doc, int startLine0Based)
    {
        if (doc?.Feature == null) return null;
        var targetLine1Based = startLine0Based + 1;
        return FindTableInChildren(doc.Feature.Children, targetLine1Based);
    }

    private static IHasRows? FindTableInChildren(IEnumerable<IHasLocation> children, int targetLine1Based)
    {
        foreach (var child in children)
        {
            if (child is IHasSteps hasSteps)
                foreach (var step in hasSteps.Steps)
                    if (step.Argument is DataTable dt &&
                        dt.Rows.Any(r => r.Location.Line == targetLine1Based))
                        return dt;

            if (child is ScenarioOutline outline)
                foreach (var ex in outline.Examples)
                    if (ex is IHasRows exRows &&
                        exRows.Rows.Any(r => r.Location.Line == targetLine1Based))
                        return exRows;

            if (child is Rule rule)
            {
                var result = FindTableInChildren(rule.Children, targetLine1Based);
                if (result != null) return result;
            }
        }
        return null;
    }
}
