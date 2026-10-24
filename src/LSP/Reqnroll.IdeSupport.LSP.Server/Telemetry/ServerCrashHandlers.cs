using System.Diagnostics;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// Runs from <see cref="AppDomain.UnhandledException"/>: writes the local crash log and a
/// <see cref="ServerCrashRecord"/> for the next server start to report. Everything it does is synchronous and
/// best-effort, because the process ends as soon as the handler returns.
/// </summary>
public sealed class FatalExceptionRecorder
{
    private readonly ServerCrashFiles _crashFiles;
    private readonly string? _sessionId;
    private readonly string? _clientSessionId;
    private readonly Func<string?> _ideClient;
    private readonly string? _serverVersion;
    private readonly Action<Exception>? _writeCrashLog;
    private readonly Func<DateTime> _utcNow;
    private Exception? _alreadyLogged;
    private int _recorded;

    /// <summary>Initializes a new instance of the <see cref="FatalExceptionRecorder"/> class.</summary>
    /// <param name="crashFiles">Where the record goes.</param>
    /// <param name="sessionId">This process's <c>SessionId</c>.</param>
    /// <param name="clientSessionId">The client's <c>ClientSessionId</c>, if it passed one.</param>
    /// <param name="ideClient">Reads the IDE identifier at crash time (the client may only identify itself after start-up).</param>
    /// <param name="serverVersion">This server's version.</param>
    /// <param name="writeCrashLog">Writes the full exception to the local crash log, or <see langword="null"/> for none.</param>
    /// <param name="utcNow">Clock; <see langword="null"/> for the system clock.</param>
    public FatalExceptionRecorder(
        ServerCrashFiles crashFiles, string? sessionId, string? clientSessionId, Func<string?> ideClient,
        string? serverVersion, Action<Exception>? writeCrashLog, Func<DateTime>? utcNow = null)
    {
        _crashFiles = crashFiles;
        _sessionId = sessionId;
        _clientSessionId = clientSessionId;
        _ideClient = ideClient;
        _serverVersion = serverVersion;
        _writeCrashLog = writeCrashLog;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Records that <paramref name="exception"/> was already written to the crash log (by <c>Main</c>'s own catch,
    /// before it rethrows into this handler), so it is not logged twice.
    /// </summary>
    public void MarkLogged(Exception exception) => _alreadyLogged = exception;

    /// <summary>Writes the crash log (unless already written) and the crash record, at most once. Never throws.</summary>
    public void Record(Exception exception)
    {
        if (Interlocked.Exchange(ref _recorded, 1) != 0)
            return;

        if (!ReferenceEquals(exception, _alreadyLogged))
        {
            try
            {
                _writeCrashLog?.Invoke(exception);
            }
            catch (Exception)
            {
                // Best-effort; the record below is still worth writing.
            }
        }

        try
        {
            string? ideClient = null;
            try
            {
                ideClient = _ideClient();
            }
            catch (Exception)
            {
                // The container may be half torn down; the record is still useful without it.
            }

            _crashFiles.TryWrite(ServerCrashRecord.From(
                exception, _utcNow(), ProcessUptimeMs(), _sessionId, _clientSessionId, ideClient, _serverVersion));
        }
        catch (Exception)
        {
            // Never throw from the unhandled-exception handler.
        }
    }

    private static long? ProcessUptimeMs()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var uptime = DateTime.Now - process.StartTime;
            return uptime < TimeSpan.Zero ? null : (long)uptime.TotalMilliseconds;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Reports exceptions from faulted tasks nobody awaited (<see cref="TaskScheduler.UnobservedTaskException"/>) as
/// non-fatal <c>Error.UnhandledException</c> events. They do not end the process, but they are bugs that were
/// otherwise invisible, and they often come before a crash. Capped per process.
/// </summary>
public sealed class UnobservedTaskExceptionReporter
{
    /// <summary>The most unobserved task exceptions reported per server process.</summary>
    public const int MaxReports = 10;

    private readonly IErrorTelemetryService _errorTelemetry;
    private int _reported;

    /// <summary>Initializes a new instance of the <see cref="UnobservedTaskExceptionReporter"/> class.</summary>
    public UnobservedTaskExceptionReporter(IErrorTelemetryService errorTelemetry)
    {
        _errorTelemetry = errorTelemetry;
    }

    /// <summary>Reports one unobserved exception, unless the cap is reached. Never throws.</summary>
    public void Report(AggregateException exception)
    {
        if (Interlocked.Increment(ref _reported) > MaxReports)
            return;

        try
        {
            // A faulted task wraps its exception; report the real one when there is exactly one.
            var reported = exception.InnerExceptions.Count == 1 ? exception.InnerExceptions[0] : exception;
            _errorTelemetry.MonitorError(reported, isFatal: false);
        }
        catch (Exception)
        {
            // Runs on the finalizer thread: telemetry must never throw here.
        }
    }
}
