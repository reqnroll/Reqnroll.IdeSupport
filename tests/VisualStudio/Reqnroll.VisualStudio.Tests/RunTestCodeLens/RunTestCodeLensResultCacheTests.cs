using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Reqnroll.IdeSupport.VisualStudio.Extension.RunTestCodeLens;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Xunit;

// VS threading analyzers assume every Task in play is real VS UI-thread-affinitized work whose
// JoinableTaskContext lineage they must be able to trace — here every "Task" is an in-memory
// TaskCompletionSource-driven stand-in exercising this cache's own dedup/cancellation contract
// directly, with no UI thread or real async I/O involved anywhere in this file.
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks

namespace Reqnroll.VisualStudio.Tests.RunTestCodeLens;

/// <summary>
/// Coverage for issue #262's follow-up fix, re-scoped by issue #495:
/// <see cref="RunTestCodeLensService.GetTargetsForLineAsync"/> is now called once per visible
/// Scenario line rather than once per file for the whole document, so this cache de-dupes concurrent
/// callers for the same <c>(fileUri, line)</c> instead of just <c>fileUri</c>.
/// </summary>
public class RunTestCodeLensResultCacheTests
{
    // ThreadHelper's own JoinableTaskContext/Factory works outside a real VS host too — it lazily
    // initializes against whatever thread first touches it rather than requiring devenv.exe.
    private static readonly JoinableTaskFactory Jtf = ThreadHelper.JoinableTaskFactory;

    private static readonly RunTestTargetEntry[] SampleEntries =
    {
        new(0, "asm.dll", "T", "M"),
    };

    private sealed class CountingResolver
    {
        private readonly TaskCompletionSource<bool>? _release;
        public int CallCount;

        public CountingResolver(TaskCompletionSource<bool>? release = null) => _release = release;

        public async Task<IReadOnlyList<RunTestTargetEntry>> GetTargetsForLineAsync(string fileUri, int line, CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);
            if (_release is not null)
                await _release.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return SampleEntries;
        }
    }

    private static RunTestCodeLensResultCache CreateSut(
        CountingResolver resolver, System.TimeSpan? computationTimeout = null) =>
        new(resolver.GetTargetsForLineAsync, NullLogger<RunTestCodeLensResultCache>.Instance, Jtf, computationTimeout);

    [Fact]
    public async Task Concurrent_callers_for_the_same_file_and_line_share_one_computation()
    {
        var release = new TaskCompletionSource<bool>();
        var resolver = new CountingResolver(release);
        var sut = CreateSut(resolver);

        // Five callers all asking for the same (file, line) at once — e.g. the tagger's own
        // placement fetch racing a data point during startup.
        var calls = new Task<IReadOnlyList<RunTestTargetEntry>>[5];
        for (var i = 0; i < calls.Length; i++)
            calls[i] = sut.GetTargetsAsync("file:///Test.feature", 3, CancellationToken.None);

        release.SetResult(true);
        var results = await Task.WhenAll(calls);

        resolver.CallCount.Should().Be(1, "all five concurrent callers should have shared one computation");
        foreach (var result in results)
            result.Should().BeEquivalentTo(SampleEntries);
    }

    [Fact]
    public async Task A_completed_result_is_reused_indefinitely_without_recomputing()
    {
        // Regression coverage (live report, 2026-08-26): this cache used to expire a completed
        // result after a fixed few-second TTL, so any caller arriving after that window forced an
        // entirely new full-document walk — one slow enough on a large corpus (30-45s) to reliably
        // outlive VS's own per-data-point timeout, even though nothing had actually changed.
        // There's no time-based expiry any more: a completed result is valid until an explicit
        // InvalidateFile/InvalidateAll (see the next test), never merely because time passed.
        var resolver = new CountingResolver();
        var sut = CreateSut(resolver);

        await sut.GetTargetsAsync("file:///Test.feature", 3, CancellationToken.None);
        await sut.GetTargetsAsync("file:///Test.feature", 3, CancellationToken.None);

        resolver.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Different_lines_in_the_same_file_are_resolved_independently()
    {
        var resolver = new CountingResolver();
        var sut = CreateSut(resolver);

        await sut.GetTargetsAsync("file:///A.feature", 3, CancellationToken.None);
        await sut.GetTargetsAsync("file:///A.feature", 7, CancellationToken.None);

        resolver.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Different_files_are_resolved_independently()
    {
        var resolver = new CountingResolver();
        var sut = CreateSut(resolver);

        await sut.GetTargetsAsync("file:///A.feature", 3, CancellationToken.None);
        await sut.GetTargetsAsync("file:///B.feature", 3, CancellationToken.None);

        resolver.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task InvalidateFile_forces_a_fresh_computation_for_every_line_of_that_file_on_the_next_call()
    {
        var resolver = new CountingResolver();
        var sut = CreateSut(resolver);

        await sut.GetTargetsAsync("file:///Test.feature", 3, CancellationToken.None);
        await sut.GetTargetsAsync("file:///Test.feature", 7, CancellationToken.None);
        sut.InvalidateFile("file:///Test.feature");
        await sut.GetTargetsAsync("file:///Test.feature", 3, CancellationToken.None);
        await sut.GetTargetsAsync("file:///Test.feature", 7, CancellationToken.None);

        resolver.CallCount.Should().Be(4, "invalidating a file must drop every one of its lines' cached results, not just one");
    }

    [Fact]
    public async Task One_callers_cancellation_does_not_abort_the_shared_computation_for_other_waiters()
    {
        // Regression coverage for the core correctness requirement: the shared computation must run
        // under its own lifetime, never a caller's own token — otherwise the first caller to give up
        // (e.g. VS's own per-data-point timeout) would kill the result for every other waiter too.
        var release = new TaskCompletionSource<bool>();
        var resolver = new CountingResolver(release);
        var sut = CreateSut(resolver);

        using var givingUpCts = new CancellationTokenSource();
        var givingUpCall = sut.GetTargetsAsync("file:///Test.feature", 3, givingUpCts.Token);
        var patientCall = sut.GetTargetsAsync("file:///Test.feature", 3, CancellationToken.None);

        givingUpCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => givingUpCall);

        // The underlying computation must still be alive for the still-waiting caller.
        release.SetResult(true);
        var result = await patientCall;

        result.Should().BeEquivalentTo(SampleEntries);
        resolver.CallCount.Should().Be(1, "the cancelled caller must not have triggered — or aborted — a separate computation");
    }

    [Fact]
    public async Task A_faulted_computation_is_not_reused_by_a_later_caller()
    {
        var callCount = 0;
        Task<IReadOnlyList<RunTestTargetEntry>> Resolver(string uri, int line, CancellationToken ct)
        {
            callCount++;
            return callCount == 1
                ? Task.FromException<IReadOnlyList<RunTestTargetEntry>>(new System.InvalidOperationException("boom"))
                : Task.FromResult<IReadOnlyList<RunTestTargetEntry>>(SampleEntries);
        }

        var sut = new RunTestCodeLensResultCache(Resolver, NullLogger<RunTestCodeLensResultCache>.Instance, Jtf);

        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => sut.GetTargetsAsync("file:///Test.feature", 3, CancellationToken.None));

        var result = await sut.GetTargetsAsync("file:///Test.feature", 3, CancellationToken.None);

        result.Should().BeEquivalentTo(SampleEntries);
        callCount.Should().Be(2, "a faulted result must not be handed to a later caller");
    }
}
