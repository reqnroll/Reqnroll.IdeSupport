using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.TestLogger;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestLogger;

/// <summary>
/// Aggregation and identity rules of <see cref="TestOutcomeStore"/> — the in-proc replacement for
/// reading outcomes through <c>RunTestOutcomeBridge</c>. Row shapes mirror what the bundled logger
/// actually sent in the 2026-09-16 live runs (MSTest: signature-bearing <c>ManagedMethod</c>, per-row
/// <c>DisplayName</c>).
/// </summary>
public class TestOutcomeStoreTests
{
    private const string Source = @"C:\repo\Specs\bin\Debug\net8.0\Specs.dll";
    private const string Type = "Specs.Features.PriceCalculationFeature";

    private static TestResultRecord Result(
        string method = "So23",
        string display = "so23(Electric guitar,1,180.0,2)",
        TestOutcomeKind outcome = TestOutcomeKind.Passed,
        string source = Source,
        string? managedType = Type,
        string? managedMethod = "So23(System.String,System.String,System.String,System.String,System.String[])",
        string runId = "run-1")
        => new(runId, source, managedType, managedMethod, $"{Type}.{method}", display, outcome, 12.5, null, null, null, false);

    [Fact]
    public void Key_prefers_managed_identity_and_strips_the_method_signature()
    {
        var key = TestOutcomeKey.From(Source, Type, "So23(System.String,System.String[])", $"{Type}.So23")!;

        key.TypeFullName.Should().Be(Type);
        key.MethodName.Should().Be("So23");
    }

    [Fact]
    public void Key_falls_back_to_the_fqn_when_managed_identity_is_absent_and_ignores_nunit_row_arguments()
    {
        var key = TestOutcomeKey.From(Source, null, null, $"{Type}.AddingNumbers(\"50\",\"5\")")!;

        key.TypeFullName.Should().Be(Type);
        key.MethodName.Should().Be("AddingNumbers");
    }

    [Fact]
    public void Key_is_null_without_a_source_or_any_identity()
    {
        TestOutcomeKey.From(null, Type, "M", "T.M").Should().BeNull();
        TestOutcomeKey.From(Source, null, null, null).Should().BeNull();
        TestOutcomeKey.From(Source, null, null, "NoDot").Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\repo\Specs\bin\Debug\net8.0\Specs.dll")]
    [InlineData(@"c:\REPO\specs\BIN\debug\net8.0\specs.DLL")]
    [InlineData(@"C:/repo/Specs/bin/Debug/net8.0/Specs.dll")]
    [InlineData(@"C:\repo\Specs\bin\Debug\..\Debug\net8.0\Specs.dll")]
    public void Lookup_matches_the_recorded_source_regardless_of_case_separators_or_dot_segments(string lookupSource)
    {
        var store = new TestOutcomeStore();
        store.Record(Result());

        store.TryGet(lookupSource, Type, "So23").Should().NotBeNull();
    }

    [Fact]
    public void Lookup_by_codelens_identity_tolerates_a_signature_on_either_side()
    {
        var store = new TestOutcomeStore();
        store.Record(Result());

        store.TryGet(Source, Type, "So23").Should().NotBeNull("TestMethodIdentifier carries no signature");
        store.TryGet(Source, Type, "So23(System.String)").Should().NotBeNull();
        store.TryGet(Source, Type, "Other").Should().BeNull();
        store.TryGet(Source, Type + "2", "So23").Should().BeNull();
    }

    [Fact]
    public void Outline_rows_aggregate_under_one_method_with_one_entry_per_display_name()
    {
        var store = new TestOutcomeStore();
        store.Record(Result(display: "so23(Electric guitar,1,180.0,2)"));
        store.Record(Result(display: "so23(Guitar pick,10,15.0,3)", outcome: TestOutcomeKind.Failed));

        var outcome = store.TryGet(Source, Type, "So23")!;
        outcome.Rows.Should().HaveCount(2);
        outcome.Aggregate.Should().Be(TestOutcomeKind.Failed);
        outcome.FailedRowCount.Should().Be(1);
    }

    [Fact]
    public void Rerunning_one_row_updates_that_row_and_keeps_the_others()
    {
        var store = new TestOutcomeStore();
        store.Record(Result(display: "row A", outcome: TestOutcomeKind.Failed));
        store.Record(Result(display: "row B", outcome: TestOutcomeKind.Passed));

        store.Record(Result(display: "row A", outcome: TestOutcomeKind.Passed, runId: "run-2"));

        var outcome = store.TryGet(Source, Type, "So23")!;
        outcome.Rows.Should().HaveCount(2);
        outcome.Rows.Single(r => r.DisplayName == "row A").RunId.Should().Be("run-2");
        outcome.Aggregate.Should().Be(TestOutcomeKind.Passed);
    }

    [Theory]
    [InlineData(new[] { TestOutcomeKind.Passed, TestOutcomeKind.Failed, TestOutcomeKind.Skipped }, TestOutcomeKind.Failed)]
    [InlineData(new[] { TestOutcomeKind.Passed, TestOutcomeKind.Skipped }, TestOutcomeKind.Passed)]
    [InlineData(new[] { TestOutcomeKind.Skipped, TestOutcomeKind.Skipped }, TestOutcomeKind.Skipped)]
    [InlineData(new[] { TestOutcomeKind.NotFound }, TestOutcomeKind.NotFound)]
    [InlineData(new[] { TestOutcomeKind.None }, TestOutcomeKind.None)]
    [InlineData(new TestOutcomeKind[0], TestOutcomeKind.None)]
    public void Aggregate_is_failed_over_passed_over_skipped(TestOutcomeKind[] rows, TestOutcomeKind expected)
    {
        var rowOutcomes = rows.Select((o, i) => new RowOutcome($"row {i}", o, 0, null, null, null, false, "r", DateTime.UtcNow, Array.Empty<Reqnroll.IdeSupport.Common.TestOutcomes.StepTraceEntry>()));

        TestOutcomeStore.Aggregate(rowOutcomes).Should().Be(expected);
    }

    [Theory]
    [InlineData("Passed", TestOutcomeKind.Passed)]
    [InlineData("failed", TestOutcomeKind.Failed)]
    [InlineData("Skipped", TestOutcomeKind.Skipped)]
    [InlineData("NotFound", TestOutcomeKind.NotFound)]
    [InlineData("None", TestOutcomeKind.None)]
    [InlineData("Running", TestOutcomeKind.None)]
    [InlineData("Bogus", TestOutcomeKind.None)]
    [InlineData(null, TestOutcomeKind.None)]
    public void ParseOutcome_maps_vstest_names_and_degrades_unknowns_to_none(string? name, TestOutcomeKind expected)
        => TestOutcomeStore.ParseOutcome(name).Should().Be(expected);

    [Fact]
    public void Record_bumps_the_revision_and_raises_Changed_with_the_affected_key()
    {
        var store = new TestOutcomeStore();
        var events = new List<TestOutcomesChangedEventArgs>();
        store.Changed += (_, e) => events.Add(e);
        var before = store.Revision;

        var key = store.Record(Result())!;

        store.Revision.Should().BeGreaterThan(before);
        events.Should().ContainSingle();
        events[0].Keys.Should().ContainSingle().Which.Should().Be(key);
        events[0].Revision.Should().Be(store.Revision);
    }

    [Fact]
    public void Record_without_identity_is_ignored_and_raises_nothing()
    {
        var store = new TestOutcomeStore();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Record(Result(source: "", managedType: null, managedMethod: null) with { FullyQualifiedName = "" }).Should().BeNull();

        raised.Should().Be(0);
        store.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Clear_forgets_everything_and_reports_the_keys_it_dropped()
    {
        var store = new TestOutcomeStore();
        store.Record(Result(method: "A", managedMethod: "A()"));
        store.Record(Result(method: "B", managedMethod: "B()"));
        TestOutcomesChangedEventArgs? last = null;
        store.Changed += (_, e) => last = e;

        store.Clear();

        store.Snapshot().Should().BeEmpty();
        last!.Keys.Should().HaveCount(2);
    }

    [Fact]
    public void MarkRunning_flags_the_method_and_keeps_its_last_known_rows()
    {
        var store = new TestOutcomeStore();
        var key = store.Record(Result(outcome: TestOutcomeKind.Failed))!;

        store.MarkRunning("run-2", new[] { key });

        var outcome = store.TryGet(key)!;
        outcome.IsRunning.Should().BeTrue();
        outcome.Aggregate.Should().Be(TestOutcomeKind.Failed, "the last outcome stays visible in the details while running");
        outcome.Rows.Should().ContainSingle();
    }

    [Fact]
    public void CompleteRun_clears_running_for_that_run_only_and_forgets_methods_that_never_reported()
    {
        var store = new TestOutcomeStore();
        var reported = TestOutcomeKey.ForLookup(Source, Type, "Reported");
        var silent = TestOutcomeKey.ForLookup(Source, Type, "Silent");
        var other = TestOutcomeKey.ForLookup(Source, Type, "OtherRun");
        store.MarkRunning("run-2", new[] { reported, silent });
        store.MarkRunning("run-3", new[] { other });
        store.Record(Result(method: "Reported", managedMethod: "Reported()", runId: "run-2"));

        store.TryGet(reported)!.IsRunning.Should().BeTrue("a result mid-run doesn't end the run");
        var affected = store.CompleteRun("run-2");

        affected.Should().BeEquivalentTo(new[] { reported, silent });
        store.TryGet(reported)!.IsRunning.Should().BeFalse();
        store.TryGet(silent).Should().BeNull("nothing was ever recorded for it");
        store.TryGet(other)!.IsRunning.Should().BeTrue("run-3 is still going");
        store.Snapshot().Select(m => m.Key.MethodName).Should().Equal("Reported");
    }

    [Fact]
    public void A_result_from_a_different_run_supersedes_a_stale_running_mark()
    {
        var store = new TestOutcomeStore();
        var key = TestOutcomeKey.ForLookup(Source, Type, "So23");
        store.MarkRunning("run-crashed", new[] { key });

        store.Record(Result(runId: "run-later"));

        store.TryGet(key)!.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Snapshots_are_immutable_copies()
    {
        var store = new TestOutcomeStore();
        store.Record(Result(display: "row A"));
        var first = store.TryGet(Source, Type, "So23")!;

        store.Record(Result(display: "row B"));

        first.Rows.Should().HaveCount(1);
        store.TryGet(Source, Type, "So23")!.Rows.Should().HaveCount(2);
    }

    [Fact]
    public void Record_parses_the_step_trace_but_does_not_retain_the_raw_stdout_or_stack_trace()
    {
        var store = new TestOutcomeStore();
        var trace = "Given x\n-> done: S.X() (0.0s)\n";
        var record = new TestResultRecord("run-1", Source, Type, "So23()", $"{Type}.So23", "row", TestOutcomeKind.Failed, 5, "boom", "at S.X()", trace, false);

        store.Record(record);

        var row = store.TryGet(Source, Type, "So23")!.Rows.Single();
        row.Steps.Should().ContainSingle("the trace was parsed before being discarded");
        row.Stdout.Should().BeNull("nothing downstream reads it again — retaining it would leak up to 64 KB per row for the store's lifetime");
        row.ErrorStackTrace.Should().BeNull();
        row.ErrorMessage.Should().Be("boom", "the (small, still useful) error message is kept");
    }
}
