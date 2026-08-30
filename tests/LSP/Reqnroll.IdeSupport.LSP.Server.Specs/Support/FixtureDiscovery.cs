using System.Diagnostics;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Discovery.AssemblyReflection;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.Support;

/// <summary>
/// Runs the <em>real</em> ported out-of-process binding discovery against the prebuilt
/// <c>ReqnrollBindingsFixture</c> assembly that is deployed next to the test host.
/// This exercises the genuine connector pipeline end-to-end (connector process →
/// Reqnroll BindingProviderService → DiscoveryResult → BindingImporter → ProjectBindingRegistry).
/// </summary>
public static class FixtureDiscovery
{
    public static string ConnectorsFolder =>
        Path.Combine(AppContext.BaseDirectory, "Connectors");

    public static string FixtureFolder =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bindings");

    public static string FixtureAssemblyPath =>
        Path.Combine(FixtureFolder, "ReqnrollBindingsFixture.dll");

    /// <summary>True when the connector binaries and the fixture assembly were both deployed.</summary>
    public static bool IsAvailable =>
        Directory.Exists(Path.Combine(ConnectorsFolder, "Reqnroll-Generic-net10.0")) &&
        File.Exists(FixtureAssemblyPath);

    public static ProjectBindingRegistry Discover()
    {
        var logger = new SilentDeveroomLogger();
        var scope = BuildScope(logger);
        var factory = new OutProcReqnrollConnectorFactory(logger);
        var service = new ConnectorDiscoveryService(logger, factory, new FileSystemForIDE());

        var (registry, _) = service.RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, lastHash: string.Empty, CancellationToken.None);
        return registry;
    }

    /// <summary>
    /// Builds the project scope describing the fixture assembly via the real
    /// <see cref="LspReqnrollProject"/>, so discovery shares the production configuration
    /// resolution (default configuration → generic connector).
    /// </summary>
    private static IProjectScope BuildScope(IIdeSupportLogger logger)
    {
        var ideScope = new LspIdeScope(logger);
        var info = new ReqnrollProjectLoadedParams
        {
            WorkspaceFolder        = FixtureFolder,
            ProjectFile            = Path.Combine(FixtureFolder, "ReqnrollBindingsFixture.csproj"),
            ProjectFolder          = FixtureFolder,
            OutputAssemblyPath     = FixtureAssemblyPath,
            TargetFrameworkMoniker = ".NETCoreApp,Version=v10.0"
        };
        return new LspReqnrollProject(info, ideScope);
    }

    private sealed class SilentDeveroomLogger : IIdeSupportLogger
    {
        public TraceLevel Level => TraceLevel.Off;
        public void Log(LogMessage message) { }
    }
}
