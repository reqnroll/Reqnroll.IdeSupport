using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Inserts an interception pipeline between VS and the LSP server process.
/// </summary>
/// <remarks>
/// <para>
/// Sits in the middle of the LSP stdio channel:
/// <code>
///   VS  ──write──► [VS-facing PipeReader/Writer]
///                        │                  ▲
///                  SendPump task      ReceivePump task
///                        │                  │
///                        ▼                  │
///              Server stdin PipeWriter   Server stdout PipeReader
/// </code>
/// </para>
/// <para>
/// Each pump reads raw LSP frames (<c>Content-Length: N\r\n\r\nBODY</c>), parses them to
/// <see cref="LspMessage"/>, runs the relevant interceptor list, then — if no interceptor
/// consumed the message — re-encodes and forwards.
/// </para>
/// <para>
/// <b>Issue #156:</b> VS.Extensibility can call <c>ReqnrollLanguageClient.CreateServerConnectionAsync</c>
/// more than once in a session — decompiling VS's own LSP client host confirmed this is by design
/// (extension hot-reload support), not a bug on VS's side, and every Microsoft sample builds a fresh
/// <c>IDuplexPipe</c> per call rather than caching one. This type now supports that: the single,
/// persistent <see cref="_serverPipe"/> connection to the actual server process never changes, but
/// the local, in-memory "VS-facing" <see cref="Pipe"/> pair is recreated on every
/// <see cref="CreateFreshVsFacingPipe"/> call. The always-running receive pump (server → VS
/// direction) looks up the *current* VS-facing writer per frame rather than capturing one for its
/// whole lifetime, and a fresh, session-scoped send pump (VS → server direction) is started for each
/// new VS-facing pipe, replacing (and cancelling) the previous one. See that method's remarks for
/// the abandoned-session cleanup.
/// </para>
/// </remarks>
internal sealed class LspInterceptingPipe : IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly IDuplexPipe                       _serverPipe;
    private readonly IReadOnlyList<ILspMessageInterceptor> _sendInterceptors;
    private readonly IReadOnlyList<ILspMessageInterceptor> _receiveInterceptors;
    private readonly ILogger<LspInterceptingPipe>       _logger;

    // The two Pipe objects whose Reader/Writer ends form the *current* VS-facing IDuplexPipe.
    // VS reads from _toVsPipe.Reader; VS writes to _fromVsPipe.Writer. Replaced wholesale by
    // CreateFreshVsFacingPipe on every CreateServerConnectionAsync call (issue #156) -- guarded by
    // _vsPipeSwapLock since the persistent receive pump reads the current _toVsPipe reference
    // concurrently with swaps happening on VS's calling thread.
    private Pipe _toVsPipe   = new Pipe();   // server → VS direction
    private Pipe _fromVsPipe = new Pipe();   // VS → server direction
    private readonly object _vsPipeSwapLock = new object();

    // Serialises injected writes against the send pump so frames are not interleaved.
    private readonly SemaphoreSlim _injectLock = new SemaphoreSlim(1, 1);

    // ── Owned request/response correlation ─────────────────────────────────
    // Requests injected by us use a string id with this prefix so they never collide
    // with VS's own numeric JSON-RPC ids.  The receive pump recognises the prefix and
    // consumes the response before it can be forwarded to VS (which never sent the request).
    private const string RequestIdPrefix = "reqnroll-rpc-";
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JToken?>> _pendingRequests
        = new ConcurrentDictionary<string, TaskCompletionSource<JToken?>>();

    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private CancellationTokenSource? _linkedCts;
    private CancellationToken        _lifetimeToken;
    private Task?                    _receivePump;

    // The current VS-facing session's send pump + its own cancellation, replaced on every
    // CreateFreshVsFacingPipe call. Guarded by _vsPipeSwapLock alongside the Pipe fields above.
    private CancellationTokenSource? _currentSendPumpCts;
    private Task?                    _currentSendPump;
    private int                      _sessionCounter;

    private bool _disposed;

    /// <summary>
    /// Initialises the intercepting pipe but does not start pumping yet.
    /// Call <see cref="StartAsync"/> to begin.
    /// </summary>
    /// <param name="serverPipe">
    /// The raw <see cref="IDuplexPipe"/> connected to the server process's stdio.
    /// </param>
    /// <param name="sendInterceptors">
    /// Interceptors applied to messages travelling VS → Server.
    /// </param>
    /// <param name="receiveInterceptors">
    /// Interceptors applied to messages travelling Server → VS.
    /// </param>
    /// <param name="logger">Logging sink for pump-level diagnostics.</param>
    public LspInterceptingPipe(
        IDuplexPipe serverPipe,
        IReadOnlyList<ILspMessageInterceptor> sendInterceptors,
        IReadOnlyList<ILspMessageInterceptor> receiveInterceptors,
        ILogger<LspInterceptingPipe> logger)
    {
        _serverPipe          = serverPipe          ?? throw new ArgumentNullException(nameof(serverPipe));
        _sendInterceptors    = sendInterceptors    ?? throw new ArgumentNullException(nameof(sendInterceptors));
        _receiveInterceptors = receiveInterceptors ?? throw new ArgumentNullException(nameof(receiveInterceptors));
        _logger              = logger              ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts the persistent receive pump (server → VS direction, bound to the real server
    /// process's stdout for the lifetime of this instance). Returns immediately; runs until the
    /// connection closes or <see cref="Dispose"/> is called. Does <b>not</b> start a send pump or
    /// hand back a VS-facing pipe — call <see cref="CreateFreshVsFacingPipe"/> for that, once per
    /// <c>CreateServerConnectionAsync</c> call.
    /// </summary>
    public Task StartAsync(CancellationToken externalCancellation)
    {
        _linkedCts     = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalCancellation);
        _lifetimeToken = _linkedCts.Token;

        // Server stdout → ReceivePump reads _serverPipe.Input → current _toVsPipe.Writer → VS
        // reads that pipe's Reader. The destination is looked up per frame (GetCurrentToVsWriter)
        // rather than captured once, since CreateFreshVsFacingPipe can swap it out from under this
        // long-running pump at any time.
        _receivePump = ReceivePumpAsync(_lifetimeToken);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a brand-new local VS-facing <see cref="Pipe"/> pair and returns the
    /// <see cref="IDuplexPipe"/> to hand to VS as the <c>CreateServerConnectionAsync</c> result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #156: VS can call <c>CreateServerConnectionAsync</c> more than once per session. The
    /// previous implementation cached and returned the exact same <c>IDuplexPipe</c> every time,
    /// which is fine if VS never calls twice but corrupts the connection the moment it does — a
    /// second consumer writing to a <see cref="PipeWriter"/> the first consumer's disposal already
    /// completed throws <c>InvalidOperationException: Writing is not allowed after writer was
    /// completed</c>, exactly what was observed. This method instead gives every call a fresh,
    /// never-before-used pipe pair, matching every Microsoft sample's
    /// <c>CreateServerConnectionAsync</c> implementation (each builds a fresh
    /// <c>FullDuplexStream.CreatePair()</c> inline, never caches).
    /// </para>
    /// <para>
    /// The real server process connection (<see cref="_serverPipe"/>) is untouched by this — only
    /// the local, in-memory relay pipes change. The previous session's send pump (VS → server) is
    /// cancelled; its abandoned <c>_toVsPipe.Writer</c> (server → VS; ours to complete) is completed
    /// so a lingering VS-side reader gets a clean EOF instead of an error. We don't touch the
    /// abandoned <c>_fromVsPipe.Writer</c> (VS → server) since VS owns that end, not us.
    /// </para>
    /// </remarks>
    public IDuplexPipe CreateFreshVsFacingPipe()
    {
        var newToVsPipe   = new Pipe();
        var newFromVsPipe = new Pipe();
        var newSendPumpCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);

        Pipe?                     oldToVsPipe;
        CancellationTokenSource?  oldSendPumpCts;
        int                       sessionId;

        lock (_vsPipeSwapLock)
        {
            oldToVsPipe    = _toVsPipe;
            oldSendPumpCts = _currentSendPumpCts;

            _toVsPipe            = newToVsPipe;
            _fromVsPipe          = newFromVsPipe;
            _currentSendPumpCts  = newSendPumpCts;
            sessionId            = ++_sessionCounter;
        }

        // Abandon the previous session: stop its send pump and let any lingering VS-side reader
        // of the old server→VS pipe see a clean end-of-stream rather than an error.
        oldSendPumpCts?.Cancel();
        oldSendPumpCts?.Dispose();
        try
        {
            oldToVsPipe?.Writer.Complete();
        }
        catch (Exception ex)
        {
            // Benign: e.g. already completed by a prior call, or a race with an in-flight
            // ReceivePumpAsync write to the pipe we're abandoning right now (see that method's
            // remarks on tolerating a stale-destination write failure).
            _logger.LogDebug(ex, "LspInterceptingPipe: completing the abandoned server→VS pipe threw (benign).");
        }

        _logger.LogInformation(
            "LspInterceptingPipe: CreateFreshVsFacingPipe — session #{SessionId} (issue #156: no longer " +
            "handing back a cached, possibly-dead pipe on repeat CreateServerConnectionAsync calls).",
            sessionId);

        // VS → server direction for this session only. lockDestination: true -- this pump's
        // destination (_serverPipe.Output) is the same stream SendNotificationToServerAsync/
        // SendRequestToServerAsync inject into from other threads.
        _currentSendPump = SendPumpAsync(newFromVsPipe.Reader, sessionId, newSendPumpCts.Token);

        return new DuplexPipeAdapter(newToVsPipe.Reader, newFromVsPipe.Writer);
    }

    private PipeWriter GetCurrentToVsWriter()
    {
        lock (_vsPipeSwapLock)
        {
            return _toVsPipe.Writer;
        }
    }

    // ── Pump loops ───────────────────────────────────────────────────────────

    /// <summary>
    /// Persistent, single-instance pump for the server → VS direction: reads
    /// <see cref="_serverPipe"/>'s real stdout for the lifetime of this object, forwarding each
    /// frame to whichever VS-facing pipe is <i>current</i> at that moment (see
    /// <see cref="GetCurrentToVsWriter"/>). Must never exit while the server process is alive --
    /// unlike <see cref="SendPumpAsync"/>, a failure here would silently stop relaying server output
    /// to every future VS session, not just the current one. A write failure against a
    /// possibly-stale destination (e.g. a race with <see cref="CreateFreshVsFacingPipe"/> completing
    /// the pipe this frame was about to be written to) is therefore logged and tolerated rather than
    /// treated as pump-ending.
    /// </summary>
    private async Task ReceivePumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadNextFrameAsync(_serverPipe.Input, ct).ConfigureAwait(false);
                if (frame is null)
                    break; // server process ended its stdout -- genuinely fatal, nothing more to relay.

                var body = frame.Body;
                if (body is null)
                {
                    // Malformed JSON — forward raw bytes verbatim so the connection stays alive.
                    await ForwardToCurrentVsWriterAsync(frame.RawBytes, ct).ConfigureAwait(false);
                    continue;
                }

                // OmniSharp's DocumentUri unconditionally lowercases drive letters, but VS
                // tracks documents using the project system's original (upper-case) casing.
                // Normalize here — before correlation and before any interceptor sees the
                // message — so every server→VS path (owned-RPC responses consumed below,
                // and messages forwarded on to VS's own LSP client) gets a VS-matching URI.
                // Guarded like an interceptor (see RunInterceptorsAsync): a bug here must
                // degrade to "URI casing unfixed for this message", never sever the pipe —
                // and unlike an interceptor fault, this runs before LspInspectorLogger sees
                // the message, so a silent failure here would leave no trace in the wire log.
                var rawBytes = frame.RawBytes;
                try
                {
                    if (DriveLetterUriNormalizer.NormalizeInPlace(body))
                        rawBytes = EncodeFrame(body);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "LspInterceptingPipe: DriveLetterUriNormalizer threw on message {Body}",
                        body.ToString());
                }

                // Consume correlated responses before external interceptors so they never reach VS.
                if (TryCompleteCorrelatedResponse(body))
                    continue;

                var message = new LspMessage(LspMessageDirection.Receive, body, DateTimeOffset.Now);
                var result  = await RunInterceptorsAsync(message, _receiveInterceptors, ct).ConfigureAwait(false);

                if (result == LspInterceptorResult.PassThrough)
                    await ForwardToCurrentVsWriterAsync(rawBytes, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LspInterceptingPipe [Receive] pump faulted.");
        }
        finally
        {
            // Complete whichever VS-facing pipe is current at shutdown time; abandoned earlier
            // sessions were already completed individually by CreateFreshVsFacingPipe.
            try { await GetCurrentToVsWriter().CompleteAsync().ConfigureAwait(false); }
            catch { /* best-effort at shutdown */ }
        }
    }

    /// <summary>
    /// Forwards one already-decoded frame to whichever VS-facing pipe is current, tolerating (log +
    /// continue, per <see cref="ReceivePumpAsync"/>'s remarks) a write failure against a pipe a
    /// concurrent <see cref="CreateFreshVsFacingPipe"/> call just abandoned and completed.
    /// </summary>
    private async Task ForwardToCurrentVsWriterAsync(byte[] rawFrame, CancellationToken ct)
    {
        try
        {
            await WriteFrameAsync(GetCurrentToVsWriter(), rawFrame, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LspInterceptingPipe [Receive]: forwarding a frame to the current VS-facing pipe failed " +
                "(tolerated -- likely raced a CreateFreshVsFacingPipe swap; the next frame goes to " +
                "whatever pipe is current then).");
        }
    }

    /// <summary>
    /// Session-scoped pump for the VS → server direction: reads one VS-facing session's
    /// <c>_fromVsPipe.Reader</c> and forwards to the real, persistent <see cref="_serverPipe"/>
    /// stdin. Unlike <see cref="ReceivePumpAsync"/>, ending this pump (for any reason, including an
    /// unhandled exception) is <b>not</b> fatal to anything beyond this one session — a fresh one
    /// replaces it on the next <see cref="CreateFreshVsFacingPipe"/> call.
    /// </summary>
    private async Task SendPumpAsync(PipeReader source, int sessionId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadNextFrameAsync(source, ct).ConfigureAwait(false);
                if (frame is null)
                    break;

                var body = frame.Body;
                if (body is null)
                {
                    // Malformed JSON — forward raw bytes verbatim so the connection stays alive.
                    await WriteFrameGuardedAsync(_serverPipe.Output, frame.RawBytes, lockDestination: true, ct)
                        .ConfigureAwait(false);
                    continue;
                }

                var message = new LspMessage(LspMessageDirection.Send, body, DateTimeOffset.Now);
                var result  = await RunInterceptorsAsync(message, _sendInterceptors, ct).ConfigureAwait(false);

                if (result == LspInterceptorResult.PassThrough)
                    await WriteFrameGuardedAsync(_serverPipe.Output, frame.RawBytes, lockDestination: true, ct)
                        .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown, or superseded by a fresh session */ }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Expected shutdown race (issue #165): Dispose() cancels the pumps and disposes
            // _injectLock without first awaiting an in-flight WriteFrameGuardedAsync call, so
            // this pump can still be inside _injectLock.WaitAsync when the semaphore gets
            // disposed out from under it. Benign — this pump loop was already exiting from the
            // same Dispose() call, and the server reports a graceful exit immediately after.
            // Logged at Debug rather than Error so shutdown doesn't produce misleading noise.
            _logger.LogDebug(
                "LspInterceptingPipe [Send] pump (session #{SessionId}) observed a disposed semaphore " +
                "during shutdown (benign).", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LspInterceptingPipe [Send] pump (session #{SessionId}) faulted.", sessionId);
        }
    }

    // ── LSP frame reader ────────────────────────────────────────────────────

    private sealed class LspFrame
    {
        public LspFrame(JObject? body, byte[] rawBytes) { Body = body; RawBytes = rawBytes; }
        public JObject? Body    { get; }
        public byte[]   RawBytes { get; }
    }

    /// <summary>
    /// Reads one LSP frame from <paramref name="reader"/>.
    /// Returns <c>null</c> when the pipe is completed (remote side closed).
    /// Returns an <see cref="LspFrame"/> with a <c>null</c> <see cref="LspFrame.Body"/> when
    /// JSON parsing fails; raw bytes are still present so the caller can forward verbatim.
    /// </summary>
    private static async Task<LspFrame?> ReadNextFrameAsync(PipeReader reader, CancellationToken ct)
    {
        // Phase 1 – read until we see \r\n\r\n and can extract Content-Length.
        // We use AdvanceTo(consumed, examined) correctly: we only mark bytes as consumed
        // once we know exactly which bytes belong to the header vs. the body.
        int contentLength;
        int headerLength; // total byte length of "Content-Length: N\r\n\r\n"

        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (result.IsCompleted && buffer.IsEmpty)
                return null;

            if (TryParseHeader(buffer, out contentLength, out headerLength))
            {
                // Mark exactly the header bytes as consumed; leave body bytes in the pipe.
                reader.AdvanceTo(buffer.GetPosition(headerLength));
                break;
            }

            // Haven't seen the full header yet – tell the pipe we've examined everything
            // but consumed nothing so it can give us more data next time.
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
                return null; // pipe ended mid-header
        }

        // Phase 2 – read exactly contentLength body bytes.
        var bodyBytes = await ReadExactAsync(reader, contentLength, ct).ConfigureAwait(false);
        if (bodyBytes is null)
            return null;

        // Re-build raw frame for verbatim forwarding.
        var headerText = $"Content-Length: {contentLength}\r\n\r\n";
        var headerEnc  = Utf8NoBom.GetBytes(headerText);
        var rawBytes   = new byte[headerEnc.Length + bodyBytes.Length];
        Array.Copy(headerEnc, 0, rawBytes, 0, headerEnc.Length);
        Array.Copy(bodyBytes, 0, rawBytes, headerEnc.Length, bodyBytes.Length);

        JObject? body;
        try
        {
            body = JObject.Parse(Utf8NoBom.GetString(bodyBytes));
        }
        catch (Exception)
        {
            body = null; // malformed JSON — caller forwards raw bytes without intercepting
        }

        return new LspFrame(body, rawBytes);
    }

    /// <summary>Re-encodes a (possibly mutated) parsed body back into a raw LSP frame.</summary>
    private static byte[] EncodeFrame(JObject body)
    {
        // Deliberately the parameterless overload: JToken.ToString(Formatting) resolves to a
        // MissingMethodException in the VS host process — some Newtonsoft.Json assembly loaded
        // there doesn't carry that overload. The parameterless one is used successfully
        // elsewhere in this codebase (e.g. GoToHooksService). Formatting (indented vs. compact)
        // doesn't affect wire correctness, only payload size.
        var bodyBytes   = Utf8NoBom.GetBytes(body.ToString());
        var headerText  = $"Content-Length: {bodyBytes.Length}\r\n\r\n";
        var headerBytes = Utf8NoBom.GetBytes(headerText);

        var rawBytes = new byte[headerBytes.Length + bodyBytes.Length];
        Array.Copy(headerBytes, 0, rawBytes, 0, headerBytes.Length);
        Array.Copy(bodyBytes, 0, rawBytes, headerBytes.Length, bodyBytes.Length);
        return rawBytes;
    }

    /// <summary>
    /// Tries to find the LSP header block (terminated by <c>\r\n\r\n</c>) in
    /// <paramref name="buffer"/> and extract the <c>Content-Length</c> value.
    /// </summary>
    private static bool TryParseHeader(ReadOnlySequence<byte> buffer, out int contentLength, out int headerLength)
    {
        contentLength = 0;
        headerLength  = 0;

        // Flatten to a single array only if the buffer is multi-segment (rare for small headers).
        var bytes = buffer.IsSingleSegment
            ? buffer.First.Span.ToArray()
            : buffer.ToArray();

        for (int i = 0; i <= bytes.Length - 4; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' &&
                bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                var headerText = Utf8NoBom.GetString(bytes, 0, i);
                foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        var valueStr = line.Substring("Content-Length:".Length).Trim();
                        if (int.TryParse(valueStr, out contentLength))
                        {
                            headerLength = i + 4; // header bytes + \r\n\r\n
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes from <paramref name="reader"/>.</summary>
    private static async Task<byte[]?> ReadExactAsync(PipeReader reader, int count, CancellationToken ct)
    {
        var accumulator = new List<byte>(count);

        while (accumulator.Count < count)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (result.IsCompleted && buffer.IsEmpty)
                return null;

            int needed = count - accumulator.Count;
            var slice  = buffer.Length >= needed ? buffer.Slice(0, needed) : buffer;

            foreach (var seg in slice)
            {
                accumulator.AddRange(seg.ToArray());
            }

            reader.AdvanceTo(slice.End);
        }

        return accumulator.ToArray();
    }

    // ── Frame writer ────────────────────────────────────────────────────────

    private static async Task WriteFrameAsync(PipeWriter writer, byte[] rawFrame, CancellationToken ct)
    {
        var memory = writer.GetMemory(rawFrame.Length);
        rawFrame.CopyTo(memory);
        writer.Advance(rawFrame.Length);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forwards a frame to <paramref name="writer"/>, taking <see cref="_injectLock"/> first when
    /// <paramref name="lockDestination"/> is set. The send pump's destination
    /// (<c>_serverPipe.Output</c>) is also written to directly by
    /// <see cref="SendNotificationToServerAsync"/>/<see cref="SendRequestToServerAsync"/> from
    /// other threads; without this, the pump's own passthrough write here could interleave with
    /// an injected write on the same unsynchronised <see cref="PipeWriter"/>, corrupting the
    /// framing.
    /// </summary>
    private async Task WriteFrameGuardedAsync(PipeWriter writer, byte[] rawFrame, bool lockDestination, CancellationToken ct)
    {
        if (!lockDestination)
        {
            await WriteFrameAsync(writer, rawFrame, ct).ConfigureAwait(false);
            return;
        }

        await _injectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(writer, rawFrame, ct).ConfigureAwait(false);
        }
        finally
        {
            _injectLock.Release();
        }
    }

    // ── Interceptor pipeline ────────────────────────────────────────────────

    private async Task<LspInterceptorResult> RunInterceptorsAsync(
        LspMessage                            message,
        IReadOnlyList<ILspMessageInterceptor> interceptors,
        CancellationToken                     ct)
    {
        foreach (var interceptor in interceptors)
        {
            try
            {
                var result = await interceptor.InterceptAsync(message, ct).ConfigureAwait(false);
                if (result == LspInterceptorResult.Consume)
                    return LspInterceptorResult.Consume;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "LspInterceptingPipe: interceptor {InterceptorType} threw.",
                    interceptor.GetType().Name);
            }
        }

        return LspInterceptorResult.PassThrough;
    }

    // ── Notification injection (VS → Server) ───────────────────────────────

    /// <summary>
    /// Encodes a JSON-RPC notification and writes it directly into the server-bound
    /// output stream, bypassing the VS-facing pipe.  Safe to call from any thread;
    /// uses <see cref="_injectLock"/> to serialise against the send pump.
    /// </summary>
    /// <param name="method">LSP method name, e.g. <c>reqnroll/projectLoaded</c>.</param>
    /// <param name="paramsJson">
    /// Already-serialized JSON string for the <c>params</c> field, or <c>null</c>/empty
    /// to omit the field.
    /// </param>
    public async Task SendNotificationToServerAsync(
        string method,
        string? paramsJson,
        CancellationToken cancellationToken)
    {
        if (_disposed) return;

        // Build the JSON-RPC notification frame.
        var body = string.IsNullOrEmpty(paramsJson)
            ? $"{{\"jsonrpc\":\"2.0\",\"method\":{JsonEscape(method)}}}"
            : $"{{\"jsonrpc\":\"2.0\",\"method\":{JsonEscape(method)},\"params\":{paramsJson}}}";

        var bodyBytes  = Utf8NoBom.GetBytes(body);
        var headerText = $"Content-Length: {bodyBytes.Length}\r\n\r\n";
        var headerBytes = Utf8NoBom.GetBytes(headerText);

        await _injectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var memory = _serverPipe.Output.GetMemory(headerBytes.Length + bodyBytes.Length);
            headerBytes.CopyTo(memory);
            bodyBytes.CopyTo(memory.Slice(headerBytes.Length));
            _serverPipe.Output.Advance(headerBytes.Length + bodyBytes.Length);
            await _serverPipe.Output.FlushAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "LspInterceptingPipe: injected notification {Method} ({ByteCount} bytes)", method, bodyBytes.Length);
        }
        finally
        {
            _injectLock.Release();
        }

        // Notify interceptors about the injected notification so it appears in the inspector log.
        // This runs outside the inject lock to avoid holding it during potentially-slow I/O.
        JObject? bodyObj = null;
        try { bodyObj = JObject.Parse(body); } catch { /* malformed — skip */ }
        if (bodyObj is not null)
        {
            var synthetic = new LspMessage(LspMessageDirection.Send, bodyObj, DateTimeOffset.Now);
            await RunInterceptorsAsync(synthetic, _sendInterceptors, cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Request injection and response correlation (VS → Server → back) ──────

    /// <summary>
    /// Injects a JSON-RPC request into the server-bound stream and awaits the server's response.
    /// The response is <b>consumed</b> by the receive pump and never forwarded to VS.
    /// </summary>
    /// <param name="method">LSP method name, e.g. <c>textDocument/references</c>.</param>
    /// <param name="paramsJson">Already-serialized JSON for <c>params</c>, or <c>null</c> to omit it.</param>
    /// <returns>
    /// The <c>result</c> field of the server's response as a <see cref="JToken"/> (may be a
    /// <see cref="JArray"/>, <see cref="JObject"/>, or primitive), or <c>null</c> if the server
    /// returned a JSON-RPC error, the result was JSON null, or the operation was cancelled.
    /// </returns>
    public async Task<JToken?> SendRequestToServerAsync(
        string method,
        string? paramsJson,
        CancellationToken cancellationToken)
    {
        if (_disposed) return null;

        var id  = RequestIdPrefix + Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        // Register cancellation before sending to avoid the race where the token is already
        // cancelled at the point we would have registered.
        var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            var body = string.IsNullOrEmpty(paramsJson)
                ? $"{{\"jsonrpc\":\"2.0\",\"id\":{JsonEscape(id)},\"method\":{JsonEscape(method)}}}"
                : $"{{\"jsonrpc\":\"2.0\",\"id\":{JsonEscape(id)},\"method\":{JsonEscape(method)},\"params\":{paramsJson}}}";

            var bodyBytes   = Utf8NoBom.GetBytes(body);
            var headerText  = $"Content-Length: {bodyBytes.Length}\r\n\r\n";
            var headerBytes = Utf8NoBom.GetBytes(headerText);

            await _injectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var memory = _serverPipe.Output.GetMemory(headerBytes.Length + bodyBytes.Length);
                headerBytes.CopyTo(memory);
                bodyBytes.CopyTo(memory.Slice(headerBytes.Length));
                _serverPipe.Output.Advance(headerBytes.Length + bodyBytes.Length);
                await _serverPipe.Output.FlushAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "LspInterceptingPipe: injected request {Method} id={Id} ({ByteCount} bytes)", method, id, bodyBytes.Length);
            }
            finally
            {
                _injectLock.Release();
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "LspInterceptingPipe: request {Method} id={Id} cancelled", method, id);
            return null;
        }
        finally
        {
            reg.Dispose();
            _pendingRequests.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Checks whether <paramref name="body"/> is a JSON-RPC response to one of our injected
    /// requests.  If so, completes the awaiting <see cref="TaskCompletionSource{T}"/> and
    /// returns <c>true</c> so the pump skips forwarding the frame to VS.
    /// </summary>
    private bool TryCompleteCorrelatedResponse(JObject body)
    {
        // A JSON-RPC response has an "id" and either "result" or "error", but no "method".
        if (body.ContainsKey("method")) return false;

        var idToken = body["id"];
        if (idToken is null) return false;

        var id = idToken.Value<string>();
        if (id is null || !id.StartsWith(RequestIdPrefix, StringComparison.Ordinal)) return false;

        if (!_pendingRequests.TryRemove(id, out var tcs)) return false;

        if (body.ContainsKey("error"))
            tcs.TrySetResult(null);
        else
            tcs.TrySetResult(body["result"]);

        _logger.LogInformation(
            "LspInterceptingPipe: consumed correlated response id={Id}", id);
        return true;
    }

    private static string JsonEscape(string value)
        => Newtonsoft.Json.JsonConvert.ToString(value); // produces "\"value\""

    // ── IDisposable ─────────────────────────────────────────────────────────

    /// <summary>Cancels the pump tasks, faults any in-flight injected requests, and completes the current VS-facing pipe.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _linkedCts?.Cancel();
        _linkedCts?.Dispose();
        _cts.Dispose();
        _injectLock.Dispose();

        lock (_vsPipeSwapLock)
        {
            _currentSendPumpCts?.Cancel();
            _currentSendPumpCts?.Dispose();
        }

        // Fault any in-flight injected requests so callers don't hang.
        foreach (var kv in _pendingRequests)
            kv.Value.TrySetCanceled();
        _pendingRequests.Clear();

        try { GetCurrentToVsWriter().Complete(); } catch { /* best-effort */ }
    }

    // ── Inner helper ────────────────────────────────────────────────────────

    /// <summary>Adapts a <see cref="PipeReader"/> / <see cref="PipeWriter"/> pair into an <see cref="IDuplexPipe"/>.</summary>
    private sealed class DuplexPipeAdapter : IDuplexPipe
    {
        public DuplexPipeAdapter(PipeReader input, PipeWriter output)
        {
            Input  = input;
            Output = output;
        }

        public PipeReader Input  { get; }
        public PipeWriter Output { get; }
    }
}
