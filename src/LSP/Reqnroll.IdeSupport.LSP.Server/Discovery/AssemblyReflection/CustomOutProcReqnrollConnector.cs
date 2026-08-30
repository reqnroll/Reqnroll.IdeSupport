using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery;

/// <summary>Connector that runs a user-configured discovery connector executable or DLL, as specified by <c>BindingDiscovery.ConnectorPath</c> in configuration.</summary>
public class CustomOutProcReqnrollConnector : OutProcReqnrollConnector
{
    /// <summary>Creates a connector that runs the connector path configured for the project.</summary>
    public CustomOutProcReqnrollConnector(IdeSupportConfiguration configuration, IIdeSupportLogger logger, TargetFrameworkMoniker targetFrameworkMoniker, string extensionFolder, ProcessorArchitectureSetting processorArchitecture, ProjectSettings projectSettings, ITelemetryService telemetryService) : base(configuration, logger, targetFrameworkMoniker, extensionFolder, processorArchitecture, projectSettings, telemetryService)
    {
    }

    /// <summary>Resolves the connector executable/DLL path from <c>BindingDiscovery.ConnectorPath</c>, expanding environment variables and building a <c>dotnet exec</c> command line if it points at a DLL.</summary>
    protected override string GetConnectorPath(List<string> arguments)
    {
        var connectorPath = Path.Combine(GetConnectorsFolder(), Environment.ExpandEnvironmentVariables(_configuration.BindingDiscovery.ConnectorPath ?? "<not specified>"));

        if (".dll".Equals(Path.GetExtension(connectorPath), StringComparison.OrdinalIgnoreCase))
            connectorPath = GetDotNetExecCommand(arguments, Path.GetDirectoryName(connectorPath), Path.GetFileName(connectorPath));

        return connectorPath;
    }
}