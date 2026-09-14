using System;
using System.Diagnostics;
using System.Reflection;
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
    public void FormatLine_appends_a_one_line_exception_summary_without_a_stack_trace()
    {
        var exception = new InvalidOperationException("boom");
        var message = new LogMessage(TraceLevel.Error, "failed", "DoWork", exception, "MyClass");

        var line = VsOutputPaneLogger.FormatLine(message);

        line.Should().Contain("InvalidOperationException").And.Contain("boom")
            .And.NotContain(Environment.NewLine)
            .And.NotContain("at ");
    }

    [Fact]
    public void FormatLine_omits_exception_section_when_absent()
    {
        var message = new LogMessage(TraceLevel.Info, "all good", "DoWork", Source: "MyClass");

        var line = VsOutputPaneLogger.FormatLine(message);

        line.Should().NotContain(Environment.NewLine);
    }

    [Fact]
    public void FormatLine_unwraps_a_single_inner_aggregate_exception()
    {
        var exception = new AggregateException(new InvalidOperationException("boom"));
        var message = new LogMessage(TraceLevel.Error, "failed", "DoWork", exception, "MyClass");

        var line = VsOutputPaneLogger.FormatLine(message);

        line.Should().Contain("InvalidOperationException").And.Contain("boom")
            .And.NotContain("AggregateException");
    }

    [Fact]
    public void FormatLine_keeps_a_multi_inner_aggregate_exception_as_is()
    {
        var exception = new AggregateException(new InvalidOperationException("boom"), new ArgumentException("nope"));
        var message = new LogMessage(TraceLevel.Error, "failed", "DoWork", exception, "MyClass");

        var line = VsOutputPaneLogger.FormatLine(message);

        line.Should().Contain("AggregateException");
    }

    [Fact]
    public void FormatLine_unwraps_target_invocation_exception()
    {
        var exception = new TargetInvocationException(new InvalidOperationException("boom"));
        var message = new LogMessage(TraceLevel.Error, "failed", "DoWork", exception, "MyClass");

        var line = VsOutputPaneLogger.FormatLine(message);

        line.Should().Contain("InvalidOperationException").And.Contain("boom")
            .And.NotContain("TargetInvocationException");
    }

    [Fact]
    public void FormatLine_appends_the_log_pointer_when_provided()
    {
        var message = new LogMessage(TraceLevel.Error, "failed", "DoWork", new InvalidOperationException("boom"), "MyClass");

        var line = VsOutputPaneLogger.FormatLine(message, @"C:\Users\me\AppData\Local\Reqnroll");

        line.Should().Contain("Details are in").And.Contain(@"C:\Users\me\AppData\Local\Reqnroll");
    }

    [Fact]
    public void FormatLine_omits_the_log_pointer_when_not_provided()
    {
        var message = new LogMessage(TraceLevel.Error, "failed", "DoWork", new InvalidOperationException("boom"), "MyClass");

        var line = VsOutputPaneLogger.FormatLine(message);

        line.Should().NotContain("Details are in");
    }

    [Fact]
    public void ConsumeLogPointerIfNeeded_returns_the_directory_only_for_the_first_exception_carrying_message()
    {
        var sut = new VsOutputPaneLogger(TraceLevel.Warning, new UnavailableServiceProvider());

        var first = sut.ConsumeLogPointerIfNeeded(messageHasException: true);
        var second = sut.ConsumeLogPointerIfNeeded(messageHasException: true);

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public void ConsumeLogPointerIfNeeded_returns_null_when_the_message_has_no_exception()
    {
        var sut = new VsOutputPaneLogger(TraceLevel.Warning, new UnavailableServiceProvider());

        sut.ConsumeLogPointerIfNeeded(messageHasException: false).Should().BeNull();
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
