using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;

namespace Reqnroll.VisualStudio.Tests.StepCodeLens;

/// <summary>
/// Unit tests for <see cref="StepCodeLensState"/>'s method-start-line bookkeeping (the data
/// <see cref="StepCodeLens"/> uses to bound its attribute-lookback window — see
/// <see cref="StepCodeLensState.GetNextMethodLine"/>). Excludes the lens-tracking/invalidation
/// half of the class (<c>RegisterLens</c>/<c>InvalidateLensesForFile</c>), which needs a real
/// <see cref="StepCodeLens"/>/VS host and belongs in an integration smoke test.
/// </summary>
public class StepCodeLensStateTests
{
    private const string FileUri = "file:///c:/w/Steps.cs";

    [Fact]
    public void GetNextMethodLine_returns_minus_one_when_the_file_has_no_registered_lines()
    {
        var state = new StepCodeLensState();

        state.GetNextMethodLine(FileUri, currentStartLine: 5).Should().Be(-1);
    }

    [Fact]
    public void GetNextMethodLine_returns_the_smallest_registered_line_greater_than_current()
    {
        var state = new StepCodeLensState();
        state.RegisterMethodLine(FileUri, 10);
        state.RegisterMethodLine(FileUri, 20);
        state.RegisterMethodLine(FileUri, 30);

        state.GetNextMethodLine(FileUri, currentStartLine: 10).Should().Be(20);
    }

    [Fact]
    public void GetNextMethodLine_returns_minus_one_when_no_registered_line_is_greater()
    {
        var state = new StepCodeLensState();
        state.RegisterMethodLine(FileUri, 10);

        state.GetNextMethodLine(FileUri, currentStartLine: 10).Should().Be(-1);
    }

    [Fact]
    public void UnregisterMethodLine_removes_the_line_so_it_no_longer_bounds_a_lookback_window()
    {
        var state = new StepCodeLensState();
        state.RegisterMethodLine(FileUri, 10);
        state.RegisterMethodLine(FileUri, 20);

        state.UnregisterMethodLine(FileUri, 20);

        state.GetNextMethodLine(FileUri, currentStartLine: 10).Should().Be(-1);
    }

    [Fact]
    public void A_stale_line_left_over_from_a_deleted_or_moved_method_does_not_leak_into_later_lookups()
    {
        // Reproduces issue #321: a method at line 20 is deleted (its StepCodeLens disposes and
        // unregisters line 20), then a new method is registered at line 15 in the edited layout.
        // Without UnregisterMethodLine, the stale "20" would remain in the bag forever and could
        // still be picked as the upper bound for line 10's lookback window, even though it no
        // longer corresponds to any live method.
        var state = new StepCodeLensState();
        state.RegisterMethodLine(FileUri, 10);
        state.RegisterMethodLine(FileUri, 20);

        state.UnregisterMethodLine(FileUri, 20); // method at 20 deleted
        state.RegisterMethodLine(FileUri, 15);   // a new method registered in its place

        state.GetNextMethodLine(FileUri, currentStartLine: 10).Should().Be(15,
            "the stale line from the deleted method must not still bound the window");
    }

    [Fact]
    public void Unregistering_a_line_for_one_file_does_not_affect_another_file()
    {
        const string otherFileUri = "file:///c:/w/OtherSteps.cs";
        var state = new StepCodeLensState();
        state.RegisterMethodLine(FileUri, 10);
        state.RegisterMethodLine(otherFileUri, 10);

        state.UnregisterMethodLine(FileUri, 10);

        state.GetNextMethodLine(otherFileUri, currentStartLine: 5).Should().Be(10);
    }

    [Fact]
    public void Unregistering_a_line_that_was_never_registered_does_not_throw()
    {
        var state = new StepCodeLensState();

        var act = () => state.UnregisterMethodLine(FileUri, 42);

        act.Should().NotThrow();
    }
}
