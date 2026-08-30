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
    /// Runs discovery for <paramref name="scope"/>.
    /// </summary>
    /// <returns>
    /// A new <see cref="ProjectBindingRegistry"/> and its content hash when discovery
    /// succeeds.  Returns (<paramref name="lastGood"/>, <paramref name="lastHash"/>) unchanged
    /// when the assembly is missing, unchanged, or the connector fails.
    /// </returns>
    (ProjectBindingRegistry Registry, string Hash) RunDiscovery(
        IProjectScope scope,
        ProjectBindingRegistry lastGood,
        string lastHash,
        CancellationToken ct);
}
