using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Reqnroll.IdeSupport.Common.Tests.Logging;

public class IdeSupportLoggerAdapterTests
{
    private sealed class CapturingLogger : IIdeSupportLogger
    {
        public TraceLevel Level => TraceLevel.Verbose;
        public LogMessage? LastMessage { get; private set; }
        public void Log(LogMessage message) => LastMessage = message;
    }

    [Fact]
    public void Log_shortens_a_namespace_qualified_category_to_its_simple_name_as_Source()
    {
        var logger = new CapturingLogger();
        var sut = new IdeSupportLoggerAdapter("Reqnroll.IdeSupport.LSP.Server.Foo", logger);

        sut.Log(LogLevel.Information, default, "state", null, (s, e) => "hello");

        logger.LastMessage!.Source.Should().Be("Foo");
    }

    [Fact]
    public void Log_uses_the_category_as_is_when_it_has_no_namespace()
    {
        var logger = new CapturingLogger();
        var sut = new IdeSupportLoggerAdapter("Foo", logger);

        sut.Log(LogLevel.Information, default, "state", null, (s, e) => "hello");

        logger.LastMessage!.Source.Should().Be("Foo");
    }

    [Fact]
    public void Log_leaves_CallerMethod_empty_since_ILogger_has_no_method_equivalent()
    {
        var logger = new CapturingLogger();
        var sut = new IdeSupportLoggerAdapter("Reqnroll.IdeSupport.LSP.Server.Foo", logger);

        sut.Log(LogLevel.Information, default, "state", null, (s, e) => "hello");

        logger.LastMessage!.CallerMethod.Should().BeEmpty();
        LogLineFormatter.FormatOrigin(logger.LastMessage!).Should().Be("Foo",
            "with no caller method, the formatted origin should be the source alone, not 'Foo.'");
    }

    [Fact]
    public void Log_maps_the_LogLevel_and_message_and_exception_through()
    {
        var logger = new CapturingLogger();
        var sut = new IdeSupportLoggerAdapter("Foo", logger);
        var ex = new InvalidOperationException("bad");

        sut.Log(LogLevel.Warning, default, "state", ex, (s, e) => $"formatted: {s}");

        logger.LastMessage!.Level.Should().Be(TraceLevel.Warning);
        logger.LastMessage!.Message.Should().Be("formatted: state");
        logger.LastMessage!.Exception.Should().BeSameAs(ex);
    }
}
