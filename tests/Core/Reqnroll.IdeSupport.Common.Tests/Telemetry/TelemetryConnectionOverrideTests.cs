using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.Common.Tests.Telemetry;

public class TelemetryConnectionOverrideTests
{
    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "InstrumentationKey=abc", false)]
    [InlineData(false, null, false)]
    [InlineData(false, "InstrumentationKey=abc", false)]
    public void BlocksBuiltIn_only_for_a_debug_build_without_an_override(bool isDebug, string? resolved, bool expected)
    {
        TelemetryConnectionOverride.BlocksBuiltIn(isDebug, resolved).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_or_blank_is_no_override_and_not_reported_invalid(string? value)
    {
        var invalid = new List<string>();

        TelemetryConnectionOverride.Resolve(value, invalid.Add).Should().BeNull();

        invalid.Should().BeEmpty();
    }

    [Theory]
    [InlineData("InstrumentationKey=abc")]
    [InlineData("  InstrumentationKey=abc;IngestionEndpoint=https://localhost:1234/  ")]
    [InlineData("IngestionEndpoint=https://localhost/;instrumentationkey=abc")]
    public void A_connection_string_with_an_instrumentation_key_is_accepted_trimmed(string value)
    {
        TelemetryConnectionOverride.Resolve(value).Should().Be(value.Trim());
    }

    [Theory]
    [InlineData("not-a-connection-string")]
    [InlineData("InstrumentationKey=")]
    [InlineData("IngestionEndpoint=https://localhost/")]
    public void A_value_without_a_usable_instrumentation_key_is_ignored_and_reported(string value)
    {
        var invalid = new List<string>();

        TelemetryConnectionOverride.Resolve(value, invalid.Add).Should().BeNull();

        invalid.Should().ContainSingle();
    }
}
