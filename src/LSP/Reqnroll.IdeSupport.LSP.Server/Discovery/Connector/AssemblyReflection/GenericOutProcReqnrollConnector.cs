using Reqnroll.IdeSupport.Common.Configuration.Model;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector.AssemblyReflection;

/// <summary>Connector that selects and runs the bundled generic Reqnroll discovery connector matching the project's target framework.</summary>
public class GenericOutProcReqnrollConnector : OutProcReqnrollConnector
{
    // Forward slash, not backslash: these are joined onto a folder via Path.Combine, which does
    // not reinterpret separators embedded inside its own arguments for the current OS. A literal
    // "\" here survives unchanged into the final path on Linux/macOS, where it's just a filename
    // character rather than a directory separator, producing a path to a file that doesn't exist
    // (confirmed via a real Rider/Linux devcontainer run: "dotnet exec .../net8.0\reqnroll-ide-
    // connector.dll" — note the literal backslash — failed to launch). "/" is accepted as a path
    // separator on Windows too, so it works cross-platform without needing Path.Combine here.
    private const string ConnectorNet462 = "Reqnroll-Generic-net462/reqnroll-ide-connector.exe";
    private const string ConnectorNet472 = "Reqnroll-Generic-net472/reqnroll-ide-connector.exe";
    private const string ConnectorNet481 = "Reqnroll-Generic-net481/reqnroll-ide-connector.exe";
    // One .NET connector serves every .NET project: it is built for net6.0 with
    // "rollForward": "LatestMajor" in its runtimeconfig.json, so the dotnet host runs it on the
    // newest installed runtime, which can load a test assembly built for any older or equal .NET.
    private const string ConnectorNet = "Reqnroll-Generic-net6.0/reqnroll-ide-connector.dll";

    /// <summary>Creates a connector that discovers bindings for a project targeting <paramref name="targetFrameworkMoniker"/>.</summary>
    public GenericOutProcReqnrollConnector(
        IdeSupportConfiguration configuration,
        IIdeSupportLogger logger,
        TargetFrameworkMoniker targetFrameworkMoniker,
        string extensionFolder,
        ProcessorArchitectureSetting processorArchitecture,
        ProjectSettings projectSettings,
        ITelemetryService telemetryService)
        : base(
            configuration,
            logger,
            targetFrameworkMoniker,
            extensionFolder,
            processorArchitecture,
            projectSettings,
            telemetryService)
    {
    }

    /// <summary>Picks the bundled generic connector for the project's target framework moniker and returns its path (or a <c>dotnet exec</c> command line for .NET Core/.NET targets).</summary>
    protected override string GetConnectorPath(List<string> arguments)
    {
        if (_targetFrameworkMoniker.IsNetFramework && _targetFrameworkMoniker.HasVersion)
        {
            var netFxConnector = _targetFrameworkMoniker.Version.Minor switch
            {
                6 => ConnectorNet462,
                7 => ConnectorNet472,
                _ => ConnectorNet481,
            };
            return Path.Combine(GetConnectorsFolder(), netFxConnector);
        }

        return GetDotNetExecCommand(arguments, GetConnectorsFolder(), ConnectorNet);
    }
}
