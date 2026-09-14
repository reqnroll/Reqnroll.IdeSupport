namespace Reqnroll.IdeSupport.Common.Tests.Logging;

public class LogLineFormatterTests
{
    [Fact]
    public void FormatOrigin_combines_source_and_caller_when_both_are_known()
    {
        var message = new LogMessage(TraceLevel.Info, "hi", "RunDiscovery", Source: "ConnectorDiscoveryService");

        LogLineFormatter.FormatOrigin(message).Should().Be("ConnectorDiscoveryService.RunDiscovery");
    }

    [Fact]
    public void FormatOrigin_falls_back_to_source_alone_when_caller_is_empty()
    {
        var message = new LogMessage(TraceLevel.Info, "hi", CallerMethod: "", Source: "SomeCategory");

        LogLineFormatter.FormatOrigin(message).Should().Be("SomeCategory");
    }

    [Fact]
    public void FormatOrigin_falls_back_to_caller_alone_when_source_is_unknown()
    {
        var message = new LogMessage(TraceLevel.Info, "hi", "RunDiscovery");

        LogLineFormatter.FormatOrigin(message).Should().Be("RunDiscovery");
    }

    [Fact]
    public void FormatOrigin_returns_a_placeholder_when_neither_is_known()
    {
        var message = new LogMessage(TraceLevel.Info, "hi", CallerMethod: "");

        LogLineFormatter.FormatOrigin(message).Should().Be("?");
    }

    [Fact]
    public void FormatPreamble_renders_UTC_timestamp_padded_level_origin_and_thread_id()
    {
        var message = new LogMessage(TraceLevel.Info, "hi", "Method", Source: "Type");

        var preamble = LogLineFormatter.FormatPreamble(message);

        preamble.Should().MatchRegex(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[Info   \] Type\.Method \(tid=\d+\)$");
    }

    [Theory]
    [InlineData(TraceLevel.Error, "Error  ")]
    [InlineData(TraceLevel.Warning, "Warning")]
    [InlineData(TraceLevel.Info, "Info   ")]
    [InlineData(TraceLevel.Verbose, "Verbose")]
    public void FormatPreamble_pads_every_real_level_to_the_same_width(TraceLevel level, string expectedPadded)
    {
        var message = new LogMessage(level, "hi", "Method");

        LogLineFormatter.FormatPreamble(message).Should().Contain($"[{expectedPadded}]");
    }
}
