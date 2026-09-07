#nullable enable

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Logging;

public class ProtocolLoggerProviderTests
{
    private sealed class CapturingLogger : IIdeSupportLogger
    {
        public TraceLevel Level => TraceLevel.Verbose;
        public List<LogMessage> Messages { get; } = new();
        public void Log(LogMessage message) => Messages.Add(message);
    }

    [Fact]
    public void CreateLogger_returns_a_logger_that_forwards_to_the_underlying_IIdeSupportLogger()
    {
        var captured = new CapturingLogger();
        var provider = new ProtocolLoggerProvider(captured);

        var logger = provider.CreateLogger("OmniSharp.Extensions.LanguageServer.Server.LanguageServer");
        logger.Log(LogLevel.Information, new EventId(0), "state", null, (s, _) => "handling request");

        var message = captured.Messages.Should().ContainSingle().Subject;
        message.Message.Should().Be("handling request");
        message.Level.Should().Be(TraceLevel.Info);
        // Shortened to the simple type name and carried as Source, not CallerMethod (issue #626)
        // — ILogger has no method-name equivalent to put there.
        message.Source.Should().Be("LanguageServer");
        message.CallerMethod.Should().BeEmpty();
        message.Exception.Should().BeNull();
    }

    [Fact]
    public void CreateLogger_forwards_the_exception()
    {
        var captured = new CapturingLogger();
        var provider = new ProtocolLoggerProvider(captured);
        var ex = new InvalidOperationException("boom");

        var logger = provider.CreateLogger("Category");
        logger.Log(LogLevel.Error, new EventId(0), "state", ex, (s, e) => "failed");

        captured.Messages.Should().ContainSingle().Which.Exception.Should().BeSameAs(ex);
    }

    [Fact]
    public void IsEnabled_reflects_the_underlying_IIdeSupportLogger_level()
    {
        var adapter = new IdeSupportLoggerAdapter("cat", new CapturingLogger());

        // CapturingLogger.Level is Verbose, so every LogLevel maps to something at or below it.
        adapter.IsEnabled(LogLevel.Trace).Should().BeTrue();
        adapter.IsEnabled(LogLevel.Critical).Should().BeTrue();
    }

    [Fact]
    public void BeginScope_returns_a_disposable_that_does_not_throw()
    {
        var adapter = new IdeSupportLoggerAdapter("cat", new CapturingLogger());

        var scope = adapter.BeginScope("state");

        scope.Should().NotBeNull();
        var act = () => scope.Dispose();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(LogLevel.Trace, TraceLevel.Verbose)]
    [InlineData(LogLevel.Debug, TraceLevel.Verbose)]
    [InlineData(LogLevel.Information, TraceLevel.Info)]
    [InlineData(LogLevel.Warning, TraceLevel.Warning)]
    [InlineData(LogLevel.Error, TraceLevel.Error)]
    [InlineData(LogLevel.Critical, TraceLevel.Error)]
    [InlineData(LogLevel.None, TraceLevel.Off)]
    public void ToTraceLevel_maps_each_LogLevel(LogLevel logLevel, TraceLevel expected)
    {
        IdeSupportLogLevelConverter.ToTraceLevel(logLevel).Should().Be(expected);
    }
}
