using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

public class ConnectorRunTelemetryTests
{
    [Theory]
    [InlineData("2.1.3", "2.1")]
    [InlineData("2.1.3-beta.4+build5", "2.1")]
    [InlineData("v3.0", "3.0")]
    [InlineData("4", "4")]
    [InlineData("  1.2.3.4 ", "1.2")]
    [InlineData("C:\\Users\\me\\Reqnroll.dll", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeVersion_reduces_to_major_minor(string? input, string? expected)
        => ConnectorRunTelemetry.NormalizeVersion(input).Should().Be(expected);

    [Fact]
    public void FromConnectorProperties_tolerates_null_and_missing_keys()
    {
        ConnectorRunTelemetry.FromConnectorProperties(null)
            .Should().Be(new ConnectorRunTelemetry(null, false, null, null));
        ConnectorRunTelemetry.FromConnectorProperties(new Dictionary<string, object>())
            .Should().Be(new ConnectorRunTelemetry(null, false, null, null));
    }

    [Fact]
    public void FromConnectorProperties_ignores_wrongly_typed_values()
    {
        var props = new Dictionary<string, object>
        {
            ["ReqnrollVersion"] = 5, ["LegacySpecFlow"] = "yes", ["ConnectorType"] = 3, ["ConnectorExitCode"] = "0",
        };

        ConnectorRunTelemetry.FromConnectorProperties(props)
            .Should().Be(new ConnectorRunTelemetry(null, false, null, null));
    }

    [Fact]
    public void AddTo_omits_unknown_values_but_always_reports_LegacySpecFlow()
    {
        var target = new Dictionary<string, object?>();

        new ConnectorRunTelemetry(null, false, null, null).AddTo(target);

        target.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, object?>("LegacySpecFlow", false));
    }
}
