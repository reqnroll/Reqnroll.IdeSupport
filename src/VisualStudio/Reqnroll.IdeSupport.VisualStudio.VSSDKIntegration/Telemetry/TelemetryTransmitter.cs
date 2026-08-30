using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
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
/// </summary>
[Export(typeof(ITelemetryTransmitter))]
public class TelemetryTransmitter : ITelemetryTransmitter, IAsyncDisposable
{
    private readonly TelemetryClient _telemetryClient;
    private readonly IEnableTelemetryChecker _enableTelemetryChecker;
    private readonly IIdeSupportLogger? _logger;
    private readonly ITelemetryDebugLog _debugLog;

    /// <summary>MEF importing constructor; builds a real <see cref="TelemetryClient"/> backed by Application Insights.</summary>
    [ImportingConstructor]
    public TelemetryTransmitter(
        IEnableTelemetryChecker enableTelemetryChecker,
        IUserUniqueIdStore userUniqueIdStore,
        IVersionProvider versionProvider,
        Reqnroll.IdeSupport.VisualStudio.Logging.IdeSupportCompositeLogger? logger = null)
        : this(CreateClient(userUniqueIdStore, versionProvider), enableTelemetryChecker, logger,
            TelemetryDebugLog.FromEnvironment())
    {
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

    private static TelemetryClient CreateClient(IUserUniqueIdStore userStore, IVersionProvider versionProvider)
    {
        var config = new TelemetryConfiguration();
        var assembly = typeof(TelemetryTransmitter).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("InstrumentationKey.txt", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        using var reader = new StreamReader(stream!);
        config.ConnectionString = reader.ReadLine();
        var client = new TelemetryClient(config);
        client.Context.User.Id = userStore.GetUserId();
        client.Context.User.AccountId = userStore.GetUserId();
        client.Context.GlobalProperties["Ide"] = "Microsoft Visual Studio";
        client.Context.GlobalProperties["IdeVersion"] = versionProvider.GetVsVersion();
        client.Context.GlobalProperties["ExtensionVersion"] = versionProvider.GetExtensionVersion();
        return client;
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
            _debugLog.Record("host", telemetryEvent.EventName, telemetryEvent.Properties,
                enabled: enabled, transmitted: false, error: ex.Message);
            TransmitExceptionEvent(ex, ImmutableDictionary<string, object>.Empty);
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
        var transmitted = false;
        string? transmitError = null;
        try
        {
            DumpTelemetryException(exception, additionalPropsArray);

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

        // Mirror the exception telemetry for debugging. The exception path is not gated by the
        // opt-out checker (hence enabled: null). `error` is a *transmission* failure, distinct from
        // the reported exception's own message, which is carried in props.
        _debugLog.Record("host", $"(exception) {exception.GetType().Name}",
            BuildExceptionProps(exception, additionalPropsArray),
            enabled: null, transmitted: transmitted, error: transmitError);
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

    /// <summary>Flushes any queued telemetry to Application Insights before this transmitter is disposed.</summary>
    public async ValueTask DisposeAsync()
    {
        _telemetryClient.Flush();
        await Task.Delay(1000);
    }
}
