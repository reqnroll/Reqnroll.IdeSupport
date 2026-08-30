using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector.AssemblyReflection;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// Creates the <see cref="OutProcReqnrollConnector"/> appropriate for a project scope.
/// </summary>
/// <remarks>
/// Selecting between the generic connector and a user-configured custom connector
/// (<see cref="Common.Configuration.BindingDiscoveryConfiguration.ConnectorPath"/>) is the
/// single responsibility of this seam, so that <see cref="ConnectorDiscoveryService"/> stays
/// free of connector-construction details and can be unit-tested with a fake connector.
/// </remarks>
public interface IOutProcConnectorFactory
{
    /// <summary>
    /// Creates a connector for <paramref name="scope"/>: a
    /// <see cref="CustomOutProcReqnrollConnector"/> when the project's binding-discovery
    /// configuration specifies a connector path, otherwise a
    /// <see cref="GenericOutProcReqnrollConnector"/>.
    /// </summary>
    OutProcReqnrollConnector Create(IProjectScope scope);
}
