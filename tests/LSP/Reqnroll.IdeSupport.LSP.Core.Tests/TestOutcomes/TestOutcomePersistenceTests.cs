using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.TestOutcomes;

/// <summary>
/// Phase 3 of the implementation plan: outcomes survive a server restart, but never outlive a rebuild
/// of the container they describe, and several server instances can share the file.
/// </summary>
public class TestOutcomePersistenceTests : IDisposable
{
    private const string SourceA = @"C:\repo\A\bin\Debug\net8.0\A.dll";
    private const string SourceB = @"C:\repo\B\bin\Debug\net8.0\B.dll";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reqnroll-outcome-persistence-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, DateTime?> _writeTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private static readonly DateTime T0 = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    public TestOutcomePersistenceTests()
    {
        Directory.CreateDirectory(_dir);
        _writeTimes[SourceA] = T0.AddHours(-1); // built an hour before the outcomes below were recorded
        _writeTimes[SourceB] = T0.AddHours(-1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private TestOutcomePersistence Persistence(string? file = null)
        => new(Path.Combine(_dir, file ?? "test-outcomes.json"), source => _writeTimes.TryGetValue(source, out var t) ? t : null, _logger);

    private static MethodOutcome Outcome(string source, string method, DateTime updated, params (string Display, TestOutcomeKind Kind, string? Error, string? Stdout)[] rows)
    {
        var key = TestOutcomeKey.From(source, "Ns.Feature", method + "()", "Ns.Feature." + method)!;
        var rowOutcomes = rows.Select(r => new RowOutcome(r.Display, r.Kind, 5, r.Error, "stack", r.Stdout, false, "run-x", updated, StepTraceParser.Parse(r.Stdout))).ToList();
        return new MethodOutcome(key, TestOutcomeStore.Aggregate(rowOutcomes), rowOutcomes, updated);
    }

    [Fact]
    public void Save_then_Load_round_trips_methods_rows_steps_and_aggregate_without_stdout()
    {
        var persistence = Persistence();
        var trace = "Given x\n-> done: S.X() (0.1s)\nWhen y\n-> error: boom (0.0s)\n";
        persistence.Save(new[]
        {
            Outcome(SourceA, "Adding", T0, ("row 1", TestOutcomeKind.Passed, null, null), ("row 2", TestOutcomeKind.Failed, "boom", trace)),
        }, nowUtc: T0);

        var loaded = Persistence().Load();

        loaded.Should().ContainSingle();
        var method = loaded[0];
        method.Key.MethodName.Should().Be("Adding");
        method.Aggregate.Should().Be(TestOutcomeKind.Failed);
        method.LastUpdatedUtc.Should().Be(T0);
        method.Rows.Should().HaveCount(2);
        var failed = method.Rows.Single(r => r.DisplayName == "row 2");
        failed.ErrorMessage.Should().Be("boom");
        failed.Stdout.Should().BeNull("raw stdout is not persisted");
        failed.ErrorStackTrace.Should().BeNull();
        failed.Steps.Should().HaveCount(2);
        failed.Steps[1].Outcome.Should().Be(StepTraceOutcome.Error);
        failed.Steps[1].StepText.Should().Be("When y");
        failed.Steps[0].DurationSeconds.Should().Be(0.1);
        failed.FailedStep!.Index.Should().Be(1);
    }

    [Fact]
    public void Load_drops_entries_whose_container_was_rebuilt_after_the_outcome_or_no_longer_exists()
    {
        Persistence().Save(new[]
        {
            Outcome(SourceA, "Fresh", T0, ("r", TestOutcomeKind.Passed, null, null)),
            Outcome(SourceB, "Rebuilt", T0, ("r", TestOutcomeKind.Passed, null, null)),
            Outcome(@"C:\gone\X.dll", "Gone", T0, ("r", TestOutcomeKind.Passed, null, null)),
        }, nowUtc: T0);

        _writeTimes[SourceB] = T0.AddMinutes(5); // rebuilt after the run

        var loaded = Persistence().Load();

        loaded.Select(m => m.Key.MethodName).Should().Equal("Fresh");
    }

    [Fact]
    public void Save_merges_with_what_another_instance_wrote_and_the_newer_method_wins()
    {
        var first = Persistence();
        var second = Persistence();
        first.Save(new[]
        {
            Outcome(SourceA, "Shared", T0, ("r", TestOutcomeKind.Failed, "old", null)),
            Outcome(SourceA, "OnlyFirst", T0, ("r", TestOutcomeKind.Passed, null, null)),
        }, nowUtc: T0);

        second.Save(new[]
        {
            Outcome(SourceA, "Shared", T0.AddMinutes(1), ("r", TestOutcomeKind.Passed, null, null)),
            Outcome(SourceB, "OnlySecond", T0, ("r", TestOutcomeKind.Passed, null, null)),
        }, nowUtc: T0.AddMinutes(1));

        var loaded = Persistence().Load();
        loaded.Select(m => m.Key.MethodName).Should().BeEquivalentTo("Shared", "OnlyFirst", "OnlySecond");
        loaded.Single(m => m.Key.MethodName == "Shared").Aggregate.Should().Be(TestOutcomeKind.Passed, "the newer instance's run wins");

        // And an older save cannot regress a newer entry.
        first.Save(new[] { Outcome(SourceA, "Shared", T0, ("r", TestOutcomeKind.Failed, "old again", null)) }, nowUtc: T0.AddMinutes(2));
        Persistence().Load().Single(m => m.Key.MethodName == "Shared").Aggregate.Should().Be(TestOutcomeKind.Passed);
    }

    [Fact]
    public void Save_prunes_entries_older_than_the_retention_window()
    {
        var persistence = Persistence();
        persistence.Save(new[] { Outcome(SourceA, "Old", T0, ("r", TestOutcomeKind.Passed, null, null)) }, nowUtc: T0);

        persistence.Save(new[] { Outcome(SourceA, "New", T0.AddDays(31), ("r", TestOutcomeKind.Passed, null, null)) }, nowUtc: T0.AddDays(31));

        // The "new" entry is stale by write time (recorded after the container was built is fine; here it's later — fresh).
        Persistence().Load().Select(m => m.Key.MethodName).Should().Equal("New");
    }

    [Fact]
    public void Load_ignores_a_file_with_a_different_format_version_or_garbage()
    {
        var persistence = Persistence();
        File.WriteAllText(persistence.FilePath, new JObject { ["version"] = 99, ["methods"] = new JArray() }.ToString());
        persistence.Load().Should().BeEmpty();

        File.WriteAllText(persistence.FilePath, "this is not json");
        persistence.Load().Should().BeEmpty();

        // And a garbage file doesn't prevent a subsequent save from working.
        persistence.Save(new[] { Outcome(SourceA, "AfterGarbage", T0, ("r", TestOutcomeKind.Passed, null, null)) }, nowUtc: T0);
        Persistence().Load().Should().ContainSingle();
    }

    [Fact]
    public void Load_of_a_missing_file_is_empty_and_creates_nothing()
    {
        var persistence = Persistence("nope.json");

        persistence.Load().Should().BeEmpty();
        File.Exists(persistence.FilePath).Should().BeFalse();
    }

    [Fact]
    public void Store_seeded_from_persistence_answers_lookups_before_any_run()
    {
        Persistence().Save(new[] { Outcome(SourceA, "Seeded", T0, ("r", TestOutcomeKind.Failed, "e", null)) }, nowUtc: T0);

        var store = new TestOutcomeStore(Persistence());

        store.TryGet(SourceA, "Ns.Feature", "Seeded")!.Aggregate.Should().Be(TestOutcomeKind.Failed);
    }

    [Fact]
    public void Import_never_overwrites_a_newer_live_result()
    {
        var store = new TestOutcomeStore();
        var key = store.Record(new TestResultRecord("live", SourceA, "Ns.Feature", "M()", "Ns.Feature.M", "r", TestOutcomeKind.Passed, 1, null, null, null, false), nowUtc: T0.AddMinutes(1))!;

        store.Import(new[] { Outcome(SourceA, "M", T0, ("r", TestOutcomeKind.Failed, "old", null)) });

        store.TryGet(key)!.Aggregate.Should().Be(TestOutcomeKind.Passed);
        store.TryGet(key)!.Rows.Single().RunId.Should().Be("live");
    }

    [Fact]
    public void Import_adds_unknown_rows_to_an_older_live_method()
    {
        var store = new TestOutcomeStore();
        store.Record(new TestResultRecord("live", SourceA, "Ns.Feature", "M()", "Ns.Feature.M", "row 1", TestOutcomeKind.Passed, 1, null, null, null, false), nowUtc: T0);

        store.Import(new[] { Outcome(SourceA, "M", T0.AddMinutes(1), ("row 1", TestOutcomeKind.Failed, null, null), ("row 2", TestOutcomeKind.Failed, null, null)) });

        var outcome = store.TryGet(SourceA, "Ns.Feature", "M")!;
        outcome.Rows.Should().HaveCount(2);
        outcome.Rows.Single(r => r.DisplayName == "row 1").Outcome.Should().Be(TestOutcomeKind.Failed, "the imported row is newer");
    }
}
