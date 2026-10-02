using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector.AssemblyReflection;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

/// <summary>
/// Issue #846: the server-side telemetry overlay must keep the connector-reported type and version
/// reachable by <see cref="ConnectorRunTelemetry"/>, using the shape the real connector emits.
/// </summary>
public class OutProcReqnrollConnectorTelemetryTests
{
    private static readonly TargetFrameworkMoniker Tfm = TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0");

    // Mirrors a real connector run: no top-level ReqnrollVersion, product version only in telemetry.
    private static DiscoveryResult ConnectorResult(string? connectorType = "Generic", string? productVersion = "3.3.4+33eeea22")
    {
        var telemetry = new Dictionary<string, object>
        {
            ["ConnectorType"] = "Reqnroll-Generic-net8.0",
            ["SFFileVersion"] = "3.0.0",
        };
        if (productVersion is not null)
            telemetry["SFProductVersion"] = productVersion;
        return new DiscoveryResult { ConnectorType = connectorType!, TelemetryProperties = telemetry };
    }

    private static ConnectorRunTelemetry Apply(DiscoveryResult result, NuGetVersion? projectVersion = null)
    {
        OutProcReqnrollConnector.ApplyRunTelemetry(result, Tfm, projectVersion, "--secret some-path", 0);
        return ConnectorRunTelemetry.FromConnectorProperties(result.TelemetryProperties);
    }

    [Fact]
    public void ConnectorType_is_forwarded()
        => Apply(ConnectorResult()).ConnectorType.Should().Be("Generic");

    [Fact]
    public void Null_result_ConnectorType_does_not_erase_the_connectors_own_value()
        => Apply(ConnectorResult(connectorType: null)).ConnectorType.Should().Be("Reqnroll-Generic-net8.0");

    [Fact]
    public void ReqnrollVersion_comes_from_the_connectors_SFProductVersion()
        => Apply(ConnectorResult()).ReqnrollVersion.Should().Be("3.3");

    [Fact]
    public void ReqnrollVersion_falls_back_to_project_settings_when_connector_reports_none()
        => Apply(ConnectorResult(productVersion: "Unknown"), new NuGetVersion("2.4.1", "2.4.1")).ReqnrollVersion.Should().Be("2.4");

    [Fact]
    public void ReqnrollVersion_is_absent_when_nothing_reports_one()
        => Apply(ConnectorResult(productVersion: null)).ReqnrollVersion.Should().BeNull();

    [Fact]
    public void Result_ReqnrollVersion_wins_over_other_sources()
    {
        var result = ConnectorResult();
        result.ReqnrollVersion = "3.1.0";
        Apply(result, new NuGetVersion("2.4.1", "2.4.1")).ReqnrollVersion.Should().Be("3.1");
    }

    [Fact]
    public void Exit_code_is_forwarded_and_arguments_are_not()
    {
        var result = ConnectorResult();
        var telemetry = Apply(result);

        telemetry.ConnectorExitCode.Should().Be(0);
        var props = new Dictionary<string, object?>();
        telemetry.AddTo(props);
        props.Keys.Should().NotContain("ConnectorArguments");
    }
}
