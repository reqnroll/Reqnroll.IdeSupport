using Reqnroll.IdeSupport.LSP.Connector.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

public class ConnectorJsonSerializationTests
{
    [Fact]
    public void SerializeAndMark_wraps_payload_in_start_and_end_markers()
    {
        var marked = ConnectorJsonSerialization.SerializeAndMark(new DiscoveryResult());

        marked.Should().Contain(ConnectorJsonSerialization.StartMarker);
        marked.Should().Contain(ConnectorJsonSerialization.EndMarker);
    }

    [Fact]
    public void DeserializeObjectWithMarker_round_trips_a_discovery_result()
    {
        var original = new DiscoveryResult
        {
            ReqnrollVersion = "2.1.0",
            StepDefinitions =
            [
                new StepDefinition
                {
                    Type           = "Given",
                    Regex          = "^the first number is (.*)$",
                    Method         = "MyApp.Steps.SetFirstNumber",
                    ParamTypes     = "i",
                    SourceLocation = "Steps.cs|10|5"
                }
            ]
        };

        var marked = ConnectorJsonSerialization.SerializeAndMark(original);
        var roundTripped = ConnectorJsonSerialization.DeserializeObjectWithMarker<DiscoveryResult>(marked);

        roundTripped.Should().NotBeNull();
        roundTripped!.ReqnrollVersion.Should().Be("2.1.0");
        roundTripped.StepDefinitions.Should().HaveCount(1);
        roundTripped.StepDefinitions[0].Method.Should().Be("MyApp.Steps.SetFirstNumber");
        roundTripped.StepDefinitions[0].Regex.Should().Be("^the first number is (.*)$");
    }

    [Fact]
    public void DeserializeObjectWithMarker_tolerates_surrounding_console_noise()
    {
        var payload = ConnectorJsonSerialization.SerializeAndMark(
            new DiscoveryResult { ReqnrollVersion = "9.9.9" });
        var noisy = "build log line 1\n" + payload + "\ntrailing diagnostic line\n";

        var result = ConnectorJsonSerialization.DeserializeObjectWithMarker<DiscoveryResult>(noisy);

        result.Should().NotBeNull();
        result!.ReqnrollVersion.Should().Be("9.9.9");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no markers at all")]
    [InlineData(">>>>>>>>>> only the start marker")]
    public void DeserializeObjectWithMarker_returns_default_when_markers_are_absent(string raw)
    {
        ConnectorJsonSerialization.DeserializeObjectWithMarker<DiscoveryResult>(raw)
            .Should().BeNull();
    }
}
