#nullable enable

using System;
using System.Linq;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.ServiceBroker;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Diagnostics;

/// <summary>
/// Logs the full-access service broker's <c>AvailabilityChanged</c> events and the service
/// monikers each one impacts (issues #156 and #555).
/// </summary>
/// <remarks>
/// <para>
/// This is the "link 2" instrument the issue #156 investigation left open. That investigation
/// decompile-verified every step of the chain that rebuilds this extension's language client —
/// <c>AvailabilityChanged</c> → <c>ExtensionPartManager.InvalidateServiceMoniker</c> → the part
/// record being replaced → <c>ProviderRemoved</c>/<c>ProviderAdded</c> → a forced second
/// <c>CreateServerConnectionAsync</c> — except for what raises the event in the first place. Every
/// link downstream of it was verified by reading VS's own assemblies; this one can only be
/// answered from a live session, because the answer is a value (which monikers) rather than a code
/// path.
/// </para>
/// <para>
/// For issue #555 it also serves a second purpose: if a solution swap raises
/// <c>AvailabilityChanged</c> naming this extension's own parts, then the swap is expected to
/// rebuild the language client, and "the LSP never comes back" is a failure to complete a rebuild
/// VS asked for — a different defect from the shutdown-token one, and one that would otherwise be
/// invisible in the logs.
/// </para>
/// <para>
/// Best-effort throughout: the brokered-service container is optional infrastructure from this
/// extension's point of view, and a diagnostic that cannot subscribe must degrade to a log line,
/// never an exception.
/// </para>
/// </remarks>
internal sealed class ServiceBrokerAvailabilityMonitor : IDisposable
{
    private readonly IServiceBroker _serviceBroker;
    private readonly IIdeSupportLogger _logger;
    private readonly EventHandler<BrokeredServicesChangedEventArgs> _handler;
    private bool _disposed;

    private ServiceBrokerAvailabilityMonitor(IServiceBroker serviceBroker, IIdeSupportLogger logger)
    {
        _serviceBroker = serviceBroker;
        _logger = logger;
        _handler = OnAvailabilityChanged;
    }

    /// <summary>
    /// Resolves the full-access service broker and subscribes to its availability changes. Returns
    /// <see langword="null"/> if the broker is unavailable or subscription fails. Must be called on
    /// the UI thread.
    /// </summary>
    public static ServiceBrokerAvailabilityMonitor? TrySubscribe(IIdeSupportLogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var container = ServiceProvider.GlobalProvider.GetService(typeof(SVsBrokeredServiceContainer))
                as IBrokeredServiceContainer;
            if (container is null)
            {
                logger.LogInfo("ServiceBrokerAvailabilityMonitor: SVsBrokeredServiceContainer unavailable; not subscribing.");
                return null;
            }

            var broker = container.GetFullAccessServiceBroker();
            if (broker is null)
            {
                logger.LogInfo("ServiceBrokerAvailabilityMonitor: full-access service broker unavailable; not subscribing.");
                return null;
            }

            var monitor = new ServiceBrokerAvailabilityMonitor(broker, logger);
            broker.AvailabilityChanged += monitor._handler;

            logger.LogInfo(
                "ServiceBrokerAvailabilityMonitor: subscribed — brokered-service availability changes will be logged " +
                "(issue #156 link 2, issue #555).");
            return monitor;
        }
        catch (Exception ex)
        {
            logger.LogException(ex, "ServiceBrokerAvailabilityMonitor: could not subscribe to AvailabilityChanged.");
            return null;
        }
    }

    private void OnAvailabilityChanged(object sender, BrokeredServicesChangedEventArgs e)
    {
        try
        {
            var monikers = e.ImpactedServices is null
                ? "(none reported)"
                : string.Join(", ", e.ImpactedServices.Select(m => m.Name + (m.Version is null ? string.Empty : $"@{m.Version}")));

            var mentionsReqnroll = e.ImpactedServices is not null
                && e.ImpactedServices.Any(m => m.Name?.IndexOf("Reqnroll", StringComparison.OrdinalIgnoreCase) >= 0);

            var message =
                $"ServiceBrokerAvailabilityMonitor: AvailabilityChanged — otherServicesImpacted={e.OtherServicesImpacted}, " +
                $"impactedServices=[{monikers}]";

            // Anything naming this extension is the case issue #156 could not pin down, so it is
            // worth a level that survives a shipped-build log; the rest is background churn.
            if (mentionsReqnroll)
                _logger.LogWarning(message + " — names a Reqnroll service: this is the issue #156 part-churn trigger.");
            else
                _logger.LogVerbose(message);
        }
        catch (Exception ex)
        {
            try { _logger.LogDebugException(ex, "ServiceBrokerAvailabilityMonitor: failed to log an availability change."); }
            catch { /* diagnostics must never throw into a VS event handler */ }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _serviceBroker.AvailabilityChanged -= _handler;
        }
        catch (Exception ex)
        {
            _logger.LogDebugException(ex, "ServiceBrokerAvailabilityMonitor: unsubscribe failed.");
        }
    }
}
