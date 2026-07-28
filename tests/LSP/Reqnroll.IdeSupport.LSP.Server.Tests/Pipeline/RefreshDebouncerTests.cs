using System.Diagnostics;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

/// <summary>
/// Tests for <see cref="RefreshDebouncer"/>: a burst of rapid <see cref="IRefreshDebouncer.Schedule"/>
/// calls for the same key should collapse into a single run of the most recently scheduled action,
/// after a quiet period — including across separate <see cref="RefreshDebouncer"/> callers, which is
/// the scenario a transient MediatR handler actually produces in production (issue #156).
/// </summary>
public class RefreshDebouncerTests : IDisposable
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private RefreshDebouncer CreateSut() => new(_logger);

    private readonly RefreshDebouncer _sut;

    public RefreshDebouncerTests() => _sut = CreateSut();

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task Schedule_runs_the_action_after_the_delay()
    {
        var ran = new TaskCompletionSource();

        _sut.Schedule("k", TimeSpan.FromMilliseconds(50), _ =>
        {
            ran.TrySetResult();
            return Task.CompletedTask;
        });

        var completed = await Task.WhenAny(ran.Task, Task.Delay(2000));
        completed.Should().BeSameAs(ran.Task, "the debounced action should eventually run");
    }

    [Fact]
    public async Task Schedule_does_not_run_immediately()
    {
        var ranImmediately = false;

        _sut.Schedule("k", TimeSpan.FromMilliseconds(500), _ =>
        {
            ranImmediately = true;
            return Task.CompletedTask;
        });

        await Task.Delay(50);
        ranImmediately.Should().BeFalse("the action should wait for the delay, not run synchronously");
    }

    [Fact]
    public async Task Schedule_coalesces_a_burst_for_the_same_key_into_a_single_run_of_the_latest_action()
    {
        var runCount = 0;
        var lastRunValue = -1;
        var ran = new TaskCompletionSource();

        void ScheduleValue(int value) => _sut.Schedule("k", TimeSpan.FromMilliseconds(500), _ =>
        {
            runCount++;
            lastRunValue = value;
            ran.TrySetResult();
            return Task.CompletedTask;
        });

        // Simulate a burst: each call cancels the previous pending run for the same key.
        ScheduleValue(1);
        await Task.Delay(20);
        ScheduleValue(2);
        await Task.Delay(20);
        ScheduleValue(3);

        var completed = await Task.WhenAny(ran.Task, Task.Delay(2000));
        completed.Should().BeSameAs(ran.Task);

        // Give any incorrectly-surviving earlier runs a chance to fire before asserting.
        await Task.Delay(200);

        runCount.Should().Be(1, "only the most recently scheduled action in the burst should run");
        lastRunValue.Should().Be(3);
    }

    [Fact]
    public async Task Schedule_coalesces_a_burst_from_separate_callers_sharing_the_debouncer()
    {
        // This is the scenario that broke before this type existed: MediatR constructs a new
        // handler instance per notification, so debounce state living in the handler's own field
        // never collapses anything (issue #156). Here, three separate "callers" (standing in for
        // three separate handler instances) all schedule against the *same* shared IRefreshDebouncer
        // — only the last one scheduled for the key should actually run.
        var runCount = 0;
        var ran = new TaskCompletionSource();

        IRefreshDebouncer sharedDebouncer = _sut;

        void ScheduleFromNewCaller() => sharedDebouncer.Schedule("k", TimeSpan.FromMilliseconds(500), _ =>
        {
            runCount++;
            ran.TrySetResult();
            return Task.CompletedTask;
        });

        ScheduleFromNewCaller();
        await Task.Delay(20);
        ScheduleFromNewCaller();
        await Task.Delay(20);
        ScheduleFromNewCaller();

        var completed = await Task.WhenAny(ran.Task, Task.Delay(2000));
        completed.Should().BeSameAs(ran.Task);

        await Task.Delay(200);

        runCount.Should().Be(1, "sharing one debouncer across separate callers must still collapse the burst");
    }

    [Fact]
    public async Task Schedule_tracks_different_keys_independently()
    {
        var ranForA = new TaskCompletionSource();
        var ranForB = new TaskCompletionSource();

        _sut.Schedule("a", TimeSpan.FromMilliseconds(50), _ => { ranForA.TrySetResult(); return Task.CompletedTask; });
        _sut.Schedule("b", TimeSpan.FromMilliseconds(50), _ => { ranForB.TrySetResult(); return Task.CompletedTask; });

        await Task.WhenAll(
            Task.WhenAny(ranForA.Task, Task.Delay(2000)),
            Task.WhenAny(ranForB.Task, Task.Delay(2000)));

        ranForA.Task.IsCompletedSuccessfully.Should().BeTrue("scheduling for another key must not cancel this one");
        ranForB.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Schedule_after_a_prior_run_for_the_same_key_completed_gets_a_fresh_uncancelled_token_and_runs()
    {
        // Simulates a transient handler instance created *after* an earlier instance's debounce
        // window already fired and its CancellationTokenSource was disposed/removed from _pending
        // -- as opposed to the burst scenarios above, where a later Schedule call overlaps a still-
        // pending one and cancels it. The later call here must still get its own independent, live
        // token rather than somehow observing the earlier (already-fired) one as cancelled.
        var firstRan = new TaskCompletionSource();
        var secondRan = new TaskCompletionSource();
        CancellationToken secondToken = default;

        _sut.Schedule("k", TimeSpan.FromMilliseconds(20), _ =>
        {
            firstRan.TrySetResult();
            return Task.CompletedTask;
        });

        var firstCompleted = await Task.WhenAny(firstRan.Task, Task.Delay(2000));
        firstCompleted.Should().BeSameAs(firstRan.Task, "the first scheduled run should complete before the second is scheduled");

        // Give the debouncer a moment to remove and dispose its now-finished entry for "k".
        await Task.Delay(50);

        _sut.Schedule("k", TimeSpan.FromMilliseconds(20), token =>
        {
            secondToken = token;
            secondRan.TrySetResult();
            return Task.CompletedTask;
        });

        var secondCompleted = await Task.WhenAny(secondRan.Task, Task.Delay(2000));
        secondCompleted.Should().BeSameAs(secondRan.Task, "scheduling again for the same key after the earlier run finished must still run");
        secondToken.IsCancellationRequested.Should().BeFalse(
            "a handler instantiated after the earlier token fired must get its own independent, non-cancelled token");
    }

    [Fact]
    public async Task Schedule_logs_a_warning_when_the_action_throws()
    {
        var attempted = new TaskCompletionSource();
        var logged = new TaskCompletionSource();
        _logger.When(l => l.Log(Arg.Any<LogMessage>())).Do(_ => logged.TrySetResult());

        _sut.Schedule("k", TimeSpan.FromMilliseconds(20), _ =>
        {
            attempted.TrySetResult();
            throw new InvalidOperationException("boom");
        });

        await Task.WhenAny(attempted.Task, Task.Delay(2000));
        await Task.WhenAny(logged.Task, Task.Delay(2000));

        _logger.Received(1).Log(Arg.Is<LogMessage>(m =>
            m.Level == TraceLevel.Warning && m.Message.Contains("boom")));
    }

    [Fact]
    public void Dispose_cancels_pending_runs_without_throwing()
    {
        var sut = CreateSut();
        sut.Schedule("k", TimeSpan.FromMilliseconds(500), _ => Task.CompletedTask);

        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_racing_a_run_completing_at_the_same_time_does_not_throw()
    {
        // Regression for the exact race already fixed in the sibling FeatureRescanDebouncer.Dispose():
        // a snapshot-then-act loop over _pending.Values can observe a CancellationTokenSource that
        // RunAfterDelayAsync's own finally block concurrently disposes, throwing
        // ObjectDisposedException from Cancel()/Dispose(). Scheduling with a ~0ms delay and disposing
        // immediately from another thread, repeated, puts Dispose()'s loop and the finally block's
        // TryRemove in direct contention on most iterations.
        for (var i = 0; i < 200; i++)
        {
            var sut = CreateSut();
            sut.Schedule($"k{i}", TimeSpan.Zero, _ => Task.CompletedTask);

            var disposeTask = Task.Run(sut.Dispose);
            var act = async () => await disposeTask;
            await act.Should().NotThrowAsync();
        }
    }
}
