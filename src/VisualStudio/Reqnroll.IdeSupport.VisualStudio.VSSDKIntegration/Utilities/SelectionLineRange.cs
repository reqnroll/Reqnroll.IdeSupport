namespace Reqnroll.IdeSupport.VisualStudio.Utilities;

/// <summary>
/// Shared selection line-range math for command filters that translate an editor selection into a
/// server-side line range — Comment/Uncomment toggle (<c>CommentToggleCommandFilter</c>) and Format
/// Document/Selection (<c>FormatDocumentCommandFilter</c>). Each caller computes the selection's
/// start/end line from its own editor API's position types, then calls
/// <see cref="AdjustEndLineForWholeLineSelection"/> to correct the shared off-by-one hazard.
/// </summary>
public static class SelectionLineRange
{
    /// <summary>
    /// Corrects <paramref name="endLine"/> for the common whole-line selection shape where the
    /// selection's end position sits at character 0 of the line <em>after</em> the last line the
    /// user actually selected (e.g. drag-selecting from the start of one line to the start of a
    /// later line, or Shift+Down across whole lines) — a shape produced by ordinary editor
    /// selection, not an edge case. Without this correction, that trailing line gets
    /// commented/uncommented or formatted too, even though the user never selected it.
    /// </summary>
    /// <param name="startLine">The selection's start line (0-based).</param>
    /// <param name="endLine">The selection's end line (0-based), before correction.</param>
    /// <param name="endPositionIsAtLineStart">
    /// Whether the selection's end position is exactly at the start of <paramref name="endLine"/>
    /// (character/column 0) — the caller determines this from its own editor API's position type.
    /// </param>
    /// <returns>
    /// <paramref name="endLine"/> unchanged, or <paramref name="endLine"/> - 1 when the selection
    /// spans more than one line and its end sits at that trailing line's very start.
    /// </returns>
    public static int AdjustEndLineForWholeLineSelection(int startLine, int endLine, bool endPositionIsAtLineStart)
        => endLine > startLine && endPositionIsAtLineStart ? endLine - 1 : endLine;
}
