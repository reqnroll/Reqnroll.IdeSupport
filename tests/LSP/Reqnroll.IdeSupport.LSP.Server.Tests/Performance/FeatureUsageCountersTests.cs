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
    public void Observe_keeps_only_the_highest_value_seen_in_the_window()
    {
        var sut = new FeatureUsageCounters();

        sut.Observe("UndefinedStepsPeak", 3);
        sut.Observe("UndefinedStepsPeak", 9);
        sut.Observe("UndefinedStepsPeak", 4);

        sut.Drain()["UndefinedStepsPeak"].Should().Be(9);
    }

    [Fact]
    public void Observe_ignores_zero_and_negative_values_so_an_idle_gauge_stays_out_of_the_drain()
    {
        var sut = new FeatureUsageCounters();

        sut.Observe("UndefinedStepsPeak", 0);
        sut.Observe("UndefinedStepsPeak", -2);

        sut.Drain().Should().BeEmpty();
    }

    [Fact]
    public void Drain_resets_peaks_so_the_next_window_starts_from_zero()
    {
        var sut = new FeatureUsageCounters();
        sut.Observe("UndefinedStepsPeak", 9);
        sut.Drain();

        sut.Observe("UndefinedStepsPeak", 2);

        sut.Drain()["UndefinedStepsPeak"].Should().Be(2, "a high-water mark is per window, not per session");
    }

    [Fact]
    public void Counts_and_peaks_are_drained_together()
    {
        var sut = new FeatureUsageCounters();
        sut.Increment("CodeAction");
        sut.Observe("ParseErrorsPeak", 5);

        var drained = sut.Drain();

        drained.Should().BeEquivalentTo(new Dictionary<string, long> { ["CodeAction"] = 1, ["ParseErrorsPeak"] = 5 });
    }

    [Fact]
    public async Task Concurrent_observations_keep_the_true_maximum()
    {
        var sut = new FeatureUsageCounters();
        const int threads = 8;
        const int perThread = 2_000;
        using var barrier = new Barrier(threads);

        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 1; i <= perThread; i++)
                sut.Observe("UndefinedStepsPeak", t * perThread + i);
        })).ToArray();
        await Task.WhenAll(tasks);

        sut.Drain()["UndefinedStepsPeak"].Should().Be(threads * perThread);
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
