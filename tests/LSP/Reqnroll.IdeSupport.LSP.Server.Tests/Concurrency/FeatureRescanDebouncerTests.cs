using System.Diagnostics;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Concurrency;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Concurrency;

/// <summary>
/// Tests for <see cref="FeatureRescanDebouncer"/>: a burst of rapid
/// <see cref="IFeatureRescanDebouncer.ScheduleRescan"/> calls for the same project should
/// collapse into a single run of the most recently scheduled action, after a quiet period.
/// </summary>
public class FeatureRescanDebouncerTests : IDisposable
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly LspIdeScope _ideScope;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly LspReqnrollProject _project;

    public FeatureRescanDebouncerTests()
    {
        _ideScope = new LspIdeScope(_logger);
        _project = DiscoveryTestSupport.MakeProject(_ideScope, _folder);
    }

    public void Dispose() => _project.Dispose();

    private FeatureRescanDebouncer CreateSut() => new(_logger);

    [Fact]
    public async Task ScheduleRescan_runs_the_action_after_the_debounce_window()
    {
        using var sut = CreateSut();
        var ran = new TaskCompletionSource();

        sut.ScheduleRescan(_project, _ =>
        {
            ran.TrySetResult();
            return Task.CompletedTask;
        });

        var completed = await Task.WhenAny(ran.Task, Task.Delay(2000));
        completed.Should().BeSameAs(ran.Task, "the debounced action should eventually run");
    }

    [Fact]
    public async Task ScheduleRescan_does_not_run_immediately()
    {
        using var sut = CreateSut();
        var ranImmediately = false;

        sut.ScheduleRescan(_project, _ =>
        {
            ranImmediately = true;
            return Task.CompletedTask;
        });

        await Task.Delay(50);
        ranImmediately.Should().BeFalse("the action should wait for the debounce window, not run synchronously");
    }

    [Fact]
    public async Task ScheduleRescan_coalesces_a_burst_into_a_single_run_of_the_latest_action()
    {
        using var sut = CreateSut();
        var runCount = 0;
        var lastRunValue = -1;
        var ran = new TaskCompletionSource();

        void Schedule(int value) => sut.ScheduleRescan(_project, _ =>
        {
            runCount++;
            lastRunValue = value;
            ran.TrySetResult();
            return Task.CompletedTask;
        });

        // Simulate a burst of keystrokes: each call cancels the previous pending run.
        Schedule(1);
        await Task.Delay(20);
        Schedule(2);
        await Task.Delay(20);
        Schedule(3);

        var completed = await Task.WhenAny(ran.Task, Task.Delay(2000));
        completed.Should().BeSameAs(ran.Task);

        // Give any incorrectly-surviving earlier runs a chance to fire before asserting.
        await Task.Delay(200);

        runCount.Should().Be(1, "only the most recently scheduled action in the burst should run");
        lastRunValue.Should().Be(3);
    }

    [Fact]
    public async Task ScheduleRescan_tracks_different_projects_independently()
    {
        using var sut = CreateSut();
        var otherProject = DiscoveryTestSupport.MakeProject(_ideScope, _folder + "_other");
        try
        {
            var ranForProject = new TaskCompletionSource();
            var ranForOther = new TaskCompletionSource();

            sut.ScheduleRescan(_project, _ => { ranForProject.TrySetResult(); return Task.CompletedTask; });
            sut.ScheduleRescan(otherProject, _ => { ranForOther.TrySetResult(); return Task.CompletedTask; });

            var completed = await Task.WhenAll(
                Task.WhenAny(ranForProject.Task, Task.Delay(2000)),
                Task.WhenAny(ranForOther.Task, Task.Delay(2000)));

            ranForProject.Task.IsCompletedSuccessfully.Should().BeTrue("scheduling for another project must not cancel this one");
            ranForOther.Task.IsCompletedSuccessfully.Should().BeTrue();
        }
        finally
        {
            otherProject.Dispose();
        }
    }

    [Fact]
    public async Task ScheduleRescan_logs_a_warning_when_the_action_throws()
    {
        using var sut = CreateSut();
        var attempted = new TaskCompletionSource();

        // LogWarning is an extension method over IIdeSupportLogger.Log(LogMessage); hook the
        // underlying substitutable call itself as the completion signal, rather than a fixed
        // delay guessing how long the exception takes to propagate out of the fire-and-forget
        // continuation.
        var logged = new TaskCompletionSource();
        _logger.When(l => l.Log(Arg.Any<LogMessage>())).Do(_ => logged.TrySetResult());

        sut.ScheduleRescan(_project, _ =>
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
    public void Dispose_cancels_pending_rescans_without_throwing()
    {
        var sut = CreateSut();
        sut.ScheduleRescan(_project, _ => Task.CompletedTask);

        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_racing_a_rescan_completing_at_the_same_time_does_not_throw()
    {
        // Regression for the race Dispose()'s TryRemove-by-key claiming already guards against:
        // a snapshot-then-act loop over _pending.Values could observe a CancellationTokenSource
        // that RunAfterDebounceAsync's own finally block concurrently disposes once the debounce
        // window elapses, throwing ObjectDisposedException. Waiting to just under the 500ms
        // window before racing Dispose() from another thread, repeated, puts the two paths in
        // contention on most iterations (unlike disposing immediately after scheduling, which
        // never lets the window elapse at all).
        for (var i = 0; i < 10; i++)
        {
            var sut = CreateSut();
            sut.ScheduleRescan(_project, _ => Task.CompletedTask);
            await Task.Delay(490);

            var disposeTask = Task.Run(sut.Dispose);
            var act = async () => await disposeTask;
            await act.Should().NotThrowAsync();
        }
    }
}
