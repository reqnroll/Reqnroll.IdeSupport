using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Bindings;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// Orchestrates a single binding-discovery run for a project scope.
/// </summary>
/// <remarks>
/// Extracted as an interface so that <see cref="ConnectorBindingRegistryProvider"/> can be
/// unit-tested with a substituted discovery service.
/// </remarks>
public interface IConnectorDiscoveryService
{
    /// <summary>
    /// The whitelisted telemetry of the most recent run that actually invoked the connector
    /// (successful or not), or <see langword="null"/> when the latest run did not reach the
    /// connector (no assembly, hash match, non-Reqnroll project, invocation exception).
    /// Instances are per-project, so this is per-project state.
    /// </summary>
    ConnectorRunTelemetry? LastRunTelemetry { get; }

    /// <summary>
    /// Runs discovery for <paramref name="scope"/>.
    /// </summary>
    /// <returns>
    /// <see cref="ConnectorDiscoveryStatus.Discovered"/> with a new <see cref="ProjectBindingRegistry"/>
    /// and its content hash when discovery succeeds. Otherwise <paramref name="lastGood"/> and
    /// <paramref name="lastHash"/> unchanged, with a status saying why:
    /// <see cref="ConnectorDiscoveryStatus.Unchanged"/> (assembly hash match),
    /// <see cref="ConnectorDiscoveryStatus.Failed"/> (the connector threw or reported failure) or
    /// <see cref="ConnectorDiscoveryStatus.Skipped"/> (no output assembly, or not a Reqnroll test project).
    /// </returns>
    ConnectorDiscoveryOutcome RunDiscovery(
        IProjectScope scope,
        ProjectBindingRegistry lastGood,
        string lastHash,
        CancellationToken ct);
}
