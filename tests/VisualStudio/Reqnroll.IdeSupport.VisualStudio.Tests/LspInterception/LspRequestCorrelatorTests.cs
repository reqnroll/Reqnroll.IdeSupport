using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// Owned-RPC correlation, tested without a pipe (issue #587, step 2). Issues #401 and #555 both
/// live here: what happens to a response whose waiter is already gone, and what happens to waiters
/// when the server goes.
/// </summary>
public class LspRequestCorrelatorTests
{
    private static LspRequestCorrelator NewCorrelator() => new(NullLogger.Instance);

    private static JObject ResultResponse(string id, string resultJson) =>
        JObject.Parse($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{resultJson}}}");

    [Fact]
    public void An_issued_id_carries_the_prefix_that_proves_ownership()
    {
        // VS's own ids are always plain integers, so the prefix alone can never collide with one.
        using var pending = NewCorrelator().Begin(CancellationToken.None);

        pending.Id.Should().StartWith(LspRequestCorrelator.RequestIdPrefix);
    }

    [Fact]
    public async Task Consuming_a_response_completes_its_waiter_with_the_result()
    {
        var correlator = NewCorrelator();
        using var pending = correlator.Begin(CancellationToken.None);

        correlator.Consume(pending.Id, ResultResponse(pending.Id, "{\"targets\":[1]}"));

        var result = await pending.Response;
        result!["targets"].Should().NotBeNull();
    }

    [Fact]
    public async Task An_error_response_completes_the_waiter_with_null_rather_than_a_result()
    {
        var correlator = NewCorrelator();
        using var pending = correlator.Begin(CancellationToken.None);

        correlator.Consume(pending.Id, JObject.Parse(
            $"{{\"jsonrpc\":\"2.0\",\"id\":\"{pending.Id}\",\"error\":{{\"code\":-32601,\"message\":\"nope\"}}}}"));

        (await pending.Response).Should().BeNull();
    }

    [Fact]
    public void A_response_is_recognised_as_ours_even_after_its_waiter_is_gone()
    {
        // Issue #401: the caller's token fires (e.g. StepCodeLensService torn down mid-reconnect),
        // Dispose removes the waiter, and the server's real response lands milliseconds later.
        // Recognition must not depend on the waiter still existing — if this returned false the
        // response would be forwarded to VS, which never sent the request, and VS closes the
        // connection over exactly that.
        var correlator = NewCorrelator();
        var pending    = correlator.Begin(CancellationToken.None);
        var id         = pending.Id;
        pending.Dispose();

        LspRequestCorrelator.IsOwnedResponse(ResultResponse(id, "[]"), out var recognisedId)
            .Should().BeTrue();
        recognisedId.Should().Be(id);

        correlator.Consume(id, ResultResponse(id, "[]")); // must not throw with no waiter present
    }

    [Fact]
    public void A_vs_response_or_a_server_request_is_not_recognised_as_ours()
    {
        LspRequestCorrelator.IsOwnedResponse(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":143,\"result\":null}"), out _)
            .Should().BeFalse("VS's own numeric ids are not ours to consume");
        LspRequestCorrelator.IsOwnedResponse(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"window/showMessageRequest\"}"), out _)
            .Should().BeFalse("a request has a method, so it is not a response at all");
    }

    [Fact]
    public async Task Releasing_completes_every_waiter_promptly_with_null()
    {
        // Issue #555: without this, callers sit until their own tokens trip, which turned one dead
        // server into a stream of OperationCanceledExceptions from every CodeLens and navigation-bar
        // request for the rest of the session.
        var correlator = NewCorrelator();
        using var first  = correlator.Begin(CancellationToken.None);
        using var second = correlator.Begin(CancellationToken.None);

        correlator.ReleaseAll();

        (await first.Response).Should().BeNull();
        (await second.Response).Should().BeNull();
    }

    [Fact]
    public async Task Cancelling_faults_every_waiter_so_no_caller_hangs_past_disposal()
    {
        var correlator = NewCorrelator();
        using var pending = correlator.Begin(CancellationToken.None);

        correlator.CancelAll();

        await AssertCancelledAsync(pending);
    }

    [Fact]
    public async Task The_callers_own_token_cancels_its_waiter()
    {
        var correlator = NewCorrelator();
        using var cts = new CancellationTokenSource();
        using var pending = correlator.Begin(cts.Token);

        cts.Cancel();

        await AssertCancelledAsync(pending);
    }

    [Fact]
    public async Task A_token_already_cancelled_before_Begin_still_cancels_its_waiter()
    {
        // The race the registration ordering exists to close: registering after sending would miss a
        // token that was already cancelled at that point, leaving the waiter to hang.
        var correlator = NewCorrelator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var pending = correlator.Begin(cts.Token);

        await AssertCancelledAsync(pending);
    }

    /// <summary>Asserts that <paramref name="pending"/>'s waiter was cancelled rather than completed.</summary>
    /// <remarks>
    /// VSTHRD003 is suppressed deliberately: the deadlock it guards against needs a
    /// JoinableTaskFactory context, and there is none in these tests — the task comes from a
    /// <see cref="LspRequestCorrelator.Begin"/> call a line or two earlier in the same test.
    /// </remarks>
#pragma warning disable VSTHRD003
    private static Task AssertCancelledAsync(LspRequestCorrelator.PendingRequest pending) =>
        Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.Response);
#pragma warning restore VSTHRD003
}
