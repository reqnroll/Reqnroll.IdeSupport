using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.Common.Tests.Logging;

public class IdeSupportLoggerExtensionsTests
{
    private sealed class CapturingLogger : IIdeSupportLogger
    {
        public TraceLevel Level => TraceLevel.Verbose;
        public LogMessage? LastMessage { get; private set; }
        public void Log(LogMessage message) => LastMessage = message;
    }

    // Every entry point below is called directly from this file, so Source should always resolve
    // to this test file's own name — proving the [CallerFilePath] capture reaches the real call
    // site rather than IdeSupportLoggerExtensions.cs's own file (issue #626).
    private const string ThisFile = nameof(IdeSupportLoggerExtensionsTests);

    [Fact]
    public void LogError_captures_the_calling_files_name_as_Source()
    {
        var logger = new CapturingLogger();

        logger.LogError("boom");

        logger.LastMessage!.Source.Should().Be(ThisFile);
        logger.LastMessage!.CallerMethod.Should().Be(nameof(LogError_captures_the_calling_files_name_as_Source));
    }

    [Fact]
    public void LogWarning_captures_the_calling_files_name_as_Source()
    {
        var logger = new CapturingLogger();

        logger.LogWarning("careful");

        logger.LastMessage!.Source.Should().Be(ThisFile);
    }

    [Fact]
    public void LogInfo_captures_the_calling_files_name_as_Source()
    {
        var logger = new CapturingLogger();

        logger.LogInfo("fyi");

        logger.LastMessage!.Source.Should().Be(ThisFile);
    }

    [Fact]
    public void LogVerbose_string_overload_captures_the_calling_files_name_as_Source()
    {
        var logger = new CapturingLogger();

        logger.LogVerbose("detail");

        logger.LastMessage!.Source.Should().Be(ThisFile);
    }

    [Fact]
    public void LogVerbose_lazy_overload_captures_the_calling_files_name_as_Source()
    {
        var logger = new CapturingLogger();

        logger.LogVerbose(() => "detail");

        logger.LastMessage!.Source.Should().Be(ThisFile);
    }

    [Fact]
    public void LogException_captures_the_calling_files_name_as_Source_and_keeps_the_exception()
    {
        var logger = new CapturingLogger();
        var ex = new InvalidOperationException("bad");

        logger.LogException(ex);

        logger.LastMessage!.Source.Should().Be(ThisFile);
        logger.LastMessage!.Exception.Should().BeSameAs(ex);
    }

    [Fact]
    public void LogException_with_telemetry_reports_and_captures_Source()
    {
        var logger = new CapturingLogger();
        var telemetry = Substitute.For<IErrorTelemetryService>();
        var ex = new InvalidOperationException("bad");

        logger.LogException(telemetry, ex);

        telemetry.Received(1).MonitorError(ex);
        logger.LastMessage!.Source.Should().Be(ThisFile);
    }

    [Fact]
    public void Trace_string_overload_captures_the_calling_files_name_as_Source()
    {
        var logger = new CapturingLogger();

        logger.Trace("checkpoint");

        logger.LastMessage!.Source.Should().Be(ThisFile);
        logger.LastMessage!.Message.Should().Contain("checkpoint").And.Contain("line");
    }

    [Fact]
    public void Trace_stopwatch_overload_captures_the_calling_files_name_as_Source_when_it_logs()
    {
        var logger = new CapturingLogger();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Thread.Sleep(15); // past Trace(Stopwatch)'s 10ms logging threshold

        logger.Trace(sw, "slow bit");

        logger.LastMessage!.Source.Should().Be(ThisFile);
    }
}
