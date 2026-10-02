using System.Diagnostics;
using System.Runtime.InteropServices;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// Emits the IDE-agnostic server lifecycle events (issue #845): <c>ServerSessionStarted</c> once the
/// LSP handshake has completed, and a best-effort <c>ServerSessionEnded</c> at graceful shutdown.
/// Because the server sends them, VS, VS Code and Rider report identical events with no per-IDE port.
/// </summary>
/// <remarks>
/// <para>
/// Identity (<c>IdeClient</c>, <c>ServerVersion</c>, <c>SessionId</c>) is stamped by the
/// <see cref="ILspTelemetryService"/> this service is given (the outermost identity decorator, #844).
/// </para>
/// <para>
/// Only closed vocabularies, numbers and version strings are sent: OS family, CPU architecture,
/// the .NET runtime description, a client version and durations. No paths, user names or machine names.
/// </para>
/// <para>
/// Loss profile: like <c>FeatureUsageSummary</c>'s final flush, the ended event rides the LSP
/// <c>shutdown</c> request and is lost when the process dies abruptly. A started event without an
/// ended event for the same <c>SessionId</c> is therefore itself the signal of an abnormal end.
/// </para>
/// </remarks>
public sealed class ServerSessionTelemetry
{
    private readonly ILspTelemetryService? _telemetry;
    private readonly ClientIdeContext _ide;
    private readonly IIdeSupportLogger _logger;
    private readonly Func<TimeSpan> _processUptime;
    private readonly long _sessionStartTimestamp = Stopwatch.GetTimestamp();
    private int _started;
    private int _ended;

    /// <summary>Initializes a new instance of the <see cref="ServerSessionTelemetry"/> class.</summary>
    public ServerSessionTelemetry(ILspTelemetryService? telemetry, ClientIdeContext ide, IIdeSupportLogger logger)
        : this(telemetry, ide, logger, GetProcessUptime)
    {
    }

    /// <summary>Test seam: supplies the process-uptime clock.</summary>
    internal ServerSessionTelemetry(
        ILspTelemetryService? telemetry, ClientIdeContext ide, IIdeSupportLogger logger, Func<TimeSpan> processUptime)
    {
        _telemetry = telemetry;
        _ide = ide;
        _logger = logger;
        _processUptime = processUptime;
    }

    /// <summary>Sends <c>ServerSessionStarted</c>, at most once per server process.</summary>
    public void ReportStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        var properties = new Dictionary<string, object?>
        {
            [TelemetryProperties.OperatingSystem] = GetOperatingSystem(),
            [TelemetryProperties.Architecture] = RuntimeInformation.ProcessArchitecture.ToString(),
            [TelemetryProperties.Runtime] = RuntimeInformation.FrameworkDescription,
            [TelemetryProperties.StartupMs] = (long)_processUptime().TotalMilliseconds,
        };
        if (IsSafeVersion(_ide.ClientVersion))
            properties[TelemetryProperties.ClientVersion] = _ide.ClientVersion;

        Send(TelemetryEvents.ServerSessionStarted, properties);
    }

    /// <summary>
    /// Sends <c>ServerSessionEnded</c>, at most once per server process (so the shutdown request and
    /// the post-exit fallback cannot double-send). Never throws: best-effort telemetry must not fail shutdown.
    /// </summary>
    public void ReportEnded()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;

        Send(TelemetryEvents.ServerSessionEnded, new Dictionary<string, object?>
        {
            [TelemetryProperties.SessionSeconds] = Math.Round(Stopwatch.GetElapsedTime(_sessionStartTimestamp).TotalSeconds),
        });
    }

    /// <summary>
    /// Calls <see cref="ReportEnded"/> when the LSP <c>shutdown</c> request arrives on <paramref name="shutdown"/>.
    /// Subscribe it <i>after</i> <see cref="Performance.FeatureUsageFlushService.FlushOnShutdown"/> so the
    /// final <c>FeatureUsageSummary</c> precedes <c>ServerSessionEnded</c> in the stream.
    /// </summary>
    public IDisposable EndOnShutdown(IObservable<bool> shutdown) => shutdown.Subscribe(new ShutdownObserver(this));

    private static readonly System.Text.RegularExpressions.Regex SafeVersion =
        new(@"^[\w.\-+]{1,32}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The client version is client-controlled text, so it is only sent when it looks like a version
    /// (word characters, dots, hyphens, plus; at most 32 characters). Anything else is omitted.
    /// </summary>
    internal static bool IsSafeVersion(string? version) => version is not null && SafeVersion.IsMatch(version);

    private void Send(string eventName, Dictionary<string, object?> properties)
    {
        try
        {
            _telemetry?.SendEvent(eventName, properties);
        }
        catch (Exception ex)
        {
            _logger.LogVerbose($"ServerSessionTelemetry: failed to send {eventName}: {ex.Message}");
        }
    }

    private static string GetOperatingSystem() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux"
        : "Other";

    private static TimeSpan GetProcessUptime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var uptime = DateTime.Now - process.StartTime;
            return uptime < TimeSpan.Zero ? TimeSpan.Zero : uptime;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private sealed class ShutdownObserver : IObserver<bool>
    {
        private readonly ServerSessionTelemetry _owner;

        public ShutdownObserver(ServerSessionTelemetry owner) => _owner = owner;

        public void OnNext(bool value) => _owner.ReportEnded();

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }
}
