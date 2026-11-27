using Reqnroll.IdeSupport.Common.Configuration.Model;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector.AssemblyReflection;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

public class GenericOutProcReqnrollConnectorTests
{
    // The connectors folder defaults to the extension folder when no "Connectors" subfolder
    // exists, so paths are predictable against this temp root.
    private readonly string _extensionFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    /// <summary>Exposes the protected per-TFM path-selection logic for assertions.</summary>
    private sealed class TestableGenericConnector : GenericOutProcReqnrollConnector
    {
        public TestableGenericConnector(string tfm, string extensionFolder)
            : base(
                new IdeSupportConfiguration(),
                Substitute.For<IIdeSupportLogger>(),
                TargetFrameworkMoniker.Create(tfm),
                extensionFolder,
                ProcessorArchitectureSetting.UseSystem,
                DiscoveryTestSupport.MinimalProjectSettings(TargetFrameworkMoniker.Create(tfm)),
                NullLspTelemetryService.Instance)
        {
        }

        public string CallGetConnectorPath(List<string> arguments) => GetConnectorPath(arguments);
    }

    private (string Path, List<string> Args) Resolve(string tfm)
    {
        var args = new List<string>();
        var sut = new TestableGenericConnector(tfm, _extensionFolder);
        var path = sut.CallGetConnectorPath(args);
        return (path, args);
    }

    // ── .NET Framework: returns the .exe path directly, no "exec" arguments ────

    [Theory]
    [InlineData(".NETFramework,Version=v4.6.2", "Reqnroll-Generic-net462/reqnroll-ide-connector.exe")]
    [InlineData(".NETFramework,Version=v4.7.2", "Reqnroll-Generic-net472/reqnroll-ide-connector.exe")]
    [InlineData(".NETFramework,Version=v4.8.1", "Reqnroll-Generic-net481/reqnroll-ide-connector.exe")]
    public void GetConnectorPath_for_netfx_returns_exe_for_the_matching_minor_version(
        string tfm, string expectedRelative)
    {
        var (path, args) = Resolve(tfm);

        path.Should().Be(Path.Combine(_extensionFolder, expectedRelative));
        args.Should().BeEmpty("netfx connectors are launched directly, not via 'dotnet exec'");
    }

    // ── Modern .NET: one net6.0 connector, launched via "dotnet exec" ──────────
    // Its runtimeconfig.json rolls forward to the newest installed runtime (covered by
    // ConnectorRollForwardTests in LSP.Connector.Tests), so every .NET version - including
    // ones newer than any connector build - is sent to the same dll.

    private const string NetConnector = "Reqnroll-Generic-net6.0/reqnroll-ide-connector.dll";

    [Theory]
    [InlineData(".NETCoreApp,Version=v6.0")]
    [InlineData(".NETCoreApp,Version=v7.0")]
    [InlineData(".NETCoreApp,Version=v8.0")]
    [InlineData(".NETCoreApp,Version=v9.0")]
    [InlineData(".NETCoreApp,Version=v10.0")]
    [InlineData(".NETCoreApp,Version=v11.0")]
    [InlineData(".NETCoreApp,Version=v12.0")]
    public void GetConnectorPath_for_netcore_uses_dotnet_exec_with_the_single_net_connector(string tfm)
    {
        var (path, args) = Resolve(tfm);

        // Launcher is the dotnet host; the connector dll is passed as an "exec" argument.
        // The host binary name is OS-dependent ("dotnet.exe" on Windows, "dotnet" elsewhere) -
        // see OutProcReqnrollConnector.GetDotNetCommand / ResolveNonWindowsDotNetCommand.
        path.Should().EndWith(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        args.Should().Equal("exec", Path.Combine(_extensionFolder, NetConnector));
    }

    [Fact]
    public void GetConnectorPath_uses_the_net_connector_when_target_framework_has_no_version()
    {
        // ".NETCoreApp" with no Version=v... part: HasVersion is false, so it is not routed to a
        // .NET Framework connector and gets the .NET one.
        var (path, args) = Resolve(".NETCoreApp");

        path.Should().EndWith(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        args.Should().Equal("exec", Path.Combine(_extensionFolder, NetConnector));
    }
}
