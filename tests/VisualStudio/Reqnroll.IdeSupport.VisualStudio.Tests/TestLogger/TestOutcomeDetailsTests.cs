using System;
using System.Linq;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestLogger;

/// <summary>
/// Phase 2 of the implementation plan: the step trace carried in each result's stdout becomes per-row
/// failed-step information and renders as the Run CodeLens details table. The trace-parsing and
/// store/handler side of this (previously covered here via a local <c>TestOutcomeStore</c> and
/// <c>RunTestCodeLensCallbackListener.ToEntry</c>) moved to
/// <c>Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestOutcomes.GetTestOutcomeHandlerTests</c> along
/// with the store and handler themselves (LSP-server outcome pipeline refactor). What's left here is
/// purely the OOP DTO's rendering into the Details popup table, which stays VS-side.
/// </summary>
public class TestOutcomeDetailsTests
{
    [Fact]
    public void Details_table_has_one_entry_per_row_with_the_failed_step_described()
    {
        var entry = new RunTestOutcomeEntry("Failed", new[]
        {
            new RunTestOutcomeRow("Adding rows(1,2,3,2)", "Passed", 42, null, StepCount: 4),
            new RunTestOutcomeRow("Adding rows(5,5,11,4)", "Failed", 1500, "Assert.AreEqual failed.", StepCount: 4, FailedStepIndex: 3, FailedStepText: "Then the result should be 11", FailedStepOutcome: "Error"),
            new RunTestOutcomeRow("Unbound", "Failed", 3, "No matching step definition", StepCount: 2, FailedStepIndex: 0, FailedStepText: "Given an unbound step", FailedStepOutcome: "Undefined"),
        }, DateTime.UtcNow);

        var (headers, entries) = RunTestCodeLensDataPoint.BuildOutcomeTable(entry);

        headers.Select(h => h.DisplayName).Should().Equal("Example", "Outcome", "Duration", "Failed step");
        headers.Sum(h => h.Width).Should().BeApproximately(1.0, 0.001);
        entries.Should().HaveCount(3);

        var texts = entries.Select(e => e.Fields.Select(f => f.Text).ToArray()).ToList();
        texts[0].Should().Equal("Adding rows(1,2,3,2)", "Passed", "42 ms", "");
        texts[1].Should().Equal("Adding rows(5,5,11,4)", "Failed", "1.5 s", "Then the result should be 11 (threw, step 4 of 4)");
        texts[2].Should().Equal("Unbound", "Failed", "3 ms", "Given an unbound step (undefined step, step 1 of 2)");
        entries[1].Tooltip.Should().Be("Assert.AreEqual failed.");
        entries[0].Tooltip.Should().Be("Adding rows(1,2,3,2)", "no error → the row name is the tooltip");
    }

    [Fact]
    public void Details_table_is_empty_without_an_outcome()
    {
        RunTestCodeLensDataPoint.BuildOutcomeTable(null).Entries.Should().BeEmpty();
        RunTestCodeLensDataPoint.BuildOutcomeTable(new RunTestOutcomeEntry("None", Array.Empty<RunTestOutcomeRow>(), DateTime.UtcNow)).Headers.Should().BeEmpty();
    }

    // ── BuildTooltip: the lens's own inline hover (previously always unset, falling back to VS's
    // generic keybinding hint, e.g. "Alt+1" — the failure detail only ever reached the Details popup) ──

    [Fact]
    public void Tooltip_is_null_without_an_outcome()
    {
        RunTestCodeLensDataPoint.BuildTooltip(null).Should().BeNull();
        RunTestCodeLensDataPoint.BuildTooltip(new RunTestOutcomeEntry("None", Array.Empty<RunTestOutcomeRow>(), DateTime.UtcNow)).Should().BeNull();
    }

    [Fact]
    public void Tooltip_describes_the_first_failing_row_with_its_step_and_error_message()
    {
        var entry = new RunTestOutcomeEntry("Failed", new[]
        {
            new RunTestOutcomeRow("Adding rows(1,2,3,2)", "Passed", 42, null, StepCount: 4),
            new RunTestOutcomeRow("Adding rows(5,5,11,4)", "Failed", 1500, "Assert.AreEqual failed.", StepCount: 4, FailedStepIndex: 3, FailedStepText: "Then the result should be 11", FailedStepOutcome: "Error"),
        }, DateTime.UtcNow);

        RunTestCodeLensDataPoint.BuildTooltip(entry).Should()
            .Be("Failed: Then the result should be 11 (threw) — Assert.AreEqual failed.");
    }

    [Fact]
    public void Tooltip_omits_the_error_message_segment_when_the_row_has_none()
    {
        var entry = new RunTestOutcomeEntry("Failed", new[]
        {
            new RunTestOutcomeRow("Unbound", "Failed", 3, null, StepCount: 2, FailedStepIndex: 0, FailedStepText: "Given an unbound step", FailedStepOutcome: "Undefined"),
        }, DateTime.UtcNow);

        RunTestCodeLensDataPoint.BuildTooltip(entry).Should().Be("Failed: Given an unbound step (undefined step)");
    }

    [Fact]
    public void Tooltip_falls_back_to_the_aggregate_when_no_row_is_failed()
    {
        var entry = new RunTestOutcomeEntry("Passed", new[]
        {
            new RunTestOutcomeRow("Adding rows(1,2,3,2)", "Passed", 42, null, StepCount: 4),
        }, DateTime.UtcNow);

        RunTestCodeLensDataPoint.BuildTooltip(entry).Should().Be("Passed");
    }

    // ── AggregateOutcomes: the Feature/Rule "Run Scenarios" glyph (issue #744) combines every
    // scenario's own outcome the same worst-wins way Test Explorer aggregates a hierarchical node —
    // regression coverage for issue #789 live testing, where only the first scenario's outcome ever
    // reached the glyph and a failing later scenario still rendered green ──

    private static RunTestOutcomeEntry Outcome(string aggregate) => new(aggregate, Array.Empty<RunTestOutcomeRow>(), DateTime.UtcNow);

    [Fact]
    public void Aggregate_is_failed_when_any_scenario_failed_even_if_the_first_one_passed()
    {
        var result = RunTestCodeLensDataPoint.AggregateOutcomes(new[] { Outcome("Passed"), Outcome("Failed"), Outcome("Passed") });

        result!.Aggregate.Should().Be("Failed");
    }

    [Fact]
    public void Aggregate_is_passed_only_when_every_scenario_passed()
    {
        var result = RunTestCodeLensDataPoint.AggregateOutcomes(new[] { Outcome("Passed"), Outcome("Passed") });

        result!.Aggregate.Should().Be("Passed");
    }

    [Fact]
    public void Aggregate_is_skipped_when_no_scenario_failed_but_at_least_one_was_skipped()
    {
        var result = RunTestCodeLensDataPoint.AggregateOutcomes(new[] { Outcome("Passed"), Outcome("Skipped") });

        result!.Aggregate.Should().Be("Skipped");
    }

    [Fact]
    public void Aggregate_is_null_when_any_scenario_has_no_resolved_outcome_yet()
    {
        // A scenario that hasn't run this session (or whose store entry was stale) resolves to null —
        // the glyph should say nothing rather than report a false green or red on partial data.
        var result = RunTestCodeLensDataPoint.AggregateOutcomes(new[] { Outcome("Passed"), null });

        result.Should().BeNull();
    }

    [Fact]
    public void Aggregate_prefers_a_running_entry_over_any_completed_outcome()
    {
        var running = new RunTestOutcomeEntry("None", Array.Empty<RunTestOutcomeRow>(), DateTime.UtcNow, IsRunning: true);

        var result = RunTestCodeLensDataPoint.AggregateOutcomes(new[] { Outcome("Failed"), running });

        result.Should().BeSameAs(running);
    }

    [Fact]
    public void Aggregate_is_null_for_an_empty_method_list()
    {
        RunTestCodeLensDataPoint.AggregateOutcomes(Array.Empty<RunTestOutcomeEntry?>()).Should().BeNull();
    }

    [Fact]
    public void Aggregate_combines_rows_from_every_method()
    {
        var first = new RunTestOutcomeEntry("Passed", new[] { new RunTestOutcomeRow("Scenario A", "Passed", 10, null) }, DateTime.UtcNow);
        var second = new RunTestOutcomeEntry("Failed", new[] { new RunTestOutcomeRow("Scenario B", "Failed", 20, "boom") }, DateTime.UtcNow);

        var result = RunTestCodeLensDataPoint.AggregateOutcomes(new[] { first, second });

        result!.Rows.Select(r => r.DisplayName).Should().Equal("Scenario A", "Scenario B");
    }
}
