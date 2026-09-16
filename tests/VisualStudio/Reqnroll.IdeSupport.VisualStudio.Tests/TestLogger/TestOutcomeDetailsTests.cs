using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Reqnroll.IdeSupport.VisualStudio.TestLogger;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestLogger;

/// <summary>
/// Phase 2 of the implementation plan: the step trace carried in each result's stdout becomes per-row
/// failed-step information, flows through the OOP DTO, and renders as the Run CodeLens details table.
/// </summary>
public class TestOutcomeDetailsTests
{
    private const string Source = @"C:\repo\Specs\bin\Debug\net8.0\Specs.dll";
    private const string Type = "Specs.Features.CalculatorFeature";

    private const string FailingMiddleStepTrace =
        "TestContext Messages:\n" +
        "Given the first number is 1\n" +
        "-> done: CalculatorSteps.GivenTheFirstNumberIs(1) (0.0s)\n" +
        "When the calculation explodes\n" +
        "-> error: deliberate failure in the middle step (0.0s)\n" +
        "Then the result should be 2\n" +
        "-> skipped because of previous errors\n";

    private static TestResultRecord Result(string method, string display, TestOutcomeKind outcome, string? stdout, string? error = null)
        => new("run-1", Source, Type, method + "()", $"{Type}.{method}", display, outcome, 1234.5, error, null, stdout, false);

    [Fact]
    public void Store_parses_the_step_trace_and_exposes_the_failing_step()
    {
        var store = new TestOutcomeStore();
        store.Record(Result("AStepInTheMiddleFails", "A step in the middle fails", TestOutcomeKind.Failed, FailingMiddleStepTrace, "deliberate failure in the middle step"));

        var row = store.TryGet(Source, Type, "AStepInTheMiddleFails")!.Rows.Single();
        row.Steps.Should().HaveCount(3);
        row.FailedStep.Should().NotBeNull();
        row.FailedStep!.Index.Should().Be(1, "the middle step threw; the trace, unlike the stack trace, says so");
        row.FailedStep.StepText.Should().Be("When the calculation explodes");
    }

    [Fact]
    public void Rows_without_a_trace_have_no_steps_and_no_failed_step()
    {
        var store = new TestOutcomeStore();
        store.Record(Result("Plain", "Plain", TestOutcomeKind.Failed, stdout: null, error: "boom"));

        var row = store.TryGet(Source, Type, "Plain")!.Rows.Single();
        row.Steps.Should().BeEmpty();
        row.FailedStep.Should().BeNull();
    }

    [Fact]
    public void Callback_entry_carries_the_failed_step_per_row()
    {
        var store = new TestOutcomeStore();
        store.Record(Result("AddingRows", "Adding rows(1,2,3,2)", TestOutcomeKind.Passed,
            "Given the first number is 1\n-> done: S.G(1) (0.0s)\nThen the result should be 3\n-> done: S.T(3) (0.0s)\n"));
        store.Record(Result("AddingRows", "Adding rows(5,5,11,4)", TestOutcomeKind.Failed,
            "Given the first number is 5\n-> done: S.G(5) (0.0s)\nThen the result should be 11\n-> error: Assert.AreEqual failed. Expected:<11>. Actual:<10>.  (0.0s)\n",
            "Assert.AreEqual failed. Expected:<11>. Actual:<10>."));

        var entry = RunTestCodeLensCallbackListener.ToEntry(store.TryGet(Source, Type, "AddingRows")!);

        entry.Aggregate.Should().Be("Failed");
        entry.Rows.Should().HaveCount(2);
        var passed = entry.Rows.Single(r => r.Outcome == "Passed");
        passed.StepCount.Should().Be(2);
        passed.FailedStepIndex.Should().BeNull();
        passed.FailedStepText.Should().BeNull();
        var failed = entry.Rows.Single(r => r.Outcome == "Failed");
        failed.StepCount.Should().Be(2);
        failed.FailedStepIndex.Should().Be(1);
        failed.FailedStepText.Should().Be("Then the result should be 11");
        failed.FailedStepOutcome.Should().Be("Error");
        failed.ErrorMessage.Should().StartWith("Assert.AreEqual failed");
        failed.DurationMs.Should().Be(1234.5);
    }

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
}
