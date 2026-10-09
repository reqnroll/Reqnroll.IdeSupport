using Reqnroll.IdeSupport.LSP.Core.Bindings;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>How one <see cref="IConnectorDiscoveryService.RunDiscovery"/> call ended.</summary>
public enum ConnectorDiscoveryStatus
{
    /// <summary>The connector ran and produced a new registry for a changed assembly.</summary>
    Discovered,

    /// <summary>The assembly is unchanged since the last successful run; the connector was not invoked.</summary>
    Unchanged,

    /// <summary>The connector was invoked but threw or reported a failure.</summary>
    Failed,

    /// <summary>
    /// Discovery could not run: no output assembly (path unset or not built yet), or the project is
    /// not a Reqnroll test project. Nothing was discovered and nothing failed.
    /// </summary>
    Skipped,
}

/// <summary>
/// The result of one <see cref="IConnectorDiscoveryService.RunDiscovery"/> call: its
/// <see cref="Status"/> plus the registry and assembly hash the caller should hold afterwards.
/// </summary>
/// <remarks>
/// Every status except <see cref="ConnectorDiscoveryStatus.Discovered"/> carries the caller's own
/// last-good registry and hash back unchanged, so the hash alone cannot tell an unchanged assembly
/// from a failed or skipped run; <see cref="Status"/> does (issue #939).
/// </remarks>
/// <param name="Status">How the run ended.</param>
/// <param name="Registry">The new registry when discovered; otherwise the caller's last-good registry.</param>
/// <param name="Hash">The new assembly hash when discovered; otherwise the caller's last hash.</param>
public sealed record ConnectorDiscoveryOutcome(
    ConnectorDiscoveryStatus Status, ProjectBindingRegistry Registry, string Hash)
{
    /// <summary>A new registry for a changed assembly.</summary>
    public static ConnectorDiscoveryOutcome Discovered(ProjectBindingRegistry registry, string hash) =>
        new(ConnectorDiscoveryStatus.Discovered, registry, hash);

    /// <summary>The assembly is unchanged; the last-good state is kept.</summary>
    public static ConnectorDiscoveryOutcome Unchanged(ProjectBindingRegistry lastGood, string lastHash) =>
        new(ConnectorDiscoveryStatus.Unchanged, lastGood, lastHash);

    /// <summary>The connector failed; the last-good state is kept.</summary>
    public static ConnectorDiscoveryOutcome Failed(ProjectBindingRegistry lastGood, string lastHash) =>
        new(ConnectorDiscoveryStatus.Failed, lastGood, lastHash);

    /// <summary>Discovery could not run; the last-good state is kept.</summary>
    public static ConnectorDiscoveryOutcome Skipped(ProjectBindingRegistry lastGood, string lastHash) =>
        new(ConnectorDiscoveryStatus.Skipped, lastGood, lastHash);

    /// <summary>
    /// Deconstructs into the registry and hash only, for callers that just want the resulting state
    /// (the shape <see cref="IConnectorDiscoveryService.RunDiscovery"/> returned before issue #939).
    /// </summary>
    public void Deconstruct(out ProjectBindingRegistry registry, out string hash)
    {
        registry = Registry;
        hash = Hash;
    }
}
