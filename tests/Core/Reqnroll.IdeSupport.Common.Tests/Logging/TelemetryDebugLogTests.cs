using System.IO;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.Common.Tests.Logging;

public class TelemetryDebugLogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("False")]
    public void FromValue_returns_disabled_sink_for_off_values(string? value)
    {
        TelemetryDebugLog.FromValue(value).IsEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void FromValue_returns_enabled_file_sink_for_on_values(string value)
    {
        TelemetryDebugLog.FromValue(value).Should().BeOfType<FileTelemetryDebugLog>()
            .Which.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void FromValue_treats_other_values_as_a_file_path()
    {
        TelemetryDebugLog.FromValue(@"C:\some\telemetry.jsonl").Should().BeOfType<FileTelemetryDebugLog>();
    }

    [Fact]
    public void FileSink_appends_one_json_object_per_event()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reqnroll-tel-{Guid.NewGuid():N}.jsonl");
        try
        {
            var sink = new FileTelemetryDebugLog(path);

            sink.Record("server", "Reqnroll Discovery executed",
                new Dictionary<string, object> { ["DiscoverySource"] = "Connector", ["StepDefinitionCount"] = 42 });
            sink.Record("host", "Extension loaded", null, enabled: true, transmitted: true);

            var lines = File.ReadAllLines(path);
            lines.Should().HaveCount(2);

            var first = JObject.Parse(lines[0]);
            first["source"]!.Value<string>().Should().Be("server");
            first["event"]!.Value<string>().Should().Be("Reqnroll Discovery executed");
            first["props"]!["DiscoverySource"]!.Value<string>().Should().Be("Connector");
            first["props"]!["StepDefinitionCount"]!.Value<int>().Should().Be(42);

            var second = JObject.Parse(lines[1]);
            second["source"]!.Value<string>().Should().Be("host");
            second["enabled"]!.Value<bool>().Should().BeTrue();
            second["transmitted"]!.Value<bool>().Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FileSink_never_throws_on_an_invalid_path()
    {
        var sink = new FileTelemetryDebugLog("Z:\\does\\not\\exist\\<>:|?.jsonl");
        var act = () => sink.Record("server", "E", null);
        act.Should().NotThrow();
    }
}
