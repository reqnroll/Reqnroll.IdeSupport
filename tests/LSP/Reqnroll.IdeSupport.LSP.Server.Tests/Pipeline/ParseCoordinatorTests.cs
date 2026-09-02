using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

/// <summary>
/// Tests for <see cref="ParseCoordinator"/>: <see cref="IParseCoordinator.Schedule"/>
/// must run its work off the caller's thread (so a Serial-lane handler can return immediately —
/// issue #471), chain same-URI work in order rather than run it concurrently, never let a fault
/// propagate back into an unrelated <see cref="IParseCoordinator.WaitForReadyAsync"/> caller,
/// and let <see cref="IParseCoordinator.WaitForReadyAsync"/> observe whatever is currently
/// pending for that URI.
/// </summary>
public class ParseCoordinatorTests
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private ParseCoordinator CreateSut() => new(_logger);

    private static readonly DocumentUri UriA = DocumentUri.FromFileSystemPath("/workspace/a.feature");
    private static readonly DocumentUri UriB = DocumentUri.FromFileSystemPath("/workspace/b.feature");

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
        // The sequential case above takes the "chain onto the pending entry" path. This one covers
        // the case that actually bit in issue #554: two threads scheduling the same, not-yet-pending
        // URI at the same instant -- a didChange on the Serial dispatch lane while a
        // BindingRegistryChanged reparse for that file runs on a pool thread. The previous
        // ConcurrentDictionary.AddOrUpdate implementation ran its add factory (which started the
        // work) on both threads and failed this on the very first round.
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
            "two Schedule calls racing for one URI must still chain, not run the work simultaneously (round {0})",
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
}
