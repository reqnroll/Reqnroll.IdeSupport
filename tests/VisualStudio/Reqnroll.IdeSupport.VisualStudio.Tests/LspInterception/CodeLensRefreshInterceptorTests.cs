using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="CodeLensRefreshInterceptor"/> only reacts to a <c>reqnroll/refreshCodeLens</c>
/// (receive); every other message, including a <c>.cs</c> <c>textDocument/didChange</c> (send) —
/// whose per-edit invalidation was removed for issue #156/#318 — must pass through untouched.
/// </summary>
/// <remarks>
/// Every message is expected to return <see cref="LspInterceptorResult.PassThrough"/>, so asserting
/// only that says nothing about whether an invalidation was scheduled. These tests therefore inject
/// the invalidation action (see the constructor's <c>invalidateOverride</c>) and assert on it
/// directly — the real dispatch hops to the VS main thread and needs a host, but the debounce and
/// rate-guard bookkeeping around it is ordinary logic.
/// </remarks>
public class CodeLensRefreshInterceptorTests
{
    // Comfortably longer than the interceptor's 400ms debounce window.
    private const int DebounceSettleMs = 1_500;

    private static CodeLensRefreshInterceptor Create(Action? onInvalidate = null) =>
        new(new StepCodeLensState(), NullLogger<CodeLensRefreshInterceptor>.Instance, onInvalidate);

    private static LspMessage Send(JObject body)    => new(LspMessageDirection.Send,    body, DateTimeOffset.Now);
    private static LspMessage Receive(JObject body) => new(LspMessageDirection.Receive, body, DateTimeOffset.Now);

    private static JObject RefreshCodeLens(bool? isFullReplacement)
    {
        var @params = new JObject { ["projectName"] = "Proj" };
        if (isFullReplacement is not null)
            @params["isFullReplacement"] = isFullReplacement.Value;

        return new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"]  = "reqnroll/refreshCodeLens",
            ["params"]  = @params,
        };
    }

    /// <summary>Counts invalidations, and lets a test wait for the first one instead of sleeping blind.</summary>
    private sealed class InvalidationSpy
    {
        private readonly ManualResetEventSlim _fired = new(false);
        private int _count;

        public int Count => Volatile.Read(ref _count);
        public Action Action => () => { Interlocked.Increment(ref _count); _fired.Set(); };
        public bool WaitForFirst(int ms = DebounceSettleMs) => _fired.Wait(ms);
    }

    private static JObject DidChange(string uri) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"]  = "textDocument/didChange",
        ["params"]  = new JObject { ["textDocument"] = new JObject { ["uri"] = uri } },
    };

    [Fact]
    public async Task A_message_without_a_method_passes_through()
    {
        var result = await Create().InterceptAsync(
            Receive(new JObject { ["jsonrpc"] = "2.0", ["id"] = 1 }), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task A_didChange_on_a_non_cs_file_passes_through_without_invalidating()
    {
        var result = await Create().InterceptAsync(
            Send(DidChange("file:///c:/w/A.feature")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task A_didChange_on_a_cs_file_passes_through_without_invalidating()
    {
        // Per-.cs-edit invalidation is disabled (issue #156/#318) — a .cs didChange is now just
        // another pass-through, no different from a non-.cs file, testable directly (no VS host
        // needed) since it no longer reaches any UI-thread CodeLens.Invalidate() call.
        var result = await Create().InterceptAsync(
            Send(DidChange("file:///c:/w/Steps.cs")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task A_didChange_without_a_uri_passes_through()
    {
        var body = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"]  = "textDocument/didChange",
            ["params"]  = new JObject { ["textDocument"] = new JObject() },
        };

        var result = await Create().InterceptAsync(Send(body), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task A_non_didChange_send_passes_through()
    {
        var result = await Create().InterceptAsync(
            Send(DidChange("file:///c:/w/Steps.cs").Tap(b => b["method"] = "textDocument/didOpen")),
            CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task An_unrelated_received_message_passes_through()
    {
        var result = await Create().InterceptAsync(
            Receive(new JObject { ["jsonrpc"] = "2.0", ["method"] = "window/logMessage" }),
            CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    // ── Invalidation scheduling (issue #343) ──────────────────────────────────

    [Theory]
    [InlineData(true)]   // rebuild / full binding-registry replacement
    [InlineData(false)]  // incremental Roslyn patch — acted on since #343
    [InlineData(null)]   // field absent, e.g. an older/mismatched payload
    public async Task A_refreshCodeLens_eventually_invalidates_regardless_of_isFullReplacement(bool? isFullReplacement)
    {
        var spy = new InvalidationSpy();
        using var sut = Create(spy.Action);

        var result = await sut.InterceptAsync(Receive(RefreshCodeLens(isFullReplacement)), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        spy.WaitForFirst().Should().BeTrue("a refresh signal must reach the lenses");
        spy.Count.Should().Be(1);
    }

    [Fact]
    public async Task Invalidation_is_deferred_rather_than_synchronous()
    {
        // The debounce is what keeps a burst of per-project signals from becoming a burst of
        // CodeLens.Invalidate() calls, each of which can provoke the #156 client reconnect.
        var spy = new InvalidationSpy();
        using var sut = Create(spy.Action);

        await sut.InterceptAsync(Receive(RefreshCodeLens(false)), CancellationToken.None);

        spy.Count.Should().Be(0, "the invalidation should be queued behind the debounce window");
    }

    [Fact]
    public async Task A_burst_of_refresh_signals_collapses_into_a_single_invalidation()
    {
        // Observed live: one user edit produced 16 refresh signals across projects, which the
        // debounce coalesced into far fewer invalidations.
        var spy = new InvalidationSpy();
        using var sut = Create(spy.Action);

        for (var i = 0; i < 10; i++)
            await sut.InterceptAsync(Receive(RefreshCodeLens(i % 2 == 0)), CancellationToken.None);

        spy.WaitForFirst().Should().BeTrue();
        await Task.Delay(300);
        spy.Count.Should().Be(1, "the whole burst should settle into one invalidation");
    }

    [Fact]
    public async Task Disposing_cancels_a_queued_invalidation()
    {
        // A queued invalidation must not fire against a torn-down connection.
        var spy = new InvalidationSpy();
        var sut = Create(spy.Action);

        await sut.InterceptAsync(Receive(RefreshCodeLens(true)), CancellationToken.None);
        sut.Dispose();

        await Task.Delay(DebounceSettleMs);
        spy.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_refresh_arriving_after_disposal_is_ignored()
    {
        var spy = new InvalidationSpy();
        var sut = Create(spy.Action);
        sut.Dispose();

        var result = await sut.InterceptAsync(Receive(RefreshCodeLens(true)), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        await Task.Delay(DebounceSettleMs);
        spy.Count.Should().Be(0);
    }

    [Fact]
    public async Task Disposing_twice_does_not_throw()
    {
        var sut = Create(() => { });
        await sut.InterceptAsync(Receive(RefreshCodeLens(true)), CancellationToken.None);

        var act = () => { sut.Dispose(); sut.Dispose(); };

        act.Should().NotThrow();
    }
}

internal static class JObjectTestExtensions
{
    /// <summary>Mutates and returns the object, for terse inline test fixtures.</summary>
    public static JObject Tap(this JObject obj, Action<JObject> mutate)
    {
        mutate(obj);
        return obj;
    }
}
