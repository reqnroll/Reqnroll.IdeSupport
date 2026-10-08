using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.NavigationBar;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.NavigationBar;

/// <summary>
/// Covers recognising Git Changes compare-window panes so they stand down instead of retrying
/// to attach a drop-down bar they can never have (issue #907).
/// </summary>
public class DiffViewDetectorTests
{
    [Theory]
    [InlineData("DIFF", "LEFTDIFF", "ANALYZABLE", "DOCUMENT", "EDITABLE")]
    [InlineData("DIFF", "INLINEDIFF", "DOCUMENT", "PRIMARYDOCUMENT")]
    [InlineData("diff", "DOCUMENT")]
    public void Difference_viewer_panes_are_detected(params string[] roles)
    {
        DiffViewDetector.IsDiffView(roles).Should().BeTrue();
    }

    [Fact]
    public void A_regular_editor_is_not_a_diff_view()
    {
        DiffViewDetector.IsDiffView(new[] { "DOCUMENT", "EDITABLE", "INTERACTIVE", "PRIMARYDOCUMENT" })
            .Should().BeFalse();
    }
}
