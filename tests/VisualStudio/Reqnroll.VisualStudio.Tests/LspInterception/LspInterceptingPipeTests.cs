using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
        // server's real response for that id arrives afterward, TryCompleteCorrelatedResponse must
        // still recognise and drop it purely from the "reqnroll-rpc-" id prefix (proof it's ours),
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
