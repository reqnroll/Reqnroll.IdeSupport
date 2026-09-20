using Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.TestOutcomes;

/// <summary>
/// <see cref="TestOutcomeFreshness"/> is the single source of truth for "is this outcome still
/// describing the code that produced it" — used directly here, and cross-checked against
/// Reqnroll.IdeSupport.LSP.Server's <c>GetTestOutcomeHandler.IsStale</c> (a different assembly, so
/// that consistency check lives in that project's own test suite instead of here — see
/// TestOutcomeFreshnessConsistencyTests in Reqnroll.IdeSupport.LSP.Server.Tests).
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
}
