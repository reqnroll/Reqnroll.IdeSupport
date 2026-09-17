using System;
using System.IO;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Reqnroll.IdeSupport.VisualStudio.TestLogger;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestLogger;

/// <summary>
/// Fresh-eyes review fix: <see cref="TestOutcomePersistence.IsFresh"/> and
/// <see cref="RunTestCodeLensCallbackListener.IsStale"/> used to be two independent implementations of
/// the same rule that disagreed on the "can't determine the container's write time" path. Both now go
/// through <see cref="TestOutcomeFreshness"/>, so this asserts they agree — including on that path —
/// plus the added trust-window check that keeps a store outcome from shadowing the live bridge forever.
/// </summary>
public class TestOutcomeFreshnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reqnroll-freshness-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string NewContainer()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "Specs.dll");
        File.WriteAllText(path, "not a real assembly, just needs a write time");
        return path;
    }

    [Fact]
    public void IsFresh_is_true_only_when_the_source_was_written_at_or_before_lastUpdated()
    {
        var container = NewContainer();
        var writtenUtc = File.GetLastWriteTimeUtc(container);

        TestOutcomeFreshness.IsFresh(container, writtenUtc, TestOutcomeFreshness.DefaultSourceLastWriteUtc).Should().BeTrue("recorded at the same instant the container was last written");
        TestOutcomeFreshness.IsFresh(container, writtenUtc.AddSeconds(1), TestOutcomeFreshness.DefaultSourceLastWriteUtc).Should().BeTrue("recorded after the write — still describes current code");
        TestOutcomeFreshness.IsFresh(container, writtenUtc.AddSeconds(-1), TestOutcomeFreshness.DefaultSourceLastWriteUtc).Should().BeFalse("recorded before the write — the container was rebuilt since");
    }

    [Fact]
    public void IsFresh_treats_an_unresolvable_write_time_as_not_fresh()
    {
        // Missing file, and any other "can't tell" signal from the lookup func (an IO exception,
        // a permission failure) - both come back as null from the func, and both must mean "not fresh",
        // never "fresh by default".
        TestOutcomeFreshness.IsFresh(@"C:\does\not\exist.dll", DateTime.UtcNow, TestOutcomeFreshness.DefaultSourceLastWriteUtc).Should().BeFalse();
        TestOutcomeFreshness.IsFresh("whatever.dll", DateTime.UtcNow, _ => null).Should().BeFalse("a lookup func returning null for any reason means untrustworthy, not fresh");
    }

    [Fact]
    public void DefaultSourceLastWriteUtc_never_throws_for_a_missing_file()
        => TestOutcomeFreshness.DefaultSourceLastWriteUtc(@"C:\does\not\exist.dll").Should().BeNull();

    [Fact]
    public void IsStale_and_IsFresh_agree_for_the_same_container_and_timestamp()
    {
        var container = NewContainer();
        var writtenUtc = File.GetLastWriteTimeUtc(container);
        var persistence = new TestOutcomePersistence(Path.Combine(_dir, "outcomes.json"), TestOutcomeFreshness.DefaultSourceLastWriteUtc);

        var freshOutcome = Outcome(container, writtenUtc.AddMinutes(1));
        var staleOutcome = Outcome(container, writtenUtc.AddMinutes(-1));

        persistence.IsFresh(freshOutcome).Should().BeTrue();
        RunTestCodeLensCallbackListener.IsStale(freshOutcome).Should().BeFalse();

        persistence.IsFresh(staleOutcome).Should().BeFalse();
        RunTestCodeLensCallbackListener.IsStale(staleOutcome).Should().BeTrue();
    }

    [Fact]
    public void IsStale_and_IsFresh_agree_when_the_container_is_missing()
    {
        var missing = Outcome(@"C:\does\not\exist.dll", DateTime.UtcNow);
        var persistence = new TestOutcomePersistence(Path.Combine(_dir, "outcomes.json"), TestOutcomeFreshness.DefaultSourceLastWriteUtc);

        persistence.IsFresh(missing).Should().BeFalse();
        RunTestCodeLensCallbackListener.IsStale(missing).Should().BeTrue();
    }

    [Fact]
    public void IsTooOldToTrust_is_independent_of_rebuild_staleness()
    {
        var container = NewContainer();
        var writtenUtc = File.GetLastWriteTimeUtc(container);
        var now = writtenUtc.AddDays(1); // container hasn't changed since; plenty of time has passed
        var recentOutcome = Outcome(container, now - RunTestCodeLensCallbackListener.MaxTrustedAge + TimeSpan.FromMinutes(1));
        var oldOutcome = Outcome(container, now - RunTestCodeLensCallbackListener.MaxTrustedAge - TimeSpan.FromMinutes(1));

        RunTestCodeLensCallbackListener.IsTooOldToTrust(recentOutcome, now).Should().BeFalse("within the trust window");
        RunTestCodeLensCallbackListener.IsTooOldToTrust(oldOutcome, now).Should().BeTrue("the store hasn't heard about this method in longer than the trust window — a run it missed may have happened");

        // Neither is a rebuild, so IsStale (the separate, bridge-suppressing check) says fresh for both —
        // an aged-out entry is meant to fall through to the bridge, not render as suppressed-and-blank.
        RunTestCodeLensCallbackListener.IsStale(recentOutcome).Should().BeFalse();
        RunTestCodeLensCallbackListener.IsStale(oldOutcome).Should().BeFalse();
    }

    private static MethodOutcome Outcome(string source, DateTime lastUpdatedUtc)
    {
        var key = new TestOutcomeKey(source, "Ns.Feature", "M");
        return new MethodOutcome(key, TestOutcomeKind.Passed, Array.Empty<RowOutcome>(), lastUpdatedUtc);
    }

    [Fact]
    public async Task GetOutcomeAsync_returns_null_for_an_aged_out_entry_instead_of_a_stale_one()
    {
        var container = NewContainer();
        var store = new TestOutcomeStore();
        var callback = new RunTestCodeLensCallbackListener(store);
        var oldWhen = DateTime.UtcNow - RunTestCodeLensCallbackListener.MaxTrustedAge - TimeSpan.FromMinutes(5);
        store.Record(new TestResultRecord("run-1", container, "Ns.Feature", "M()", "Ns.Feature.M", "row", TestOutcomeKind.Passed, 1, null, null, null, false), nowUtc: oldWhen);

        var entry = await callback.GetOutcomeAsync(container, "Ns.Feature", "M", CancellationToken.None);

        // Not IsStale:true-with-data — genuinely absent, so RunTestCodeLensDataPoint's caller falls
        // through to the bridge exactly as it does for a method the store has never heard of at all.
        entry.Should().BeNull();
    }

    [Fact]
    public async Task GetOutcomeAsync_returns_the_entry_for_a_recent_one()
    {
        var container = NewContainer();
        var store = new TestOutcomeStore();
        var callback = new RunTestCodeLensCallbackListener(store);
        store.Record(new TestResultRecord("run-1", container, "Ns.Feature", "M()", "Ns.Feature.M", "row", TestOutcomeKind.Passed, 1, null, null, null, false));

        var entry = await callback.GetOutcomeAsync(container, "Ns.Feature", "M", CancellationToken.None);

        entry.Should().NotBeNull();
        entry!.Aggregate.Should().Be("Passed");
        entry.IsStale.Should().BeFalse();
    }
}
