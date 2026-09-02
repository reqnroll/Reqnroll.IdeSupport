using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;
using Xunit;

// VS threading analyzers assume every Task in play is real VS UI-thread-affinitized work whose
// JoinableTaskContext lineage they must be able to trace — here every "Task" is an in-memory
// TaskCompletionSource-driven stand-in exercising this cache's own dedup/cancellation contract
// directly, with no UI thread or real async I/O involved anywhere in this file. Same rationale as
// RunTestCodeLensResultCacheTests.
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks

namespace Reqnroll.VisualStudio.Tests.StepCodeLens;

/// <summary>
/// Unit tests for <see cref="StepCodeLensResultCache"/> (issue #552 follow-up): mirrors
/// <c>RunTestCodeLensResultCacheTests</c>, whose subject fixed the same class of problem first —
/// VS.Extensibility calling once per code element, all wanting the same shared result.
/// </summary>
public class StepCodeLensResultCacheTests
{
    // ThreadHelper's own JoinableTaskContext/Factory works outside a real VS host too — it lazily
    // initializes against whatever thread first touches it rather than requiring devenv.exe.
    private static readonly JoinableTaskFactory Jtf = ThreadHelper.JoinableTaskFactory;

    private static readonly StepLensItem[] SampleItems =
    {
        new(RangeLine: 4, Title: "1 step usage", CommandName: "reqnroll.findStepUsages", ArgLine: 4, ArgChar: 5),
    };

    private sealed class CountingFetcher
    {
        private readonly TaskCompletionSource<bool>? _release;
        public int CallCount;

        public CountingFetcher(TaskCompletionSource<bool>? release = null) => _release = release;

        public async Task<IReadOnlyList<StepLensItem>> GetLensesAsync(string fileUri, CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);
            if (_release is not null)
                await _release.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return SampleItems;
        }
    }

    private static StepCodeLensResultCache CreateSut(CountingFetcher fetcher) =>
        new(fetcher.GetLensesAsync, Jtf);

    [Fact]
    public async Task Concurrent_callers_for_the_same_file_share_one_fetch()
    {
        var release = new TaskCompletionSource<bool>();
        var fetcher = new CountingFetcher(release);
        var sut = CreateSut(fetcher);

        // Five callers all asking for the same file at once — e.g. five step-definition methods'
        // CodeElements all requesting their lens on the same paint cycle.
        var calls = new Task<IReadOnlyList<StepLensItem>>[5];
        for (var i = 0; i < calls.Length; i++)
            calls[i] = sut.GetLensesAsync("file:///Steps.cs", CancellationToken.None);

        release.SetResult(true);
        var results = await Task.WhenAll(calls);

        fetcher.CallCount.Should().Be(1, "all five concurrent callers should have shared one fetch");
        foreach (var result in results)
            result.Should().BeEquivalentTo(SampleItems);
    }

    [Fact]
    public async Task A_completed_result_is_reused_indefinitely_without_refetching()
    {
        var fetcher = new CountingFetcher();
        var sut = CreateSut(fetcher);

        await sut.GetLensesAsync("file:///Steps.cs", CancellationToken.None);
        await sut.GetLensesAsync("file:///Steps.cs", CancellationToken.None);

        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Different_files_are_fetched_independently()
    {
        var fetcher = new CountingFetcher();
        var sut = CreateSut(fetcher);

        await sut.GetLensesAsync("file:///A.cs", CancellationToken.None);
        await sut.GetLensesAsync("file:///B.cs", CancellationToken.None);

        fetcher.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task InvalidateFile_forces_a_fresh_fetch_for_that_file_on_the_next_call()
    {
        var fetcher = new CountingFetcher();
        var sut = CreateSut(fetcher);

        await sut.GetLensesAsync("file:///A.cs", CancellationToken.None);
        await sut.GetLensesAsync("file:///B.cs", CancellationToken.None);
        sut.InvalidateFile("file:///A.cs");
        await sut.GetLensesAsync("file:///A.cs", CancellationToken.None);
        await sut.GetLensesAsync("file:///B.cs", CancellationToken.None);

        fetcher.CallCount.Should().Be(3, "invalidating one file must not force a re-fetch of another");
    }

    [Fact]
    public async Task InvalidateAll_forces_a_fresh_fetch_for_every_file()
    {
        var fetcher = new CountingFetcher();
        var sut = CreateSut(fetcher);

        await sut.GetLensesAsync("file:///A.cs", CancellationToken.None);
        await sut.GetLensesAsync("file:///B.cs", CancellationToken.None);
        sut.InvalidateAll();
        await sut.GetLensesAsync("file:///A.cs", CancellationToken.None);
        await sut.GetLensesAsync("file:///B.cs", CancellationToken.None);

        fetcher.CallCount.Should().Be(4);
    }

    [Fact]
    public async Task One_callers_cancellation_does_not_abort_the_shared_fetch_for_other_waiters()
    {
        // Regression coverage for the core correctness requirement: the shared fetch must run
        // under its own lifetime, never a caller's own token — otherwise VS abandoning one lens
        // (e.g. it scrolled out of view) would kill the result every other lens in the same file
        // is also waiting on.
        var release = new TaskCompletionSource<bool>();
        var fetcher = new CountingFetcher(release);
        var sut = CreateSut(fetcher);

        using var givingUpCts = new CancellationTokenSource();
        var givingUpCall = sut.GetLensesAsync("file:///Steps.cs", givingUpCts.Token);
        var patientCall  = sut.GetLensesAsync("file:///Steps.cs", CancellationToken.None);

        givingUpCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => givingUpCall);

        // The underlying fetch must still be alive for the still-waiting caller.
        release.SetResult(true);
        var result = await patientCall;

        result.Should().BeEquivalentTo(SampleItems);
        fetcher.CallCount.Should().Be(1, "the cancelled caller must not have triggered — or aborted — a separate fetch");
    }

    [Fact]
    public async Task A_faulted_fetch_is_not_reused_by_a_later_caller()
    {
        var callCount = 0;
        Task<IReadOnlyList<StepLensItem>> Fetch(string uri, CancellationToken ct)
        {
            callCount++;
            return callCount == 1
                ? Task.FromException<IReadOnlyList<StepLensItem>>(new InvalidOperationException("boom"))
                : Task.FromResult<IReadOnlyList<StepLensItem>>(SampleItems);
        }

        var sut = new StepCodeLensResultCache(Fetch, Jtf);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GetLensesAsync("file:///Steps.cs", CancellationToken.None));

        var result = await sut.GetLensesAsync("file:///Steps.cs", CancellationToken.None);

        result.Should().BeEquivalentTo(SampleItems);
        callCount.Should().Be(2, "a faulted result must not be handed to a later caller");
    }

    [Fact]
    public async Task File_uris_are_matched_case_insensitively()
    {
        // Mirrors StepCodeLensStateTests' file-URI case-insensitivity: URIs reach this cache from
        // several sources (VS code element context, LSP responses) whose casing does not always
        // agree.
        var fetcher = new CountingFetcher();
        var sut = CreateSut(fetcher);

        await sut.GetLensesAsync("file:///c:/w/Steps.cs", CancellationToken.None);
        await sut.GetLensesAsync("file:///C:/W/STEPS.CS", CancellationToken.None);

        fetcher.CallCount.Should().Be(1);
    }
}
