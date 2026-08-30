using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="DocumentActivationTrackingInterceptor"/> drives <see cref="DocumentActivationState"/>
/// from <c>didOpen</c>/<c>didClose</c> traffic. The one case where it needs to inject a message
/// itself (activation raced ahead of <c>didOpen</c>) requires a real <c>LspInterceptingPipe</c>,
/// which is VS-bound plumbing not constructed in these tests — mirroring
/// <see cref="ScaffoldTrackingInterceptorTests"/>, we verify the observable contract that doesn't
/// require it: message classification, state-machine wiring, and safety when the pipe is
/// unavailable.
/// </summary>
public class DocumentActivationTrackingInterceptorTests
{
    private static DocumentActivationTrackingInterceptor Create(DocumentActivationState state) =>
        new(state, getPipe: () => null, logger: NullLogger<DocumentActivationTrackingInterceptor>.Instance);

    private static LspMessage Send(JObject body) => new(LspMessageDirection.Send, body, DateTimeOffset.Now);

    private static JObject DidOpen(string uri) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"]  = "textDocument/didOpen",
        ["params"]  = new JObject { ["textDocument"] = new JObject { ["uri"] = uri } },
    };

    private static JObject DidClose(string uri) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"]  = "textDocument/didClose",
        ["params"]  = new JObject { ["textDocument"] = new JObject { ["uri"] = uri } },
    };

    [Fact]
    public async Task DidOpen_for_a_feature_file_with_no_prior_activation_passes_through()
    {
        var result = await Create(new DocumentActivationState()).InterceptAsync(
            Send(DidOpen("file:///c:/w/Calculator.feature")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task DidOpen_for_a_non_feature_file_passes_through_untouched()
    {
        var state = new DocumentActivationState();
        state.OnWindowActivated(@"c:\w\Steps.cs");

        var result = await Create(state).InterceptAsync(
            Send(DidOpen("file:///c:/w/Steps.cs")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task DidClose_for_a_feature_file_passes_through()
    {
        var result = await Create(new DocumentActivationState()).InterceptAsync(
            Send(DidClose("file:///c:/w/Calculator.feature")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task An_unrelated_send_message_passes_through()
    {
        var result = await Create(new DocumentActivationState()).InterceptAsync(
            Send(new JObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/didChange" }),
            CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task Activation_pending_didOpen_is_safe_when_the_pipe_is_unavailable()
    {
        const string uri  = "file:///c:/w/Calculator.feature";
        const string path = @"c:\w\Calculator.feature";
        var state = new DocumentActivationState();
        state.OnWindowActivated(path); // activation races ahead of didOpen

        var sut = Create(state);
        var act = async () => await sut.InterceptAsync(Send(DidOpen(uri)), CancellationToken.None);

        // Pipe is null (not yet constructed), so the interceptor must degrade to a plain
        // passthrough rather than throwing — same contract ScaffoldTrackingInterceptor has for
        // its own "monitor unavailable" case.
        (await act.Should().NotThrowAsync()).Which.Should().Be(LspInterceptorResult.PassThrough);
    }

    /// <summary>Minimal <see cref="IDuplexPipe"/> over a pair of in-memory <see cref="Pipe"/> ends.</summary>
    private sealed class TestDuplexPipe : IDuplexPipe
    {
        public TestDuplexPipe(PipeReader input, PipeWriter output)
        {
            Input  = input;
            Output = output;
        }

        public PipeReader Input  { get; }
        public PipeWriter Output { get; }
    }

    [Fact]
    public async Task Activation_pending_didOpen_does_not_corrupt_state_via_the_pipes_own_reentrant_logging_call()
    {
        // Issue #187: LspInterceptingPipe.SendNotificationToServerAsync (used below to re-forward
        // didOpen) re-runs the full send-interceptor pipeline on a synthetic copy of what it just
        // wrote, purely so the injected message shows up in the inspector log — which calls this
        // very interceptor's InterceptAsync a second time, re-entrantly, on the same didOpen.
        // Without the _selfForwardedPaths guard, that second call would run OnDidOpen again and
        // (since phase is already Activated) incorrectly reset it back to Opened.
        const string uri  = "file:///c:/w/Calculator.feature";
        const string path = @"c:\w\Calculator.feature";

        var state = new DocumentActivationState();
        state.OnWindowActivated(path); // activation races ahead of didOpen -> ActivationPending

        LspInterceptingPipe? pipe = null;
        var sut = new DocumentActivationTrackingInterceptor(
            state, getPipe: () => pipe, logger: NullLogger<DocumentActivationTrackingInterceptor>.Instance);

        var serverSidePipe = new Pipe();
        var serverDuplex   = new TestDuplexPipe(serverSidePipe.Reader, serverSidePipe.Writer);
        pipe = new LspInterceptingPipe(
            serverDuplex,
            sendInterceptors: new ILspMessageInterceptor[] { sut },
            receiveInterceptors: Array.Empty<ILspMessageInterceptor>(),
            logger: NullLogger<LspInterceptingPipe>.Instance);

        var result = await sut.InterceptAsync(Send(DidOpen(uri)), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.Consume);

        // If the reentrant call had wrongly reset phase from Activated back to Opened, this
        // would return SendNow instead of None (see DocumentActivationState.OnWindowActivated's
        // Activated case).
        state.OnWindowActivated(path).Should().Be(DocumentActivationAction.None);
    }
}
