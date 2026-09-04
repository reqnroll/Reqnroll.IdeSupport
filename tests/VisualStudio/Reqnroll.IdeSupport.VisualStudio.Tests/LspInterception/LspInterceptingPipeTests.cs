using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// Regression coverage for issue #395: a response to a request the <em>old</em> VS-facing session
/// sent (e.g. the <c>shutdown</c> request <see cref="LspInterceptingPipe.CreateFreshVsFacingPipe"/>'s
/// own remarks describe) must not be misdelivered to whichever session happens to be current when
/// the response finally arrives. Confirmed live: VS's JsonRpc treats an unmatched response as a
/// fatal protocol violation and closes the brand-new connection over it — exactly what silently
/// broke inlay hints/CodeLens refresh for the rest of the session after a reconnect.
/// </summary>
public class LspInterceptingPipeTests : IAsyncLifetime
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Records every message it sees, for asserting an owned-RPC request/response was
    /// actually run through the interceptor list (issue #491) rather than bypassing it.</summary>
    private sealed class RecordingInterceptor : ILspMessageInterceptor
    {
        public List<LspMessage> Seen { get; } = new();

        public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
        {
            Seen.Add(message);
            return Task.FromResult(LspInterceptorResult.PassThrough);
        }
    }

    /// <summary>Throws on every message, to characterize that an interceptor fault never ends a pump.</summary>
    private sealed class ThrowingInterceptor : ILspMessageInterceptor
    {
        public int Calls { get; private set; }

        public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("interceptor blew up");
        }
    }

    private sealed class FakeServerPipe : IDuplexPipe
    {
        // From LspInterceptingPipe's point of view: Input = server's stdout, Output = server's stdin.
        private readonly Pipe _serverToUs = new(); // server stdout
        private readonly Pipe _usToServer = new(); // server stdin

        public PipeReader Input  => _serverToUs.Reader;
        public PipeWriter Output => _usToServer.Writer;

        /// <summary>The test's view of the server's stdin — what LspInterceptingPipe forwarded to "the server".</summary>
        public PipeReader ServerSideStdin => _usToServer.Reader;

        /// <summary>The test's view of the server's stdout — write here to simulate a server response/push.</summary>
        public PipeWriter ServerSideStdout => _serverToUs.Writer;
    }

    private LspInterceptingPipe? _pipe;

    public async Task InitializeAsync()
    {
        // Constructed per-test in each fact (needs the fake server pipe), so this only exists to
        // satisfy IAsyncLifetime's DisposeAsync cleanup below.
        await Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _pipe?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_response_for_an_abandoned_sessions_request_is_dropped_not_delivered_to_the_new_session()
    {
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        // Session #1: VS sends "shutdown" (id=143) as its last act before being abandoned —
        // mirrors RemoteLanguageClientInstance.StopAsync's real behaviour (see class remarks).
        var session1 = _pipe.CreateFreshVsFacingPipe();
        await WriteFrameAsync(session1.Output, "{\"jsonrpc\":\"2.0\",\"id\":143,\"method\":\"shutdown\"}");

        // Confirm the request actually reached "the server" before reconnecting, so the test
        // exercises the real race rather than a request that never left the pump.
        var forwarded = await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout);
        forwarded.Should().Contain("\"id\":143").And.Contain("shutdown");

        // Reconnect: a fresh session #2 pipe, matching CreateFreshVsFacingPipe (issue #156).
        var session2 = _pipe.CreateFreshVsFacingPipe();

        // The server's response to session #1's shutdown request finally arrives — after the
        // swap, exactly like the captured repro (71ms after CreateFreshVsFacingPipe — session #2).
        await WriteFrameAsync(serverSide.ServerSideStdout, "{\"jsonrpc\":\"2.0\",\"id\":143,\"result\":null}");

        // It must not reach session #2 — delivering it there is what causes VS's JsonRpc to treat
        // it as an unmatched response and fatally close the brand-new connection.
        var deliveredToSession2 = await TryReadFrameAsync(session2.Input, TimeSpan.FromMilliseconds(500));
        deliveredToSession2.Should().BeNull();
    }

    [Fact]
    public async Task A_late_response_for_a_cancelled_owned_request_is_dropped_not_delivered_to_vs()
    {
        // Regression coverage for issue #401: SendRequestToServerAsync (used by StepCodeLensService,
        // FindStepUsagesService, etc.) removes its pending-request entry as soon as its caller's
        // CancellationToken fires — e.g. cancelled mid-reconnect, same as the captured repro. If the
        // server's real response for that id arrives afterward, TryGetCorrelatedResponseId/
        // CompleteCorrelatedResponse must still recognise and drop it purely from the
        // "reqnroll-rpc-" id prefix (proof it's ours),
        // rather than letting it fall through to VS's JsonRpc — which never sent that request and
        // would treat it as the same fatal "unmatched response" protocol violation #395 fixed for
        // VS's own peer-session responses.
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session1 = _pipe.CreateFreshVsFacingPipe();

        using var requestCts = new CancellationTokenSource();
        var requestTask = _pipe.SendRequestToServerAsync("textDocument/codeLens", null, requestCts.Token);

        // Confirm the request actually reached "the server" (and capture its generated id) before
        // cancelling, so the test exercises the real race rather than a request that never left.
        var forwarded = await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout);
        forwarded.Should().Contain("textDocument/codeLens");
        var id = ExtractId(forwarded);
        id.Should().StartWith("reqnroll-rpc-");

        // Cancel it — mirrors the caller (e.g. StepCodeLensService) being torn down mid-reconnect —
        // which removes the pending-request entry via SendRequestToServerAsync's finally block.
        requestCts.Cancel();
        (await requestTask).Should().BeNull();

        // The server's real response for that same id arrives anyway, shortly after cancellation —
        // exactly like the captured repro (server replied ~25ms after we'd already cancelled).
        await WriteFrameAsync(serverSide.ServerSideStdout, $"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":[]}}");

        // It must not reach VS — forwarding it would hand VS's JsonRpc an unmatched response and
        // fatally close the connection.
        var deliveredToVs = await TryReadFrameAsync(session1.Input, TimeSpan.FromMilliseconds(500));
        deliveredToVs.Should().BeNull();
    }

    [Fact]
    public async Task A_response_for_the_current_sessions_request_is_still_delivered_normally()
    {
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session1 = _pipe.CreateFreshVsFacingPipe();
        await WriteFrameAsync(session1.Output, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");

        await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout); // drain the forwarded request

        // No reconnect this time — the response belongs to the still-current session.
        await WriteFrameAsync(serverSide.ServerSideStdout, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");

        var delivered = await ReadFrameAsync(session1.Input, ShortTimeout);
        delivered.Should().Contain("\"id\":1");
    }

    [Fact]
    public async Task An_owned_rpc_request_and_its_response_are_both_run_through_the_interceptors()
    {
        // Regression coverage for issue #491: a request injected by SendRequestToServerAsync (e.g.
        // ScenarioTestTargetService's reqnroll/resolveTestTargets) and the response consumed for it
        // must both reach the interceptor list — that's how LspInspectorLogger sees this traffic —
        // even though the response is never forwarded on to VS. Before this fix, neither direction
        // ran through the interceptors at all, so this owned-RPC channel was invisible to the
        // inspector log regardless of how much traffic crossed it.
        var sendInterceptor = new RecordingInterceptor();
        var receiveInterceptor = new RecordingInterceptor();
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, new ILspMessageInterceptor[] { sendInterceptor }, new ILspMessageInterceptor[] { receiveInterceptor },
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        _pipe.CreateFreshVsFacingPipe();

        var requestTask = _pipe.SendRequestToServerAsync("reqnroll/resolveTestTargets", "{}", CancellationToken.None);

        var forwarded = await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout);
        var id = ExtractId(forwarded);

        await WriteFrameAsync(serverSide.ServerSideStdout, $"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{\"targets\":[]}}}}");

        await requestTask;

        sendInterceptor.Seen.Should().ContainSingle(m =>
            m.Direction == LspMessageDirection.Send && m.Method == "reqnroll/resolveTestTargets");
        receiveInterceptor.Seen.Should().ContainSingle(m =>
            m.Direction == LspMessageDirection.Receive && m.Id!.Value<string>() == id);
    }

    // ── Session termination (issue #555) ─────────────────────────────────────────────────────
    //
    // VS ends an LSP session with `shutdown` then `exit`, and it does that on a solution close, not
    // only at IDE shutdown. `exit` reaches the real server, which terminates — so everything after
    // it on this connection is talking to a corpse. Captured live: VS's `exit` went out at +102ms,
    // our own StepCodeLens/navigation-bar requests kept being injected at +142ms and +224ms and were
    // never answered, and VS's `initialize` for the new session at +419ms hung forever.

    [Fact]
    public async Task Exit_forwarded_to_the_server_marks_the_connection_terminated()
    {
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session = _pipe.CreateFreshVsFacingPipe();
        _pipe.ServerTerminated.Should().BeFalse();

        await WriteFrameAsync(session.Output, "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}");

        // `exit` is still honoured — the server is meant to receive it and terminate.
        var forwarded = await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout);
        forwarded.Should().Contain("exit");

        await WaitForAsync(() => _pipe.ServerTerminated, ShortTimeout);
    }

    [Fact]
    public async Task Exit_carrying_an_explicit_json_null_id_still_terminates_the_connection()
    {
        // JObject["id"] returns a JTokenType.Null token for "id":null, not a C# null, so a check for
        // the latter alone would miss this frame — leaving the connection looking alive after its
        // server had been told to leave, which is the exact failure the detection exists to prevent.
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session = _pipe.CreateFreshVsFacingPipe();
        await WriteFrameAsync(session.Output, "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"exit\"}");
        await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout);

        await WaitForAsync(() => _pipe.ServerTerminated, ShortTimeout);
    }

    [Fact]
    public async Task Shutdown_alone_does_not_terminate_the_connection()
    {
        // Only `exit` ends the process. A `shutdown` with no `exit` after it leaves a server that is
        // still there to answer — treating it as gone would throw away a usable connection.
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session = _pipe.CreateFreshVsFacingPipe();
        await WriteFrameAsync(session.Output, "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"shutdown\"}");
        await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout);

        _pipe.ServerTerminated.Should().BeFalse();
    }

    [Fact]
    public async Task An_injected_request_after_exit_returns_promptly_instead_of_awaiting_a_dead_server()
    {
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session = _pipe.CreateFreshVsFacingPipe();
        await WriteFrameAsync(session.Output, "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}");
        await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout);
        await WaitForAsync(() => _pipe.ServerTerminated, ShortTimeout);

        // Uncancelled token on purpose: before this fix the only thing that ended such a request was
        // the caller's own token, which is why the log filled with OperationCanceledException.
        var result = await WithTimeoutAsync(
            _pipe.SendRequestToServerAsync("textDocument/codeLens", null, CancellationToken.None), ShortTimeout);

        result.Should().BeNull();
        (await TryReadFrameAsync(serverSide.ServerSideStdin, TimeSpan.FromMilliseconds(300)))
            .Should().BeNull("nothing should be written to a server that has been told to exit");
    }

    [Fact]
    public async Task An_injected_notification_after_exit_is_not_written_to_the_server()
    {
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session = _pipe.CreateFreshVsFacingPipe();
        await WriteFrameAsync(session.Output, "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}");
        await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout);
        await WaitForAsync(() => _pipe.ServerTerminated, ShortTimeout);

        await _pipe.SendNotificationToServerAsync("reqnroll/projectLoaded", "{}", CancellationToken.None);

        (await TryReadFrameAsync(serverSide.ServerSideStdin, TimeSpan.FromMilliseconds(300)))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_request_already_in_flight_is_released_when_the_server_terminates()
    {
        // The captured repro's requests were sent *before* the server finished exiting, so they were
        // already waiting on a response that would never come. Marking the connection terminated has
        // to release those too, not just refuse new ones.
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var requestTask = _pipe.SendRequestToServerAsync("reqnroll/documentSymbolHierarchical", null, CancellationToken.None);
        var forwarded = await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout);
        forwarded.Should().Contain("reqnroll/documentSymbolHierarchical");

        _pipe.MarkServerTerminated("test");

        (await WithTimeoutAsync(requestTask, ShortTimeout)).Should().BeNull();
    }

    // ── Characterization coverage taken before the #587 phase-2/3 extractions ─────────────────
    //
    // These pin behaviour that the decomposition must preserve but that nothing asserted before.
    // Two of the invariants in the design's table (I7: the receive pump must never exit; I8: injected
    // writes must not interleave with the send pump's) had no coverage at all, and the #395 shape was
    // only tested one generation deep. They are written against the *current* implementation and are
    // expected to pass unchanged through every pure-move commit; one of them (the purge-boundary test
    // below) deliberately records a latent defect rather than the desired behaviour.

    [Fact]
    public async Task Owned_and_vs_responses_arriving_together_each_reach_only_their_own_requester()
    {
        // #395 and #401 in one shape: an owned RPC and one of VS's own requests in flight at the same
        // time, answered out of order. The owned response must be consumed (never forwarded), and
        // VS's must be forwarded (never swallowed) -- the two policies meeting on one connection.
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session = _pipe.CreateFreshVsFacingPipe();

        var ownedTask = _pipe.SendRequestToServerAsync("reqnroll/resolveTestTargets", "{}", CancellationToken.None);
        var ownedId   = ExtractId(await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout));

        await WriteFrameAsync(session.Output, "{\"jsonrpc\":\"2.0\",\"id\":77,\"method\":\"textDocument/codeLens\"}");
        (await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout)).Should().Contain("\"id\":77");

        // Answered in the reverse order they were sent, which is what the server is free to do.
        await WriteFrameAsync(serverSide.ServerSideStdout, "{\"jsonrpc\":\"2.0\",\"id\":77,\"result\":[]}");
        await WriteFrameAsync(serverSide.ServerSideStdout, $"{{\"jsonrpc\":\"2.0\",\"id\":\"{ownedId}\",\"result\":{{\"targets\":[1]}}}}");

        var ownedResult = await WithTimeoutAsync(ownedTask, ShortTimeout);
        ownedResult.Should().NotBeNull("the owned response must complete its own waiter");
        ownedResult!["targets"].Should().NotBeNull();

        // VS gets its own response and only its own: the owned one must never reach VS's JsonRpc.
        var toVs = await ReadFrameAsync(session.Input, ShortTimeout);
        toVs.Should().Contain("\"id\":77");
        (await TryReadFrameAsync(session.Input, TimeSpan.FromMilliseconds(300)))
            .Should().BeNull("the owned response was consumed, so nothing else should reach VS");
    }

    [Fact]
    public async Task A_response_for_a_session_abandoned_two_generations_ago_is_dropped()
    {
        // This assertion was inverted deliberately (design §3.5). It previously recorded a latent
        // recurrence of #395: CreateFreshVsFacingPipe purges request->session entries older than two
        // generations, and the old guard only fired when the entry was *found*, so a response whose
        // entry had been purged fell through and was forwarded to the current session -- the same
        // unmatched response #395 exists to prevent, delayed by one more generation. The router now
        // delivers a response only when its id is tracked against the current session.
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session1 = _pipe.CreateFreshVsFacingPipe();
        await WriteFrameAsync(session1.Output, "{\"jsonrpc\":\"2.0\",\"id\":143,\"method\":\"shutdown\"}");
        (await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout)).Should().Contain("\"id\":143");

        _pipe.CreateFreshVsFacingPipe();                       // session #2
        var session3 = _pipe.CreateFreshVsFacingPipe();        // session #3 -- purges session #1's entry

        await WriteFrameAsync(serverSide.ServerSideStdout, "{\"jsonrpc\":\"2.0\",\"id\":143,\"result\":null}");

        var deliveredToSession3 = await TryReadFrameAsync(session3.Input, TimeSpan.FromMilliseconds(500));
        deliveredToSession3.Should().BeNull(
            "session #3 never sent id=143, and forwarding it there is the RemoteProtocolViolation " +
            "that fatally closes the connection -- purged or not, the response has no correct destination");
    }

    [Fact]
    public async Task The_receive_pump_survives_an_interceptor_that_throws_and_keeps_delivering()
    {
        // I7: the receive pump is persistent and shared by every future VS session, so nothing short
        // of the server's stdout ending may stop it.
        var throwing = new ThrowingInterceptor();
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), new ILspMessageInterceptor[] { throwing },
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session = _pipe.CreateFreshVsFacingPipe();

        await WriteFrameAsync(serverSide.ServerSideStdout, "{\"jsonrpc\":\"2.0\",\"method\":\"window/logMessage\"}");
        (await ReadFrameAsync(session.Input, ShortTimeout)).Should().Contain("window/logMessage");

        await WriteFrameAsync(serverSide.ServerSideStdout, "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/publishDiagnostics\"}");
        (await ReadFrameAsync(session.Input, ShortTimeout)).Should().Contain("publishDiagnostics");

        throwing.Calls.Should().Be(2, "both frames still ran through the interceptor list");
    }

    [Fact]
    public async Task The_receive_pump_survives_a_malformed_header_from_the_server_and_keeps_delivering()
    {
        // I7 again, via the case step 2.0 made survivable: a header block with no usable
        // Content-Length used to escape the codec as an exception and end this pump for good.
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session = _pipe.CreateFreshVsFacingPipe();

        await serverSide.ServerSideStdout.WriteAsync(Encoding.UTF8.GetBytes("Content-Length: -1\r\n\r\n"));
        await WriteFrameAsync(serverSide.ServerSideStdout, "{\"jsonrpc\":\"2.0\",\"method\":\"window/logMessage\"}");

        var delivered = await ReadFrameAsync(session.Input, ShortTimeout);
        delivered.Should().Contain("window/logMessage", "one corrupt header must cost one frame, not the pump");
    }

    [Fact]
    public async Task The_receive_pump_keeps_delivering_after_a_session_is_abandoned()
    {
        // I7 + I1: the pump looks up the *current* VS-facing writer per frame rather than capturing
        // one, so abandoning a session (whose VS-side reader is gone) must not disturb it.
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session1 = _pipe.CreateFreshVsFacingPipe();
        await session1.Input.CompleteAsync();   // VS-side reader goes away, as at the end of a session

        var session2 = _pipe.CreateFreshVsFacingPipe();
        await WriteFrameAsync(serverSide.ServerSideStdout, "{\"jsonrpc\":\"2.0\",\"method\":\"window/logMessage\"}");

        (await ReadFrameAsync(session2.Input, ShortTimeout)).Should().Contain("window/logMessage");
    }

    [Fact]
    public async Task Injected_traffic_and_send_pump_traffic_never_interleave_on_the_server_stream()
    {
        // I8: SendNotificationToServerAsync/SendRequestToServerAsync write straight to the server's
        // stdin from arbitrary threads, on the same unsynchronised PipeWriter the send pump forwards
        // VS's own frames to. Without the inject lock the two can interleave mid-frame and corrupt
        // the framing -- which would show up here as a frame that does not parse, or a lost one.
        const int injectedCount = 40;
        const int vsCount       = 40;

        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session = _pipe.CreateFreshVsFacingPipe();

        var injecting = Task.Run(async () =>
        {
            for (var i = 0; i < injectedCount; i++)
                await _pipe.SendNotificationToServerAsync("reqnroll/injected", $"{{\"n\":{i}}}", CancellationToken.None);
        });

        var vsWriting = Task.Run(async () =>
        {
            for (var i = 0; i < vsCount; i++)
                await WriteFrameAsync(session.Output, $"{{\"jsonrpc\":\"2.0\",\"method\":\"vs/notification\",\"params\":{{\"n\":{i}}}}}");
        });

        await Task.WhenAll(injecting, vsWriting);

        var injectedSeen = 0;
        var vsSeen       = 0;
        for (var i = 0; i < injectedCount + vsCount; i++)
        {
            var frame = await TryReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout);
            frame.Should().NotBeNull($"every one of the {injectedCount + vsCount} frames should arrive intact");

            // Parsing is the real assertion: an interleaved write produces a body that is not valid
            // JSON (or a Content-Length that no longer matches its payload).
            var body = JObject.Parse(frame!);
            var method = body["method"]!.Value<string>();
            if (method == "reqnroll/injected") injectedSeen++;
            else if (method == "vs/notification") vsSeen++;
        }

        injectedSeen.Should().Be(injectedCount);
        vsSeen.Should().Be(vsCount);
    }

    [Fact]
    public async Task Late_responses_arriving_after_termination_are_handled_without_reaching_vs_wrongly()
    {
        // I5/I6 interaction: MarkServerTerminated releases the in-flight owned request, and a
        // response that still turns up afterwards must not be forwarded to VS -- the "reqnroll-rpc-"
        // prefix proves it is ours whether or not a waiter survives (#401). VS's own outstanding
        // response is a different matter: VS asked for it and is still listening, so it is delivered.
        var serverSide = new FakeServerPipe();
        _pipe = new LspInterceptingPipe(
            serverSide, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance);
        await _pipe.StartAsync(CancellationToken.None);

        var session = _pipe.CreateFreshVsFacingPipe();

        var ownedTask = _pipe.SendRequestToServerAsync("textDocument/codeLens", null, CancellationToken.None);
        var ownedId   = ExtractId(await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout));

        await WriteFrameAsync(session.Output, "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"shutdown\"}");
        await ReadFrameAsync(serverSide.ServerSideStdin, ShortTimeout);

        _pipe.MarkServerTerminated("test");
        (await WithTimeoutAsync(ownedTask, ShortTimeout)).Should().BeNull();

        await WriteFrameAsync(serverSide.ServerSideStdout, $"{{\"jsonrpc\":\"2.0\",\"id\":\"{ownedId}\",\"result\":[]}}");
        await WriteFrameAsync(serverSide.ServerSideStdout, "{\"jsonrpc\":\"2.0\",\"id\":12,\"result\":null}");

        // The owned one is dropped; VS's shutdown response still gets through, and is the only frame.
        var delivered = await ReadFrameAsync(session.Input, ShortTimeout);
        delivered.Should().Contain("\"id\":12");
        (await TryReadFrameAsync(session.Input, TimeSpan.FromMilliseconds(300)))
            .Should().BeNull("the owned response must never reach VS's JsonRpc");
    }

    /// <summary>Awaits <paramref name="task"/>, failing the test if it does not finish in time.</summary>
    /// <remarks>
    /// VSTHRD003 is suppressed deliberately: the deadlock it guards against needs a JoinableTaskFactory
    /// context, and there is none in these tests — the task is started by the same test method a line
    /// or two earlier and is awaited here only so a hang fails as an assertion instead of stalling the
    /// run, which is precisely the regression under test.
    /// </remarks>
#pragma warning disable VSTHRD003
    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        completed.Should().BeSameAs(task, "the call should return without waiting on a dead server");
        return await task;
    }
#pragma warning restore VSTHRD003

    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> elapses.</summary>
    /// <remarks>
    /// The flag is set by the send pump on its own thread just after the frame is forwarded, so the
    /// test cannot assert it synchronously off the back of the read that observed the frame.
    /// </remarks>
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the condition should hold within the timeout");
    }

    // ── Minimal LSP frame read/write helpers (Content-Length: N\r\n\r\nBODY) ──────────────────

    /// <summary>Pulls the <c>"id":"..."</c> value out of a raw JSON-RPC frame body.</summary>
    private static string ExtractId(string json)
    {
        const string marker = "\"id\":\"";
        var start = json.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the frame should carry a string id");
        start += marker.Length;
        var end = json.IndexOf('"', start);
        return json.Substring(start, end - start);
    }

    private static async Task WriteFrameAsync(PipeWriter writer, string json)
    {
        var bodyBytes   = Encoding.UTF8.GetBytes(json);
        var headerBytes = Encoding.UTF8.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
        await writer.WriteAsync(headerBytes);
        await writer.WriteAsync(bodyBytes);
    }

    private static async Task<string> ReadFrameAsync(PipeReader reader, TimeSpan timeout)
    {
        var result = await TryReadFrameAsync(reader, timeout);
        result.Should().NotBeNull("a frame was expected within the timeout");
        return result!;
    }

    /// <summary>Reads one frame, or returns <see langword="null"/> if nothing arrives within <paramref name="timeout"/>.</summary>
    private static async Task<string?> TryReadFrameAsync(PipeReader reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            int contentLength;
            int headerLength;
            ReadOnlySequence<byte> headerBuffer;

            while (true)
            {
                var readResult = await reader.ReadAsync(cts.Token).ConfigureAwait(false);
                headerBuffer = readResult.Buffer;
                if (TryParseHeader(headerBuffer, out contentLength, out headerLength))
                {
                    reader.AdvanceTo(headerBuffer.GetPosition(headerLength));
                    break;
                }
                reader.AdvanceTo(headerBuffer.Start, headerBuffer.End);
                if (readResult.IsCompleted)
                    return null;
            }

            var body = new byte[contentLength];
            var read = 0;
            while (read < contentLength)
            {
                var readResult = await reader.ReadAsync(cts.Token).ConfigureAwait(false);
                var buffer = readResult.Buffer;
                var take = (int)Math.Min(contentLength - read, buffer.Length);
                buffer.Slice(0, take).CopyTo(body.AsSpan(read, take));
                read += take;
                reader.AdvanceTo(buffer.GetPosition(take));
            }

            return Encoding.UTF8.GetString(body);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static bool TryParseHeader(ReadOnlySequence<byte> buffer, out int contentLength, out int headerLength)
    {
        contentLength = 0;
        headerLength  = 0;

        var bytes = buffer.IsSingleSegment ? buffer.First.Span.ToArray() : buffer.ToArray();
        for (int i = 0; i <= bytes.Length - 4; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                var headerText = Encoding.UTF8.GetString(bytes, 0, i);
                foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength))
                    {
                        headerLength = i + 4;
                        return true;
                    }
                }
            }
        }
        return false;
    }
}
