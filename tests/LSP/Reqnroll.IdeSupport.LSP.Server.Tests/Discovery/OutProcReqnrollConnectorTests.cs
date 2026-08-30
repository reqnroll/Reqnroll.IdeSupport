using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

public class OutProcReqnrollConnectorTests
{
    /// <summary>
    /// Connector stub that points at a caller-supplied (non-existent) connector path so the
    /// base <see cref="OutProcReqnrollConnector.RunDiscovery"/> takes its missing-connector
    /// guard branch without spawning a process.  Named with the conventional suffix so
    /// <c>GetConnectorType()</c> resolves to "Fake".
    /// </summary>
    private sealed class FakeOutProcReqnrollConnector : OutProcReqnrollConnector
    {
        private readonly string _connectorPath;

        public FakeOutProcReqnrollConnector(string connectorPath)
            : base(
                new DeveroomConfiguration(),
                Substitute.For<IIdeSupportLogger>(),
                TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0"),
                AppContext.BaseDirectory,
                ProcessorArchitectureSetting.UseSystem,
                DiscoveryTestSupport.MinimalProjectSettings(
                    TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0")),
                NullLspTelemetryService.Instance)
        {
            _connectorPath = connectorPath;
        }

        protected override string GetConnectorPath(List<string> arguments) => _connectorPath;
    }

    [Fact]
    public void RunDiscovery_returns_failed_result_when_connector_executable_is_missing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-no-connector.exe");
        var sut = new FakeOutProcReqnrollConnector(missingPath);

        var result = sut.RunDiscovery(
            testAssemblyPath: Path.Combine(Path.GetTempPath(), "SomeAssembly.dll"),
            configFilePath: string.Empty);

        result.IsFailed.Should().BeTrue();
        result.ErrorMessage.Should().Contain("Unable to find connector");
        result.ErrorMessage.Should().Contain(missingPath);
    }

    [Fact]
    public void RunDiscovery_stamps_the_connector_type_on_the_result()
    {
        var sut = new FakeOutProcReqnrollConnector(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-no-connector.exe"));

        var result = sut.RunDiscovery(
            testAssemblyPath: Path.Combine(Path.GetTempPath(), "SomeAssembly.dll"),
            configFilePath: string.Empty);

        // GetConnectorType() strips the "OutProcReqnrollConnector" suffix from the type name.
        result.ConnectorType.Should().Be("Fake");
    }

    // ── Non-Windows dotnet-host resolution (see GetDotNetCommand) ──────────────
    // Exercised directly against the extracted pure function so both branches are
    // covered regardless of which OS actually runs the test.

    [Fact]
    public void ResolveNonWindowsDotNetCommand_falls_back_to_bare_dotnet_when_nothing_else_resolves()
    {
        var command = OutProcReqnrollConnector.ResolveNonWindowsDotNetCommand(
            dotNetRoot: null, userHome: null, fileExists: _ => false);

        command.Should().Be("dotnet", "PATH resolution is the standard install on Linux/macOS");
    }

    [Fact]
    public void ResolveNonWindowsDotNetCommand_falls_back_to_bare_dotnet_when_DOTNET_ROOT_is_empty()
    {
        var command = OutProcReqnrollConnector.ResolveNonWindowsDotNetCommand(
            dotNetRoot: "", userHome: null, fileExists: _ => false);

        command.Should().Be("dotnet", "PATH resolution is the standard install on Linux/macOS");
    }

    [Fact]
    public void ResolveNonWindowsDotNetCommand_prefers_DOTNET_ROOT_when_set()
    {
        var dotNetRoot = Path.Combine(Path.GetTempPath(), "dotnet-root");

        var command = OutProcReqnrollConnector.ResolveNonWindowsDotNetCommand(
            dotNetRoot, userHome: null, fileExists: _ => false);

        command.Should().Be(Path.Combine(dotNetRoot, "dotnet"));
    }

    [Fact]
    public void ResolveNonWindowsDotNetCommand_falls_back_to_conventional_dotnet_install_dir_when_present()
    {
        // Confirmed necessary live: in a Rider/Linux devcontainer, "dotnet" was not resolvable
        // via PATH for our process (nor for Rider's own JVM, whose environment ours inherits),
        // but the standard dotnet-install.sh location under $HOME/.dotnet/dotnet did exist.
        var home = Path.Combine(Path.GetTempPath(), "home-with-dotnet");
        var expected = Path.Combine(home, ".dotnet", "dotnet");

        var command = OutProcReqnrollConnector.ResolveNonWindowsDotNetCommand(
            dotNetRoot: null, userHome: home, fileExists: path => path == expected);

        command.Should().Be(expected);
    }

    [Fact]
    public void ResolveNonWindowsDotNetCommand_falls_back_to_bare_dotnet_when_conventional_install_dir_is_absent()
    {
        var home = Path.Combine(Path.GetTempPath(), "home-without-dotnet");

        var command = OutProcReqnrollConnector.ResolveNonWindowsDotNetCommand(
            dotNetRoot: null, userHome: home, fileExists: _ => false);

        command.Should().Be("dotnet", "no DOTNET_ROOT, no conventional install dir — PATH is the last resort");
    }

    [Fact]
    public void ResolveNonWindowsDotNetCommand_DOTNET_ROOT_takes_priority_over_the_conventional_install_dir()
    {
        var dotNetRoot = Path.Combine(Path.GetTempPath(), "dotnet-root");
        var home = Path.Combine(Path.GetTempPath(), "home-with-dotnet");

        var command = OutProcReqnrollConnector.ResolveNonWindowsDotNetCommand(
            dotNetRoot, userHome: home, fileExists: _ => true);

        command.Should().Be(Path.Combine(dotNetRoot, "dotnet"));
    }
}
