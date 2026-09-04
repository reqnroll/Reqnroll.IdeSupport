using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Owns the local, in-memory pipes that face Visual Studio, and the generation counter that
/// identifies them (issue #587, step 3 — extracted from <see cref="LspInterceptingPipe"/>).
/// </summary>
/// <remarks>
/// <para>
/// Issue #156: VS can call <c>CreateServerConnectionAsync</c> more than once per session — its own
/// LSP client host does this by design, for extension hot-reload. An implementation that cached and
/// returned the same <c>IDuplexPipe</c> every time is fine until VS calls twice, and then corrupts
/// the connection: a second consumer writing to a <see cref="PipeWriter"/> the first consumer's
/// disposal already completed throws <c>InvalidOperationException: Writing is not allowed after
/// writer was completed</c>, exactly what was observed. Every call therefore gets a fresh,
/// never-before-used pipe pair, matching every Microsoft sample.
/// </para>
/// <para>
/// The connection to the real server process is <b>not</b> this type's business and is untouched by
/// a swap — only the local relay pipes change.
/// </para>
/// </remarks>
internal sealed class VsFacingSessionManager : IDisposable
{
    // The two Pipe objects whose Reader/Writer ends form the *current* VS-facing IDuplexPipe.
    // VS reads from _toVsPipe.Reader; VS writes to _fromVsPipe.Writer. Replaced wholesale on every
    // StartNewSession call -- guarded by _swapLock since the persistent receive pump reads the
    // current _toVsPipe reference concurrently with swaps happening on VS's calling thread.
    private Pipe _toVsPipe   = new Pipe();   // server → VS direction
    private Pipe _fromVsPipe = new Pipe();   // VS → server direction
    private readonly object _swapLock = new object();

    // The current session's send pump + its own cancellation, replaced on every StartNewSession
    // call. Guarded by _swapLock alongside the Pipe fields above.
    private CancellationTokenSource? _currentSendPumpCts;
    private Task?                    _currentSendPump;
    private int                      _sessionCounter;

    private readonly ILogger _logger;

    /// <summary>Creates the manager with an initial (session #0) pipe pair, before VS has asked for one.</summary>
    public VsFacingSessionManager(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The writer of whichever server → VS pipe is current.
    /// </summary>
    /// <remarks>
    /// Looked up per frame by the receive pump rather than captured once, since
    /// <see cref="StartNewSession"/> can swap it out from under that long-running pump at any time.
    /// </remarks>
    public PipeWriter CurrentToVsWriter
    {
        get { lock (_swapLock) { return _toVsPipe.Writer; } }
    }

    /// <summary>The session id currently in effect. Sessions are numbered from 1 by <see cref="StartNewSession"/>.</summary>
    public int CurrentSessionId
    {
        get { lock (_swapLock) { return _sessionCounter; } }
    }

    /// <summary>
    /// Swaps in a fresh pipe pair, abandons the previous session, and returns everything the new one
    /// needs. The caller starts the send pump — this type knows nothing about interceptors or frames.
    /// </summary>
    /// <param name="lifetimeToken">The connection's lifetime token; the new session's own token is linked to it.</param>
    /// <remarks>
    /// Abandoning the previous session means cancelling its send pump (VS → server) and completing
    /// its <c>_toVsPipe.Writer</c> (server → VS; ours to complete) so a lingering VS-side reader gets
    /// a clean end-of-stream rather than an error. The abandoned <c>_fromVsPipe.Writer</c>
    /// (VS → server) is left alone: VS owns that end, not us.
    /// </remarks>
    public VsFacingSession StartNewSession(CancellationToken lifetimeToken)
    {
        var newToVsPipe    = new Pipe();
        var newFromVsPipe  = new Pipe();
        var newSendPumpCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);

        Pipe?                    oldToVsPipe;
        CancellationTokenSource? oldSendPumpCts;
        int                      sessionId;

        lock (_swapLock)
        {
            oldToVsPipe    = _toVsPipe;
            oldSendPumpCts = _currentSendPumpCts;

            _toVsPipe           = newToVsPipe;
            _fromVsPipe         = newFromVsPipe;
            _currentSendPumpCts = newSendPumpCts;
            sessionId           = ++_sessionCounter;
        }

        oldSendPumpCts?.Cancel();
        oldSendPumpCts?.Dispose();
        try
        {
            oldToVsPipe?.Writer.Complete();
        }
        catch (Exception ex)
        {
            // Benign: e.g. already completed by a prior call, or a race with an in-flight receive-pump
            // write to the pipe we're abandoning right now (which that pump tolerates by design).
            _logger.LogDebug(ex, "VsFacingSessionManager: completing the abandoned server→VS pipe threw (benign).");
        }

        _logger.LogInformation(
            "VsFacingSessionManager: started session #{SessionId} (issue #156: no longer handing back a " +
            "cached, possibly-dead pipe on repeat CreateServerConnectionAsync calls).", sessionId);

        return new VsFacingSession(
            new DuplexPipeAdapter(newToVsPipe.Reader, newFromVsPipe.Writer),
            newFromVsPipe.Reader,
            sessionId,
            newSendPumpCts.Token);
    }

    /// <summary>Records the send-pump task started for the session just created.</summary>
    /// <remarks>Held so the running task is reachable rather than anonymous; nothing awaits it — a session's send pump ends with its session.</remarks>
    public void AttachSendPump(Task pump)
    {
        lock (_swapLock)
        {
            _currentSendPump = pump;
        }
    }

    /// <summary>Cancels the current session's send pump and completes the current server → VS writer.</summary>
    public void Dispose()
    {
        lock (_swapLock)
        {
            _currentSendPumpCts?.Cancel();
            _currentSendPumpCts?.Dispose();
        }

        try { CurrentToVsWriter.Complete(); } catch { /* best-effort */ }
    }

    /// <summary>One VS-facing session: what VS gets, what its send pump reads, and how it is identified and cancelled.</summary>
    internal sealed class VsFacingSession
    {
        internal VsFacingSession(IDuplexPipe vsFacing, PipeReader fromVsReader, int sessionId, CancellationToken sessionToken)
        {
            VsFacing     = vsFacing;
            FromVsReader = fromVsReader;
            SessionId    = sessionId;
            SessionToken = sessionToken;
        }

        /// <summary>The pipe pair handed back to VS as the <c>CreateServerConnectionAsync</c> result.</summary>
        public IDuplexPipe VsFacing { get; }

        /// <summary>The VS → server side this session's send pump reads.</summary>
        public PipeReader FromVsReader { get; }

        /// <summary>This session's generation number.</summary>
        public int SessionId { get; }

        /// <summary>Cancelled when this session is abandoned or the connection ends.</summary>
        public CancellationToken SessionToken { get; }
    }

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
