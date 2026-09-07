using ReqnrollConnector.Logging;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.Logging;

public class CompositeLoggerTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<Log> Messages { get; } = new();
        public void Log(Log log) => Messages.Add(log);
    }

    [Fact]
    public void Log_forwards_to_every_composed_logger()
    {
        var first = new CapturingLogger();
        var second = new CapturingLogger();
        var sut = new CompositeLogger(first, second);
        var entry = new Log(LogLevel.Info, "hello");

        sut.Log(entry);

        first.Messages.Should().ContainSingle().Which.Should().Be(entry);
        second.Messages.Should().ContainSingle().Which.Should().Be(entry);
    }

    [Fact]
    public void Log_with_no_composed_loggers_does_not_throw()
    {
        var sut = new CompositeLogger();

        var act = () => sut.Log(new Log(LogLevel.Info, "hello"));

        act.Should().NotThrow();
    }
}
