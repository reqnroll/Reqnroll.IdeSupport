using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestOutcomes;

/// <summary>
/// <see cref="TestOutcomePersistence.IsFresh"/> and <see cref="GetTestOutcomeHandler.IsStale"/> used
/// to be two independent implementations of the same rule (one in the VS-only VSSDKIntegration
/// project, one in the OOP callback listener) that disagreed on the "can't determine the container's
/// write time" path. Both now go through <see cref="TestOutcomeFreshness"/>, so this asserts they
/// agree — including on that path — plus the trust-window check that keeps a store outcome from
/// shadowing the client's own reflection bridge forever.
/// </summary>
public class TestOutcomeFreshnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reqnroll-freshness-tests", Guid.NewGuid().ToString("N"));
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

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
        var persistence = new TestOutcomePersistence(Path.Combine(_dir, "outcomes.json"), TestOutcomeFreshness.DefaultSourceLastWriteUtc, _logger);

        var freshOutcome = Outcome(container, writtenUtc.AddMinutes(1));
        var staleOutcome = Outcome(container, writtenUtc.AddMinutes(-1));

        persistence.IsFresh(freshOutcome).Should().BeTrue();
        GetTestOutcomeHandler.IsStale(freshOutcome).Should().BeFalse();

        persistence.IsFresh(staleOutcome).Should().BeFalse();
        GetTestOutcomeHandler.IsStale(staleOutcome).Should().BeTrue();
    }

    [Fact]
    public void IsStale_and_IsFresh_agree_when_the_container_is_missing()
    {
        var missing = Outcome(@"C:\does\not\exist.dll", DateTime.UtcNow);
        var persistence = new TestOutcomePersistence(Path.Combine(_dir, "outcomes.json"), TestOutcomeFreshness.DefaultSourceLastWriteUtc, _logger);

        persistence.IsFresh(missing).Should().BeFalse();
        GetTestOutcomeHandler.IsStale(missing).Should().BeTrue();
    }

    [Fact]
    public void IsTooOldToTrust_is_independent_of_rebuild_staleness()
    {
        var container = NewContainer();
        var writtenUtc = File.GetLastWriteTimeUtc(container);
        var now = writtenUtc.AddDays(1); // container hasn't changed since; plenty of time has passed
        var recentOutcome = Outcome(container, now - GetTestOutcomeHandler.MaxTrustedAge + TimeSpan.FromMinutes(1));
        var oldOutcome = Outcome(container, now - GetTestOutcomeHandler.MaxTrustedAge - TimeSpan.FromMinutes(1));

        GetTestOutcomeHandler.IsTooOldToTrust(recentOutcome, now).Should().BeFalse("within the trust window");
        GetTestOutcomeHandler.IsTooOldToTrust(oldOutcome, now).Should().BeTrue("the store hasn't heard about this method in longer than the trust window — a run it missed may have happened");

        // Neither is a rebuild, so IsStale (the separate, bridge-suppressing check) says fresh for both —
        // an aged-out entry is meant to fall through to the bridge, not render as suppressed-and-blank.
        GetTestOutcomeHandler.IsStale(recentOutcome).Should().BeFalse();
        GetTestOutcomeHandler.IsStale(oldOutcome).Should().BeFalse();
    }

    private static MethodOutcome Outcome(string source, DateTime lastUpdatedUtc)
    {
        var key = new TestOutcomeKey(source, "Ns.Feature", "M");
        return new MethodOutcome(key, TestOutcomeKind.Passed, Array.Empty<RowOutcome>(), lastUpdatedUtc);
    }
}
