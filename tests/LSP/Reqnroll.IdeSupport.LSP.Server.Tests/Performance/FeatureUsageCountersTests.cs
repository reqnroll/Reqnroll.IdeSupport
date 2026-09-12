using Reqnroll.IdeSupport.LSP.Server.Performance;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

/// <summary>
/// Concurrency tests for <see cref="FeatureUsageCounters"/> (issue #582), mirroring the barrier-
/// based style in <c>BindingMatchServiceConcurrencyTests</c>: <see cref="FeatureUsageCounters.Increment"/>
/// is called concurrently from the OmniSharp dispatch lane, thread-pool continuations, and detached
/// <c>FireAndForget</c> work, so an increment racing a drain must never be silently lost.
/// </summary>
public class FeatureUsageCountersTests
{
    [Fact]
    public void Drain_returns_empty_when_nothing_was_incremented()
    {
        var sut = new FeatureUsageCounters();

        sut.Drain().Should().BeEmpty();
    }

    [Fact]
    public void Increment_accumulates_multiple_calls_for_the_same_key()
    {
        var sut = new FeatureUsageCounters();

        sut.Increment("textDocument/definition");
        sut.Increment("textDocument/definition");
        sut.Increment("textDocument/definition");

        sut.Drain()["textDocument/definition"].Should().Be(3);
    }

    [Fact]
    public void Increment_tracks_different_keys_independently()
    {
        var sut = new FeatureUsageCounters();

        sut.Increment("textDocument/definition");
        sut.Increment("textDocument/references");
        sut.Increment("textDocument/references");

        var drained = sut.Drain();
        drained["textDocument/definition"].Should().Be(1);
        drained["textDocument/references"].Should().Be(2);
    }

    [Fact]
    public void Drain_clears_counts_so_a_second_drain_is_empty()
    {
        var sut = new FeatureUsageCounters();
        sut.Increment("textDocument/definition");

        sut.Drain();

        sut.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_increments_for_the_same_key_lose_no_count()
    {
        const int threadCount = 16;
        const int incrementsPerThread = 5000;
        var sut = new FeatureUsageCounters();

        using var gate = new Barrier(threadCount);
        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            gate.SignalAndWait();
            for (var i = 0; i < incrementsPerThread; i++)
                sut.Increment("textDocument/definition");
        }));
        await Task.WhenAll(tasks);

        sut.Drain()["textDocument/definition"].Should().Be((long)threadCount * incrementsPerThread);
    }

    [Fact]
    public async Task Drain_racing_Increment_does_not_drop_the_racing_increment()
    {
        // One writer incrementing continuously, one drainer racing it repeatedly. Whatever gets
        // split across two drains, the sum of every drain's counts plus whatever remains after
        // the writer stops must equal the total number of increments actually issued.
        var sut = new FeatureUsageCounters();
        const int totalIncrements = 200_000;
        var issued = 0;
        long drainedTotal = 0;

        using var stop = new CancellationTokenSource();
        var drainer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var count in sut.Drain().Values)
                    Interlocked.Add(ref drainedTotal, count);
            }
        });

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < totalIncrements; i++)
            {
                sut.Increment("textDocument/definition");
                Interlocked.Increment(ref issued);
            }
        });

        await writer;
        await stop.CancelAsync();
        await drainer;

        // Final drain to collect whatever the writer produced after the drainer's last pass.
        foreach (var count in sut.Drain().Values)
            Interlocked.Add(ref drainedTotal, count);

        drainedTotal.Should().Be(issued);
    }
}
