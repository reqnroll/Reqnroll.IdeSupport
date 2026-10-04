using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.VisualStudio.Telemetry;

/// <summary>
/// MEF-exported Application Insights transmitter for the Visual Studio host.
/// <para>
/// This is the only component in the .NET solution that depends on
/// <c>Microsoft.ApplicationInsights</c>. The LSP server never transmits telemetry — it
/// emits <c>telemetry/event</c> notifications, which the VS client forwards to this
/// transmitter (see <c>TelemetryEventInterceptor</c>). Because pre-LSP/lifecycle events
/// (Welcome wizard, New Project wizard, install/upgrade) are raised before any server
/// exists, transmission is necessarily host-side, so each IDE owns its own transmitter
/// (VS in .NET here, VSCode in TypeScript, Rider on the JVM). The IDE-neutral contracts
/// (<see cref="ITelemetryTransmitter"/>, <see cref="ITelemetryEvent"/>) stay in
/// Core/Common so the cross-platform server's dependency graph never pulls in AppInsights.
/// </para>
/// <para>
/// Delivery policy (#859): best-effort and silent. Events are handed to an in-memory channel that is
/// never persisted, and a send that cannot reach the endpoint is dropped by the SDK without any
/// signal reaching this class — so VS neither queues for later nor tells the user. Only shutdown is
/// actively bounded (see <see cref="DisposeAsync"/>). The one-line "telemetry is being dropped"
/// notice that the VS Code and Rider transmitters write has no VS counterpart, because this host
/// cannot detect the failure; the asymmetry is deliberate and documented in
/// <c>docs/Telemetry-Events-Inventory.md</c> §1.
/// </para>
/// </summary>
[Export(typeof(ITelemetryTransmitter))]
public class TelemetryTransmitter : ITelemetryTransmitter, IAsyncDisposable
{
    private readonly TelemetryClient _telemetryClient;
    private readonly IEnableTelemetryChecker _enableTelemetryChecker;
    private readonly IIdeSupportLogger? _logger;
    private readonly ITelemetryDebugLog _debugLog;
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>MEF importing constructor; builds a real <see cref="TelemetryClient"/> backed by Application Insights.</summary>
    [ImportingConstructor]
    public TelemetryTransmitter(
        IEnableTelemetryChecker enableTelemetryChecker,
        IUserUniqueIdStore userUniqueIdStore,
        IVersionProvider versionProvider,
        IIdeSupportLogger? logger = null)
        : this(CreateClient(userUniqueIdStore, versionProvider), ApplyDebugBuildGuard(enableTelemetryChecker), logger,
            TelemetryDebugLog.FromEnvironment())
    {
    }

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    /// <summary>
    /// Debug-build guard (#889): without a <c>REQNROLL_DEBUG_TELEMETRY_CONNECTION_STRING</c> override a Debug
    /// build is treated as telemetry-disabled, so a developer's F5 session never reaches the
    /// production resource (events are still mirrored to the debug log, flagged as not transmitted).
    /// </summary>
    internal static IEnableTelemetryChecker ApplyDebugBuildGuard(IEnableTelemetryChecker inner, bool isDebugBuild = IsDebugBuild)
        => TelemetryConnectionOverride.BlocksBuiltIn(isDebugBuild, TelemetryConnectionOverride.FromEnvironment())
            ? new DisabledTelemetryChecker()
            : inner;

    private sealed class DisabledTelemetryChecker : IEnableTelemetryChecker
    {
        public bool IsEnabled() => false;
    }

    /// <summary>
    /// Test seam: inject a <see cref="TelemetryClient"/> backed by an in-memory channel so
    /// transmission can be asserted without contacting Application Insights, and an
    /// <see cref="ITelemetryDebugLog"/> to assert what the host mirrored.
    /// </summary>
    internal TelemetryTransmitter(
        TelemetryClient telemetryClient,
        IEnableTelemetryChecker enableTelemetryChecker,
        IIdeSupportLogger? logger = null,
        ITelemetryDebugLog? debugLog = null)
    {
        _telemetryClient = telemetryClient;
        _enableTelemetryChecker = enableTelemetryChecker;
        _logger = logger;
        _debugLog = debugLog ?? NullTelemetryDebugLog.Instance;
    }

    private static string? ReadBuiltInConnectionString()
    {
        var assembly = typeof(TelemetryTransmitter).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("InstrumentationKey.txt", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        using var reader = new StreamReader(stream!);
        return reader.ReadLine();
    }

    private static TelemetryClient CreateClient(IUserUniqueIdStore userStore, IVersionProvider versionProvider)
    {
        // Best-effort delivery (#859). InMemoryChannel is already what a default
        // TelemetryConfiguration uses (TelemetrySink creates one), so naming it here only makes the
        // delivery policy explicit and lets both knobs be seen:
        // - SendingInterval: how often the buffered items are POSTed (the SDK default, 30 s);
        // - MaxTelemetryBufferCapacity: how many items trigger an immediate send, *not* a hard cap.
        // The hard cap is TelemetryBuffer.BacklogSize (SDK default: 1,000,000 items): past it items
        // are dropped rather than queued, and there is no on-disk buffer, so nothing is replayed
        // later. A failed send is swallowed by the channel's transmitter (CoreEventSource/ETW only)
        // and never reaches this class — see DisposeAsync for what that means for the host.
        var config = new TelemetryConfiguration
        {
            TelemetryChannel = new InMemoryChannel { MaxTelemetryBufferCapacity = 100, SendingInterval = TimeSpan.FromSeconds(30) },
        };
        config.ConnectionString = TelemetryConnectionOverride.FromEnvironment() ?? ReadBuiltInConnectionString();
        var client = new TelemetryClient(config);
        client.Context.User.Id = userStore.GetUserId();
        client.Context.User.AccountId = userStore.GetUserId();
        ApplyClientIdentity(client.Context.GlobalProperties, versionProvider);
        return client;
    }

    /// <summary>
    /// Stamps the host's client identity on every event (issue #844). <c>IdeClient</c> is the
    /// canonical cross-IDE key, also stamped server-side on server-originated events with the same
    /// <c>visualstudio</c>/<c>vscode</c>/<c>rider</c> vocabulary; stamping it here as well covers
    /// host-originated events the server never sees. Per-event properties take precedence over
    /// these global ones in Application Insights, so a server-stamped value is never overridden.
    /// </summary>
    internal static void ApplyClientIdentity(IDictionary<string, string> properties, IVersionProvider versionProvider)
    {
        properties["IdeClient"] = "visualstudio";
        properties["Ide"] = "Microsoft Visual Studio";
        properties["IdeVersion"] = versionProvider.GetVsVersion();
        properties["ExtensionVersion"] = versionProvider.GetExtensionVersion();
    }

    /// <summary>
    /// Transmits <paramref name="telemetryEvent"/> to Application Insights unless telemetry is
    /// disabled; always mirrors the event (sent or not) to the debug log.
    /// </summary>
    public void TransmitEvent(ITelemetryEvent telemetryEvent)
    {
        var enabled = _enableTelemetryChecker.IsEnabled();
        try
        {
            DumpTelemetryEvent(telemetryEvent);
            if (!enabled)
            {
                // Mirror the event even when opted out — debugging needs to see what *would*
                // have been sent — recording that it was gated and not transmitted.
                _debugLog.Record("host", telemetryEvent.EventName, telemetryEvent.Properties,
                    enabled: false, transmitted: false);
                return;
            }

            var eventTelemetry = new EventTelemetry(telemetryEvent.EventName) { Timestamp = DateTime.UtcNow };
            foreach (var property in telemetryEvent.Properties)
            {
                eventTelemetry.Properties.Add(property.Key, property.Value?.ToString() ?? string.Empty);
            }
            _telemetryClient.TrackEvent(eventTelemetry);

            _debugLog.Record("host", telemetryEvent.EventName, telemetryEvent.Properties,
                enabled: true, transmitted: true);
        }
        catch (Exception ex)
        {
            // Never report a failed transmission as a new exception event: it would go to the same
            // unreachable endpoint (#859). This catch is defensive only — the channel hands the
            // event off to a background sender and does not throw for an unreachable endpoint, so
            // in practice a dead endpoint shows up here only as a synchronous channel fault.
            _debugLog.Record("host", telemetryEvent.EventName, telemetryEvent.Properties,
                enabled: enabled, transmitted: false, error: ex.Message);
        }
    }

    /// <summary>
    /// Transmits <paramref name="exception"/> as a normal (non-fatal) exception event, unless it is
    /// not classified as a "normal" error type (see <see cref="IsNormalError"/>), in which case it
    /// is transmitted as a fatal exception event instead.
    /// </summary>
    public void TransmitExceptionEvent(Exception exception, IEnumerable<KeyValuePair<string, object>> additionalProps)
    {
        var isNormalError = IsNormalError(exception);
        if (isNormalError)
            TransmitException(exception, additionalProps);
        else
            TransmitFatalExceptionEvent(exception, true);
    }

    /// <summary>Transmits <paramref name="exception"/> as an exception event, tagging it as fatal when <paramref name="isFatal"/> is <see langword="true"/>.</summary>
    public void TransmitFatalExceptionEvent(Exception exception, bool isFatal)
    {
        var additionalProps = ImmutableDictionary.CreateBuilder<string, object>();
        if (isFatal)
            additionalProps.Add("IsFatal", isFatal.ToString());

        TransmitException(exception, additionalProps.ToImmutable());
    }

    private void TransmitException(Exception exception, IEnumerable<KeyValuePair<string, object>> additionalProps)
    {
        var additionalPropsArray = additionalProps.ToArray();
        var enabled = _enableTelemetryChecker.IsEnabled();
        var transmitted = false;
        string? transmitError = null;

        DumpTelemetryException(exception, additionalPropsArray);

        if (enabled)
        {
            try
            {
                var exceptionTelemetry = new ExceptionTelemetry(exception) { Timestamp = DateTime.UtcNow };
                foreach (var prop in additionalPropsArray)
                {
                    exceptionTelemetry.Properties.Add(prop.Key, prop.Value?.ToString() ?? string.Empty);
                }
                _telemetryClient.TrackException(exceptionTelemetry);
                transmitted = true;
            }
            catch (Exception ex)
            {
                // catch all exceptions since we do not want to break the whole extension simply because data transmission failed
                transmitError = ex.Message;
                Debug.WriteLine(ex, "Error during transmitting analytics event.");
            }
        }

        // Mirror the exception telemetry for debugging, recording whether the opt-out gated it,
        // consistent with TransmitEvent. `error` is a *transmission* failure, distinct from
        // the reported exception's own message, which is carried in props.
        _debugLog.Record("host", $"(exception) {exception.GetType().Name}",
            BuildExceptionProps(exception, additionalPropsArray),
            enabled: enabled, transmitted: transmitted, error: transmitError);
    }

    private static Dictionary<string, object?> BuildExceptionProps(
        Exception exception, KeyValuePair<string, object>[] additionalProps)
    {
        var props = new Dictionary<string, object?>
        {
            ["ExceptionType"] = exception.GetType().FullName,
            ["Message"] = exception.Message,
        };
        foreach (var p in additionalProps)
            props[p.Key] = p.Value;
        return props;
    }

    [Conditional("ANALYTICS_DEBUG")]
    private void DumpTelemetryEvent(ITelemetryEvent telemetryEvent)
    {
        _logger?.LogVerbose(() => $"{telemetryEvent.EventName}: {string.Join(Environment.NewLine + "  ", telemetryEvent.Properties.Select(p => $"{p.Key}={p.Value}"))}");
    }

    [Conditional("ANALYTICS_DEBUG")]
    private void DumpTelemetryException(Exception exception, IEnumerable<KeyValuePair<string, object>> additionalProps)
    {
        _logger?.LogVerbose(() => $"{exception.Message}: {string.Join(Environment.NewLine + "  ", additionalProps.Select(p => $"{p.Key}={p.Value}"))}");
    }

    private static bool IsNormalError(Exception exception)
    {
        if (exception is AggregateException aggregateException)
            return aggregateException.InnerExceptions.All(IsNormalError);
        return
            //exception is IdeSupportConfigurationException ||
            exception is TimeoutException ||
            exception is TaskCanceledException ||
            exception is OperationCanceledException ||
            exception is HttpRequestException;
    }

    /// <summary>
    /// Flushes any queued telemetry to Application Insights before this transmitter is disposed,
    /// bounded so an unreachable endpoint cannot delay host shutdown (#859).
    /// </summary>
    /// <remarks>
    /// The flush is the only place where this class can observe an unreachable endpoint: the channel
    /// delivers asynchronously and swallows background send failures (they reach ETW/self-diagnostics
    /// only), so nothing can be detected earlier in the session — hence no breaker and no session
    /// notice here. The wait is bounded by <see cref="FlushTimeout"/>; an abandoned flush keeps
    /// running on the thread pool until the SDK's own 100 s HTTP timeout elapses.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            var flush = Task.Run(() => _telemetryClient.Flush());
            if (await Task.WhenAny(flush, Task.Delay(FlushTimeout)).ConfigureAwait(false) != flush)
            {
                // Abandon the still-running flush; observe its eventual fault so it is never unobserved.
                _ = flush.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                _logger?.LogVerbose(() => $"Telemetry flush did not finish within {FlushTimeout.TotalMilliseconds} ms; abandoning it.");
            }
            else if (flush.IsFaulted)
            {
                // Reading Exception observes it, so a failed flush can never surface as an unobserved
                // task exception.
                _logger?.LogVerbose(() => $"Telemetry flush failed: {flush.Exception?.GetBaseException().Message}");
            }
        }
        catch (Exception ex)
        {
            // The endpoint is unreachable or the channel is already gone: telemetry is lost either
            // way, and shutdown must not fail because of it.
            _logger?.LogVerbose(() => $"Telemetry flush failed: {ex.Message}");
        }
    }
}
