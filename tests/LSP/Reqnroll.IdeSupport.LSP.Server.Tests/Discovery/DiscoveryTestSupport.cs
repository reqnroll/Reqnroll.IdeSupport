using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

/// <summary>
/// Shared builders for the connector-discovery unit tests.
/// </summary>
internal static class DiscoveryTestSupport
{
    /// <summary>
    /// Builds a minimal <see cref="ProjectSettings"/> matching what the production factory uses.
    /// </summary>
    public static ProjectSettings MinimalProjectSettings(TargetFrameworkMoniker? tfm)
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

    /// <summary>
    /// Creates a real <see cref="LspReqnrollProject"/> and seeds its property bag with a
    /// configuration provider returning <paramref name="connectorPath"/> so that
    /// <c>GetIdeSupportConfiguration()</c> does not touch the file system.
    /// </summary>
    public static LspReqnrollProject MakeProject(
        IIdeScope ideScope,
        string projectFolder,
        string targetFrameworkMoniker = ".NETCoreApp,Version=v8.0",
        string? outputAssemblyPath = null,
        string? connectorPath = null)
    {
        var info = new ReqnrollProjectLoadedParams
        {
            WorkspaceFolder        = projectFolder,
            ProjectFile            = Path.Combine(projectFolder, "MyApp.Tests.csproj"),
            ProjectFolder          = projectFolder,
            OutputAssemblyPath     = outputAssemblyPath
                                     ?? Path.Combine(projectFolder, "bin", "Debug", "MyApp.Tests.dll"),
            TargetFrameworkMoniker = targetFrameworkMoniker
        };

        var project = new LspReqnrollProject(info, ideScope);

        var config = new IdeSupportConfiguration();
        config.BindingDiscovery.ConnectorPath = connectorPath;

        var configProvider = Substitute.For<IIdeSupportConfigurationProvider>();
        configProvider.GetConfiguration().Returns(config);

        // Seed the cache slot that GetIdeSupportConfiguration() reads from.
        project.Properties[typeof(IIdeSupportConfigurationProvider)] = configProvider;

        return project;
    }

    public static IIdeSupportLogger SilentLogger() => Substitute.For<IIdeSupportLogger>();
}
