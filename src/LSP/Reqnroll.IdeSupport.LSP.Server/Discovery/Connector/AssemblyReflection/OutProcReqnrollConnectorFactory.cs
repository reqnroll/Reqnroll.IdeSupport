using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector.AssemblyReflection;

/// <summary>
/// Default <see cref="IOutProcConnectorFactory"/>.  Builds a generic connector for the
/// project's target framework, or a custom connector when the project's binding-discovery
/// configuration specifies a <see cref="BindingDiscoveryConfiguration.ConnectorPath"/>.
/// </summary>
/// <remarks>
/// The extension folder is resolved from <see cref="AppContext.BaseDirectory"/> so the
/// connector binaries shipped alongside the server process are found automatically.
/// </remarks>
public sealed class OutProcReqnrollConnectorFactory : IOutProcConnectorFactory
{
    private readonly IIdeSupportLogger _logger;

    /// <summary>Initializes a new instance of the <see cref="OutProcReqnrollConnectorFactory"/> class.</summary>
    public OutProcReqnrollConnectorFactory(IIdeSupportLogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public OutProcReqnrollConnector Create(IProjectScope scope)
    {
        var configuration   = scope.GetIdeSupportConfiguration();
        var tfm             = TargetFrameworkMoniker.Create(scope.TargetFrameworkMoniker);
        var extensionFolder = AppContext.BaseDirectory;
        var processorArch   = ResolveProcessorArchitecture(configuration);
        var projectSettings = BuildMinimalProjectSettings(tfm);

        if (configuration.BindingDiscovery.ConnectorPath is not null)
            return new CustomOutProcReqnrollConnector(
                configuration, _logger, tfm, extensionFolder,
                processorArch, projectSettings, NullTelemetryService.Instance);

        return new GenericOutProcReqnrollConnector(
            configuration, _logger, tfm, extensionFolder,
            processorArch, projectSettings, NullTelemetryService.Instance);
    }

    private static ProcessorArchitectureSetting ResolveProcessorArchitecture(IdeSupportConfiguration configuration)
        => configuration.ProcessorArchitecture != ProcessorArchitectureSetting.AutoDetect
            ? configuration.ProcessorArchitecture
            : ProcessorArchitectureSetting.UseSystem;

    /// <summary>
    /// Builds a minimal <see cref="ProjectSettings"/> record with only the fields that the
    /// generic connector uses.  Richer project analysis (Reqnroll version, platform target, etc.)
    /// is a prerequisite improvement tracked as a cleanup item.
    /// </summary>
    private static ProjectSettings BuildMinimalProjectSettings(TargetFrameworkMoniker? tfm)
        => new(
            IdeSupportProjectKind.ReqnrollTestProject,
            tfm!,
            tfm?.Value ?? string.Empty,
            ProjectPlatformTarget.AnyCpu,
            string.Empty,
            string.Empty,
            ReqnrollVersion: null!,
            string.Empty,
            string.Empty,
            ReqnrollProjectTraits.None,
            ProjectProgrammingLanguage.CSharp);
}
