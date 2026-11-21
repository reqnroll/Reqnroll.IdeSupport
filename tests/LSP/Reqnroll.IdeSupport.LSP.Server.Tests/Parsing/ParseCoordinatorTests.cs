using System.Collections.Concurrent;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Parsing;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Parsing;

/// <summary>
/// Tests for <see cref="ParseCoordinator"/>: <see cref="IParseCoordinator.Schedule"/> must hand work off so a
/// Serial-lane handler can return immediately, run work for one URI one piece at a time, keep at most one piece of
/// work waiting per URI (the newest replaces the one that was waiting), never let a fault propagate back into an
/// unrelated <see cref="IParseCoordinator.WaitForReadyAsync"/> caller, and let
/// <see cref="IParseCoordinator.WaitForReadyAsync"/> observe the latest work scheduled for that URI.
/// </summary>
public class ParseCoordinatorTests
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ConcurrentQueue<string> _ran = new();

    private ParseCoordinator CreateSut() => new(_logger);

    private static readonly DocumentUri UriA = DocumentUri.FromFileSystemPath("/workspace/a.feature");
    private static readonly DocumentUri UriB = DocumentUri.FromFileSystemPath("/workspace/b.feature");

    private Func<CancellationToken, Task> Records(string name) => _ =>
    {
        _ran.Enqueue(name);
        return Task.CompletedTask;
    };

    // Work that records that it started and then waits until the test lets it finish.
    private Func<CancellationToken, Task> Blocks(string name, TaskCompletionSource mayFinish) => async _ =>
    {
        _ran.Enqueue($"{name} started");
        await mayFinish.Task;
        _ran.Enqueue($"{name} finished");
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public void Schedule_returns_immediately_without_waiting_for_the_work_to_complete()
    {
        var sut = CreateSut();
        var gate = new TaskCompletionSource();

        sut.Schedule(UriA, async _ => await gate.Task);

        // Schedule must not block on the work it just queued.
        gate.Task.IsCompleted.Should().BeFalse();
        gate.TrySetResult();
    }

    [Fact]
    public void Work_scheduled_while_nothing_is_running_starts_on_the_calling_thread_before_Schedule_returns()
    {
        // Callers rely on the synchronous prefix of the work running inline, so a request that follows a
        // didOpen/didChange on the Serial dispatch lane already sees the freshly parsed buffer.
        var sut = CreateSut();

        sut.Schedule(UriA, Records("a"));

        _ran.Should().Equal("a");
    }

    [Fact]
    public async Task WaitForReadyAsync_completes_once_the_scheduled_work_finishes()
    {
        var sut = CreateSut();
        var gate = new TaskCompletionSource();
        var ran = false;

        sut.Schedule(UriA, async _ =>
        {
            await gate.Task;
            ran = true;
        });

        var wait = sut.WaitForReadyAsync(UriA, CancellationToken.None);
        wait.IsCompleted.Should().BeFalse("the scheduled work has not finished yet");

        gate.TrySetResult();
        await wait;

        ran.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForReadyAsync_completes_immediately_when_nothing_was_scheduled_for_that_uri()
    {
        var sut = CreateSut();

        var wait = sut.WaitForReadyAsync(UriA, CancellationToken.None);

        (await Task.WhenAny(wait, Task.Delay(2000))).Should().BeSameAs(wait);
    }

    [Fact]
    public async Task WaitForReadyAsync_completes_immediately_once_the_last_scheduled_work_has_finished()
    {
        var sut = CreateSut();
        sut.Schedule(UriA, _ => Task.CompletedTask);

        await sut.WaitForReadyAsync(UriA, CancellationToken.None);

        var secondWait = sut.WaitForReadyAsync(UriA, CancellationToken.None);
        (await Task.WhenAny(secondWait, Task.Delay(2000))).Should().BeSameAs(secondWait);
    }

    [Fact]
    public async Task Two_schedules_for_the_same_uri_never_run_concurrently()
    {
        var sut = CreateSut();
        var firstStarted = new TaskCompletionSource();
        var firstMayFinish = new TaskCompletionSource();
        var secondStartedWhileFirstStillRunning = false;

        sut.Schedule(UriA, async _ =>
        {
            firstStarted.TrySetResult();
            await firstMayFinish.Task;
        });

        sut.Schedule(UriA, _ =>
        {
            secondStartedWhileFirstStillRunning = !firstMayFinish.Task.IsCompleted;
            return Task.CompletedTask;
        });

        await firstStarted.Task;
        // Give the (wrongly, if buggy) concurrent second scheduled item a chance to start.
        await Task.Delay(50);
        firstMayFinish.TrySetResult();

        await sut.WaitForReadyAsync(UriA, CancellationToken.None);

        secondStartedWhileFirstStillRunning.Should().BeFalse(
            "the second scheduled item must not start until the first one for the same URI has finished");
    }

    [Fact]
    public async Task Two_concurrent_schedules_for_the_same_uri_never_run_concurrently()
    {
        // Two threads scheduling the same, not-yet-running URI at the same instant -- a didChange on the Serial
        // dispatch lane while a BindingRegistryChanged reparse for that file runs on a pool thread. A
        // ConcurrentDictionary.AddOrUpdate implementation would run its add factory (which starts the work) on
        // both threads and fail this on the very first round.
        var sut = CreateSut();
        var overlapAt = -1;

        for (var round = 0; round < 200 && overlapAt < 0; round++)
        {
            var inFlight = 0;
            var maxInFlight = 0;

            async Task Work(CancellationToken _)
            {
                InterlockedMax(ref maxInFlight, Interlocked.Increment(ref inFlight));
                await Task.Delay(10);
                Interlocked.Decrement(ref inFlight);
            }

            var uri = DocumentUri.FromFileSystemPath($"/workspace/concurrent-{round}.feature");
            using var gate = new Barrier(2);
            var t1 = Task.Run(() => { gate.SignalAndWait(); sut.Schedule(uri, Work); });
            var t2 = Task.Run(() => { gate.SignalAndWait(); sut.Schedule(uri, Work); });
            await Task.WhenAll(t1, t2);

            await sut.WaitForReadyAsync(uri, CancellationToken.None);
            if (Volatile.Read(ref maxInFlight) > 1)
                overlapAt = round;
        }

        overlapAt.Should().Be(-1,
            "two Schedule calls racing for one URI must still run one at a time, not simultaneously (round {0})",
            overlapAt);
    }

    /// <summary>Raises <paramref name="target"/> to <paramref name="value"/> if it is currently lower, without locking.</summary>
    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value)
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
                return;
    }

    [Fact]
    public async Task Two_schedules_for_different_uris_do_not_block_each_other()
    {
        var sut = CreateSut();
        var gateForA = new TaskCompletionSource();
        var bRan = false;

        sut.Schedule(UriA, async _ => await gateForA.Task);
        sut.Schedule(UriB, _ =>
        {
            bRan = true;
            return Task.CompletedTask;
        });

        await sut.WaitForReadyAsync(UriB, CancellationToken.None);

        bRan.Should().BeTrue("scheduled work for a different URI must not wait behind UriA's still-pending work");
        gateForA.TrySetResult();
    }

    // ── At most one waiting ──────────────────────────────────────────────────

    [Fact]
    public async Task Work_scheduled_while_one_is_running_replaces_the_work_that_was_waiting()
    {
        var sut = CreateSut();
        var firstMayFinish = new TaskCompletionSource();

        sut.Schedule(UriA, Blocks("first", firstMayFinish));
        sut.Schedule(UriA, Records("second"));
        sut.Schedule(UriA, Records("third"));
        sut.Schedule(UriA, Records("fourth"));

        firstMayFinish.SetResult();
        await sut.WaitForReadyAsync(UriA, CancellationToken.None);

        _ran.Should().Equal(new[] { "first started", "first finished", "fourth" },
            "only the newest work that arrived while the first was running is left to run");
    }

    [Fact]
    public async Task A_burst_of_a_hundred_schedules_runs_the_running_work_and_one_more()
    {
        var sut = CreateSut();
        var firstMayFinish = new TaskCompletionSource();
        var runs = 0;

        sut.Schedule(UriA, async _ =>
        {
            Interlocked.Increment(ref runs);
            await firstMayFinish.Task;
        });
        for (var i = 0; i < 100; i++)
            sut.Schedule(UriA, _ =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            });

        firstMayFinish.SetResult();
        await sut.WaitForReadyAsync(UriA, CancellationToken.None);

        runs.Should().Be(2);
    }

    [Fact]
    public async Task Work_that_was_replaced_before_it_ran_never_runs_even_when_the_replacement_is_slow()
    {
        var sut = CreateSut();
        var firstMayFinish = new TaskCompletionSource();
        var thirdMayFinish = new TaskCompletionSource();

        sut.Schedule(UriA, Blocks("first", firstMayFinish));
        sut.Schedule(UriA, Records("second"));
        sut.Schedule(UriA, Blocks("third", thirdMayFinish));
        firstMayFinish.SetResult();
        await WaitUntilAsync(() => _ran.Contains("third started"));
        thirdMayFinish.SetResult();
        await sut.WaitForReadyAsync(UriA, CancellationToken.None);

        _ran.Should().NotContain("second");
        _ran.Should().Equal("first started", "first finished", "third started", "third finished");
    }

    [Fact]
    public async Task WaitForReadyAsync_waits_for_the_latest_work_not_just_the_running_work()
    {
        var sut = CreateSut();
        var firstMayFinish = new TaskCompletionSource();
        var latestMayFinish = new TaskCompletionSource();

        sut.Schedule(UriA, Blocks("first", firstMayFinish));
        sut.Schedule(UriA, Blocks("latest", latestMayFinish));

        var wait = sut.WaitForReadyAsync(UriA, CancellationToken.None);
        firstMayFinish.SetResult();
        await WaitUntilAsync(() => _ran.Contains("latest started"));

        wait.IsCompleted.Should().BeFalse("the waiter is waiting for the latest work, which is still running");

        latestMayFinish.SetResult();
        await wait;
        _ran.Should().Contain("latest finished");
    }

    [Fact]
    public async Task A_waiter_for_work_that_gets_replaced_completes_when_the_replacement_has_run()
    {
        var sut = CreateSut();
        var firstMayFinish = new TaskCompletionSource();

        sut.Schedule(UriA, Blocks("first", firstMayFinish));
        sut.Schedule(UriA, Records("replaced"));
        var wait = sut.WaitForReadyAsync(UriA, CancellationToken.None);
        sut.Schedule(UriA, Records("replacement"));

        firstMayFinish.SetResult();
        await wait;

        _ran.Should().Contain("replacement", "the waiter must not be released before the work that stands in for what it waited for");
        _ran.Should().NotContain("replaced");
    }

    [Fact]
    public async Task WaitForReadyAsync_can_be_cancelled_without_affecting_the_work()
    {
        var sut = CreateSut();
        var firstMayFinish = new TaskCompletionSource();
        sut.Schedule(UriA, Blocks("first", firstMayFinish));
        using var cancellation = new CancellationTokenSource();

        var wait = sut.WaitForReadyAsync(UriA, cancellation.Token);
        await cancellation.CancelAsync();

        var act = async () => await wait;
        await act.Should().ThrowAsync<OperationCanceledException>();

        firstMayFinish.SetResult();
        await sut.WaitForReadyAsync(UriA, CancellationToken.None);
        _ran.Should().Contain("first finished");
    }

    [Fact]
    public async Task After_all_work_has_finished_the_next_schedule_starts_at_once_again()
    {
        var sut = CreateSut();
        sut.Schedule(UriA, Records("a"));
        await sut.WaitForReadyAsync(UriA, CancellationToken.None);

        sut.Schedule(UriA, Records("b"));

        _ran.Should().Equal("a", "b");
    }

    // ── Faults ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_faulting_scheduled_action_does_not_fault_WaitForReadyAsync()
    {
        var sut = CreateSut();

        sut.Schedule(UriA, _ => throw new InvalidOperationException("boom"));

        var act = async () => await sut.WaitForReadyAsync(UriA, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_faulting_scheduled_action_does_not_block_the_next_schedule_for_the_same_uri()
    {
        var sut = CreateSut();
        var secondRan = false;

        sut.Schedule(UriA, _ => throw new InvalidOperationException("boom"));
        sut.Schedule(UriA, _ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        await sut.WaitForReadyAsync(UriA, CancellationToken.None);

        secondRan.Should().BeTrue();
    }

    [Fact]
    public async Task A_running_action_that_fails_after_a_wait_does_not_stop_the_waiting_work_from_running()
    {
        var sut = CreateSut();
        var firstMayFail = new TaskCompletionSource();

        sut.Schedule(UriA, async _ =>
        {
            await firstMayFail.Task;
            throw new InvalidOperationException("boom");
        });
        sut.Schedule(UriA, Records("after"));

        firstMayFail.SetResult();
        await sut.WaitForReadyAsync(UriA, CancellationToken.None);

        _ran.Should().Equal("after");
    }

    [Fact]
    public async Task A_failure_while_reporting_a_failure_releases_the_waiters_and_leaves_the_document_free_for_new_work()
    {
        // The logger throwing while the coordinator reports a fault of the work is the one way the run loop itself can
        // fail. The entry for the document must not be left behind, or every later parse of it would wait forever.
        _logger.When(l => l.Log(Arg.Is<LogMessage>(m => m.Level == System.Diagnostics.TraceLevel.Warning))).Do(_ => throw new InvalidOperationException("logger"));
        var sut = CreateSut();
        var mayFail = new TaskCompletionSource();

        sut.Schedule(UriA, async _ =>
        {
            await mayFail.Task;
            throw new InvalidOperationException("boom");
        });
        sut.Schedule(UriA, Records("dropped"));
        var waiter = sut.WaitForReadyAsync(UriA, CancellationToken.None);

        mayFail.SetResult();
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));

        sut.Schedule(UriA, Records("fresh"));
        await sut.WaitForReadyAsync(UriA, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        _ran.Should().Equal("fresh");
    }
}
