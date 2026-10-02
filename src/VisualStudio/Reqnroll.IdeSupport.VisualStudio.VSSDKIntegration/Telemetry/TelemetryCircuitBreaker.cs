using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Telemetry;

/// <summary>
/// Best-effort delivery policy for telemetry (issue #859): after the first definitive failure to
/// reach the analytics endpoint the transmitter stops touching the network for the rest of the
/// session and tells the user exactly once. Same behaviour and wording as the VS Code
/// (<c>telemetryCircuitBreaker.ts</c>) and Rider (<c>TelemetryCircuitBreaker.kt</c>) copies.
/// </summary>
internal sealed class TelemetryCircuitBreaker
{
    /// <summary>The single per-session notation; identical in every IDE.</summary>
    public const string UnavailableNotice =
        "Telemetry endpoint unreachable; telemetry for this session will be dropped";

    private readonly IIdeSupportLogger? _logger;
    private int _open;

    public TelemetryCircuitBreaker(IIdeSupportLogger? logger) => _logger = logger;

    /// <summary>True once a failure has been recorded; callers must then skip all network work.</summary>
    public bool IsOpen => Volatile.Read(ref _open) != 0;

    /// <summary>
    /// Records a definitive failure. Opens the breaker and emits the one-time notice on the first
    /// call; later calls are verbose-only.
    /// </summary>
    public void RecordFailure(Exception? error)
    {
        if (Interlocked.Exchange(ref _open, 1) == 0)
        {
            _logger?.LogInfo(UnavailableNotice);
            if (error != null)
                _logger?.LogVerbose(() => $"Telemetry failure: {error.Message}");
        }
        else if (error != null)
        {
            _logger?.LogVerbose(() => $"Telemetry failure (suppressed): {error.Message}");
        }
    }
}
