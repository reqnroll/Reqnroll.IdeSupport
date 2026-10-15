using Reqnroll.IdeSupport.VisualStudio;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Tests;

/// <summary>
/// Unit tests for <see cref="SelectionLineRange.AdjustEndLineForWholeLineSelection"/>, the shared
/// line-range correction used by both the Comment/Uncomment toggle filter and the Format
/// Document/Selection filter. Each caller determines <c>endPositionIsAtLineStart</c> from its own
/// editor API's position type; this class tests the pure decision logic in isolation from either
/// editor API.
/// </summary>
public class SelectionLineRangeTests
{
    [Fact]
    public void Does_not_adjust_a_single_line_selection()
    {
        // startLine == endLine: no adjustment applies regardless of the end-position flag.
        SelectionLineRange.AdjustEndLineForWholeLineSelection(
            startLine: 5, endLine: 5, endPositionIsAtLineStart: true).Should().Be(5);
    }

    [Fact]
    public void Excludes_the_trailing_line_when_the_selection_end_sits_at_that_lines_start()
    {
        // Reproduces issue #322: dragging from the start of line 5 to the start of line 7
        // (visually selecting lines 5-6) must exclude line 7.
        SelectionLineRange.AdjustEndLineForWholeLineSelection(
            startLine: 5, endLine: 7, endPositionIsAtLineStart: true).Should().Be(6);
    }

    [Fact]
    public void Keeps_the_trailing_line_when_the_selection_end_is_mid_line()
    {
        // Selection ends partway through the last line (e.g. selecting to a specific column) --
        // that line was genuinely selected and must be included.
        SelectionLineRange.AdjustEndLineForWholeLineSelection(
            startLine: 5, endLine: 7, endPositionIsAtLineStart: false).Should().Be(7);
    }

    [Fact]
    public void Does_not_adjust_below_the_start_line()
    {
        // A two-line selection (start 5, end 6) whose end sits at line 6's start still has a
        // real line 5 to include -- adjusting down must never cross below startLine.
        SelectionLineRange.AdjustEndLineForWholeLineSelection(
            startLine: 5, endLine: 6, endPositionIsAtLineStart: true).Should().Be(5);
    }
}
