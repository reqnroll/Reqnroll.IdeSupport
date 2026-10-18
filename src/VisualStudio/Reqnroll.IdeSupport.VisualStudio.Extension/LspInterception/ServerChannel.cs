using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// The write side of the connection to the real server process: sole owner of the server's stdin
/// <see cref="PipeWriter"/>, the lock that serialises writes to it, and the "this server is gone"
/// flag (issue #587, step 2).
/// </summary>
/// <remarks>
/// <para>
/// Those three belong together. The send pump's passthrough writes and the injected writes from
/// <see cref="LspInterceptingPipe.SendNotificationToServerAsync"/> /
/// <see cref="LspInterceptingPipe.SendRequestToServerAsync"/> target the same unsynchronised
/// <see cref="PipeWriter"/> from different threads; without a single owner holding one lock, two
/// writers can interleave mid-frame and corrupt the framing. And whether writing is worth doing at
/// all depends on the termination flag, which is why issue #555's lifetime rule lives here rather
/// than beside the routing rules it was tangled with.
/// </para>
/// <para>
/// The two write methods differ only in whether termination gates them, and that difference is
/// deliberate — see each one.
/// </para>
/// </remarks>
internal sealed class ServerChannel
{
    private readonly PipeWriter    _output;
    private readonly ILogger       _logger;

    /// <summary>
    /// Serialises every write to <see cref="_output"/>.
    /// </summary>
    /// <remarks>
    /// Never disposed, on purpose. <see cref="SemaphoreSlim.Dispose"/> only has to be called when
    /// <see cref="SemaphoreSlim.AvailableWaitHandle"/> has been used, which nothing in this codebase
    /// does. Disposing it at shutdown without first awaiting in-flight writers is what produced the
    /// <see cref="ObjectDisposedException"/> the send pump used to have to swallow (issue #165):
    /// the pump could still be inside <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> when
    /// the semaphore went away underneath it.
    /// </remarks>
    private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

    private volatile bool _terminated;

    /// <summary>1 once a failed write has been logged at Warning (issue #1023).</summary>
    private int _writeFailureReported;

    /// <summary>Creates the channel over the server process's stdin writer.</summary>
    public ServerChannel(PipeWriter output, ILogger logger)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// <see langword="true"/> once the server behind this channel has been asked to <c>exit</c> (or
    /// is otherwise known to be gone), meaning it can never serve another request.
    /// </summary>
    public bool IsTerminated => _terminated;

    /// <summary>Records that the server is terminating. Idempotent; safe from any thread.</summary>
    /// <param name="reason">Why the server is considered gone; logged.</param>
    public void MarkTerminated(string reason)
    {
        if (_terminated) return;
        _terminated = true;

        _logger.LogDebug(
            "ServerChannel: server considered terminated — {Reason}. This connection is spent; " +
            "further injected traffic is refused and a new server must be launched for the next " +
            "session (issue #555).", reason);
    }

    /// <summary>
    /// Forwards a frame that came from VS. Never gated on termination.
    /// </summary>
    /// <remarks>
    /// VS's own <c>exit</c> travels this path, and the server is only considered terminated
    /// <em>after</em> that frame has actually gone out — so gating here would either refuse the very
    /// message that ends the session, or silently swallow VS traffic that the previous
    /// implementation forwarded. Refusal applies to traffic <em>we</em> originate; see
    /// <see cref="InjectAsync"/>.
    /// </remarks>
    public async Task ForwardAsync(byte[] rawFrame, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LspFrameCodec.WriteFrameAsync(_output, rawFrame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes a frame this extension originated, unless the server has terminated.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if the write was refused because the server is gone, or failed because
    /// its stdin is no longer writable.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Issue #555: after <c>exit</c> the server is on its way out, so this would write into a stream
    /// nothing is reading. Observed in the wild — StepCodeLens and navigation-bar traffic kept being
    /// injected for ~200ms after VS's <c>exit</c> and on through the following session, each request
    /// awaiting a response that could never arrive.
    /// </para>
    /// <para>
    /// Issue #1023: the flag is checked again once the lock is held, because a caller that passed the
    /// first check can wait behind another write while the server is marked terminated. A write that
    /// fails because the server's stdin is gone is reported the same way, as <see langword="false"/>,
    /// rather than thrown: an <see cref="IOException"/> is a crashed server's closed pipe, an
    /// <see cref="ObjectDisposedException"/> a write racing the disposal of a discarded generation's
    /// process. Neither marks the channel terminated: that flag means the server was <em>asked</em> to
    /// go, and setting it for a crash would suppress the unexpected-exit report (issue #845).
    /// </para>
    /// </remarks>
    public async Task<bool> InjectAsync(byte[] rawFrame, string method, CancellationToken cancellationToken)
    {
        if (RefuseIfTerminated(method)) return false;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (RefuseIfTerminated(method)) return false;

            await LspFrameCodec.WriteFrameAsync(_output, rawFrame, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "ServerChannel: injected {Method} ({ByteCount} bytes)", method, rawFrame.Length);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            LogWriteFailure(method, ex);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }

        return true;
    }

    private bool RefuseIfTerminated(string method)
    {
        if (!_terminated) return false;

        _logger.LogDebug(
            "ServerChannel: refusing to inject {Method} — the server on this connection has terminated.",
            method);
        return true;
    }

    /// <summary>
    /// Warns about the first write that fails while the server is still believed alive; every later
    /// one, and any during an expected teardown, is Debug, since a dead stdin fails every write.
    /// </summary>
    private void LogWriteFailure(string method, Exception ex)
    {
        var level = !_terminated && Interlocked.Exchange(ref _writeFailureReported, 1) == 0
            ? LogLevel.Warning
            : LogLevel.Debug;

        _logger.Log(
            level,
            "ServerChannel: could not inject {Method} — the server's stdin is no longer writable " +
            "({ExceptionType}: {Message}); the server process has most likely exited.",
            method, ex.GetType().Name, ex.Message);
    }
}
