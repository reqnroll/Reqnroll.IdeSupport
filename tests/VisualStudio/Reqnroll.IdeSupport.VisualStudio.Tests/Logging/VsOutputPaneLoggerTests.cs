using System;
using System.Diagnostics;
using AwesomeAssertions;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Extension.Logging;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.Logging;

/// <summary>
/// Coverage for issue #651's "Reqnroll" VS Output Window pane sink. Only the sink's pure
/// formatting/threshold decisions are covered here - actual pane creation/writes are UI-thread-
/// affinitized VS SDK glue with no host available in this test process (same "extract pure logic,
/// leave glue untested" split the rest of the VS-extension test suite follows).
/// </summary>
public class VsOutputPaneLoggerTests
{
    private sealed class UnavailableServiceProvider : IServiceProvider
    {
        public int GetServiceCallCount { get; private set; }

        public object? GetService(Type serviceType)
        {
            GetServiceCallCount++;
            return null;
        }
    }

    [Fact]
    public void FormatLine_includes_level_origin_and_message()
    {
        var message = new LogMessage(TraceLevel.Warning, "something went wrong", "DoWork", Source: "MyClass");

        var line = VsOutputPaneLogger.FormatLine(message);

        line.Should().Contain("[Warning")
            .And.Contain("MyClass.DoWork")
            .And.Contain("something went wrong");
    }

    [Fact]
    public void FormatLine_appends_exception_details_when_present()
    {
        var exception = new InvalidOperationException("boom");
        var message = new LogMessage(TraceLevel.Error, "failed", "DoWork", exception, "MyClass");

        var line = VsOutputPaneLogger.FormatLine(message);

        line.Should().Contain("boom");
    }

    [Fact]
    public void FormatLine_omits_exception_section_when_absent()
    {
        var message = new LogMessage(TraceLevel.Info, "all good", "DoWork", Source: "MyClass");

        var line = VsOutputPaneLogger.FormatLine(message);

        line.Should().NotContain(Environment.NewLine);
    }

    [Theory]
    [InlineData(TraceLevel.Error, true)]
    [InlineData(TraceLevel.Warning, true)]
    [InlineData(TraceLevel.Info, false)]
    [InlineData(TraceLevel.Verbose, false)]
    public void ShouldActivate_matches_legacy_warning_or_worse_threshold(TraceLevel level, bool expected)
    {
        VsOutputPaneLogger.ShouldActivate(level).Should().Be(expected);
    }

    [Fact]
    public void Log_below_configured_level_does_not_touch_the_service_provider()
    {
        var serviceProvider = new UnavailableServiceProvider();
        var sut = new VsOutputPaneLogger(TraceLevel.Warning, serviceProvider);

        sut.Log(new LogMessage(TraceLevel.Verbose, "too noisy", "DoWork", Source: "MyClass"));

        serviceProvider.GetServiceCallCount.Should().Be(0);
    }
}
