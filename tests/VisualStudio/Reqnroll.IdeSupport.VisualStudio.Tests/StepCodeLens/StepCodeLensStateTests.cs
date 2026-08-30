using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;

namespace Reqnroll.VisualStudio.Tests.StepCodeLens;

/// <summary>
/// Unit tests for <see cref="StepCodeLensState"/>: the method-start-line bookkeeping (the data
/// <see cref="StepCodeLens"/> uses to bound its attribute-lookback window — see
/// <see cref="StepCodeLensState.GetNextMethodLine"/>), and the lens-tracking/invalidation registry
/// (<c>RegisterLens</c>/<c>InvalidateLensesForFile</c>).
/// </summary>
/// <remarks>
/// The invalidation half was previously excluded here as needing a real <see cref="StepCodeLens"/>
/// and a VS host. Issue #400 generalised the registry to the <c>IInvalidatableLens</c> interface so
/// <c>HookMatchCountCodeLens</c> could participate, which incidentally made the registry's own
/// bookkeeping fakeable. Only <c>InvalidateLabel()</c> is exercised — the SDK's
/// <c>CodeLens.Invalidate()</c> call inside the real implementations still needs a VS host.
/// </remarks>
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

    // ── Lens invalidation registry (issue #400) ───────────────────────────────
    //
    // Testable since the registry was generalised from the sealed, VS-host-bound StepCodeLens to
    // the IInvalidatableLens interface — the very change that let HookMatchCountCodeLens
    // participate. Only InvalidateLabel() is exercised here; the SDK's CodeLens.Invalidate() call
    // behind the real implementations still needs a VS host.

    private sealed class FakeLens : IInvalidatableLens
    {
        public int InvalidateCount { get; private set; }
        public void InvalidateLabel() => InvalidateCount++;
    }

    private const string OtherFileUri = "file:///c:/w/OtherSteps.cs";

    [Fact]
    public void Invalidating_a_file_invalidates_the_lenses_registered_for_it()
    {
        var state = new StepCodeLensState();
        var lens  = new FakeLens();
        state.RegisterLens(lens, FileUri);

        state.InvalidateLensesForFile(FileUri);

        lens.InvalidateCount.Should().Be(1);
    }

    [Fact]
    public void Every_lens_registered_for_a_file_is_invalidated()
    {
        // A .cs file has one lens per method, plus — since #400 — a second lens kind on the same
        // file from HookMatchCountCodeLensProvider.
        var state   = new StepCodeLensState();
        var lenses  = new[] { new FakeLens(), new FakeLens(), new FakeLens() };
        foreach (var lens in lenses)
            state.RegisterLens(lens, FileUri);

        state.InvalidateLensesForFile(FileUri);

        lenses.Should().OnlyContain(l => l.InvalidateCount == 1);
    }

    [Fact]
    public void Different_lens_implementations_registered_for_one_file_are_all_invalidated()
    {
        // The regression #400 was actually about: the registry used to be typed to StepCodeLens, so
        // a second implementation (HookMatchCountCodeLens) could not participate at all.
        var state      = new StepCodeLensState();
        var stepLens   = new FakeLens();
        var otherLens  = new AlternateFakeLens();
        state.RegisterLens(stepLens, FileUri);
        state.RegisterLens(otherLens, FileUri);

        state.InvalidateLensesForFile(FileUri);

        stepLens.InvalidateCount.Should().Be(1);
        otherLens.InvalidateCount.Should().Be(1);
    }

    private sealed class AlternateFakeLens : IInvalidatableLens
    {
        public int InvalidateCount { get; private set; }
        public void InvalidateLabel() => InvalidateCount++;
    }

    [Fact]
    public void Invalidating_one_file_does_not_invalidate_another_files_lenses()
    {
        var state = new StepCodeLensState();
        var mine  = new FakeLens();
        var other = new FakeLens();
        state.RegisterLens(mine,  FileUri);
        state.RegisterLens(other, OtherFileUri);

        state.InvalidateLensesForFile(FileUri);

        mine.InvalidateCount.Should().Be(1);
        other.InvalidateCount.Should().Be(0);
    }

    [Fact]
    public void An_unregistered_lens_is_no_longer_invalidated()
    {
        var state = new StepCodeLensState();
        var lens  = new FakeLens();
        state.RegisterLens(lens, FileUri);
        state.UnregisterLens(lens, FileUri);

        state.InvalidateLensesForFile(FileUri);

        lens.InvalidateCount.Should().Be(0);
    }

    [Fact]
    public void Unregistering_one_lens_leaves_its_siblings_registered()
    {
        // Disposing a single lens (its method was deleted) must not silently stop the rest of the
        // file's lenses refreshing.
        var state    = new StepCodeLensState();
        var disposed = new FakeLens();
        var survivor = new FakeLens();
        state.RegisterLens(disposed, FileUri);
        state.RegisterLens(survivor, FileUri);

        state.UnregisterLens(disposed, FileUri);
        state.InvalidateLensesForFile(FileUri);

        disposed.InvalidateCount.Should().Be(0);
        survivor.InvalidateCount.Should().Be(1);
    }

    [Fact]
    public void InvalidateAllTrackedLenses_reaches_every_file()
    {
        // The full-replacement (rebuild) path, and — since #343 — the incremental path too.
        var state = new StepCodeLensState();
        var mine  = new FakeLens();
        var other = new FakeLens();
        state.RegisterLens(mine,  FileUri);
        state.RegisterLens(other, OtherFileUri);

        state.InvalidateAllTrackedLenses();

        mine.InvalidateCount.Should().Be(1);
        other.InvalidateCount.Should().Be(1);
    }

    [Fact]
    public void Repeated_invalidation_invalidates_each_time()
    {
        // Registration must survive an invalidation — the alive-set rebuild inside
        // InvalidateLensesForFile must not drop still-live lenses.
        var state = new StepCodeLensState();
        var lens  = new FakeLens();
        state.RegisterLens(lens, FileUri);

        state.InvalidateLensesForFile(FileUri);
        state.InvalidateLensesForFile(FileUri);
        state.InvalidateAllTrackedLenses();

        lens.InvalidateCount.Should().Be(3);
    }

    [Fact]
    public void Invalidating_a_file_with_no_registered_lenses_does_not_throw()
    {
        var state = new StepCodeLensState();

        var act = () => state.InvalidateLensesForFile(FileUri);

        act.Should().NotThrow();
    }

    [Fact]
    public void Unregistering_a_lens_that_was_never_registered_does_not_throw()
    {
        var state = new StepCodeLensState();

        var act = () => state.UnregisterLens(new FakeLens(), FileUri);

        act.Should().NotThrow();
    }

    [Fact]
    public void File_uris_are_matched_case_insensitively()
    {
        // Windows paths reach the registry from several sources (VS code element context, LSP URIs)
        // whose casing does not always agree.
        var state = new StepCodeLensState();
        var lens  = new FakeLens();
        state.RegisterLens(lens, "file:///c:/w/Steps.cs");

        state.InvalidateLensesForFile("file:///C:/W/STEPS.CS");

        lens.InvalidateCount.Should().Be(1);
    }
}
