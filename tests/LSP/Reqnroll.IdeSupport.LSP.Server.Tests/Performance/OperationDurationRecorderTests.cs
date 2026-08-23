using System.Diagnostics;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Tracing;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

public class OperationDurationRecorderTests
{
    private sealed class CapturingLogger : IIdeSupportLogger
    {
        public TraceLevel Level => TraceLevel.Verbose;
        public List<string> Messages { get; } = new();
        public void Log(LogMessage message) => Messages.Add(message.Message);
    }

    private sealed class FixedSampler : IPerfTelemetrySampler
    {
        private readonly bool _sample;
        public FixedSampler(bool sample) => _sample = sample;
        public bool ShouldSample() => _sample;
    }

    private static ClientIdeContext Ide(string? ide = "visualstudio") => new(ide);

    [Fact]
    public void Record_writes_a_PERF_log_line_with_operation_and_duration()
    {
        var logger = new CapturingLogger();
        var sut = new OperationDurationRecorder(logger, Ide(), telemetry: null, sampler: new FixedSampler(false));

        sut.Record("textDocument/completion#step", 42.5);

        var line = logger.Messages.Should().ContainSingle().Subject;
        line.Should().StartWith("PERF ");
        line.Should().Contain("op=textDocument/completion#step");
        line.Should().Contain("ms=42.5");
    }

    [Fact]
    public void Record_includes_uri_in_log_line_when_provided()
    {
        var logger = new CapturingLogger();
        var sut = new OperationDurationRecorder(logger, Ide(), telemetry: null, sampler: new FixedSampler(false));

        var uri = DocumentUri.FromFileSystemPath(@"C:\ws\Sample.feature");
        sut.Record("textDocument/definition", 10, uri);

        logger.Messages.Single().Should().Contain("uri=").And.Contain("Sample.feature");
    }

    [Fact]
    public void Record_emits_PerfSample_telemetry_when_sampler_says_yes()
    {
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new OperationDurationRecorder(
            new CapturingLogger(), Ide("vscode"), telemetry, new FixedSampler(true));

        sut.Record("textDocument/definition", 123.4,
            DocumentUri.FromFileSystemPath(@"C:\ws\Secret.feature"));

        telemetry.Received(1).SendEvent(
            OperationDurationRecorder.PerfSampleEventName,
            Arg.Is<Dictionary<string, object?>>(d =>
                (string)d["Operation"]! == "textDocument/definition" &&
                (long)d["DurationMs"]! == 123L &&
                (string)d["DurationBucket"]! == "<=250" &&
                (string)d["IDEClient"]! == "vscode"));
    }

    [Fact]
    public void Record_telemetry_payload_never_contains_the_uri_or_path()
    {
        var telemetry = Substitute.For<ILspTelemetryService>();
        Dictionary<string, object?>? captured = null;
        telemetry
            .When(t => t.SendEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>()))
            .Do(ci => captured = ci.Arg<Dictionary<string, object?>>());

        var sut = new OperationDurationRecorder(
            new CapturingLogger(), Ide(), telemetry, new FixedSampler(true));

        sut.Record("textDocument/completion#step", 5,
            DocumentUri.FromFileSystemPath(@"C:\Users\someone\Secret.feature"));

        captured.Should().NotBeNull();
        captured!.Values
            .OfType<string>()
            .Should().NotContain(v => v.Contains("Secret") || v.Contains("someone") || v.Contains(":\\"));
    }

    [Fact]
    public void Record_does_not_emit_telemetry_when_sampler_says_no()
    {
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new OperationDurationRecorder(
            new CapturingLogger(), Ide(), telemetry, new FixedSampler(false));

        sut.Record("textDocument/definition", 10);

        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }

    [Fact]
    public void Measure_records_on_dispose()
    {
        var logger = new CapturingLogger();
        var sut = new OperationDurationRecorder(logger, Ide(), telemetry: null, sampler: new FixedSampler(false));

        using (sut.Measure("textDocument/semanticTokens/full"))
        {
            // no work
        }

        logger.Messages.Should().ContainSingle()
            .Which.Should().Contain("op=textDocument/semanticTokens/full");
    }

    [Fact]
    public void Record_mirrors_the_measurement_as_a_trace_notification()
    {
        var trace = Substitute.For<ITraceService>();
        var sut = new OperationDurationRecorder(
            new CapturingLogger(), Ide(), telemetry: null, sampler: new FixedSampler(false), trace: trace);

        sut.Record("textDocument/completion#step", 42.5);

        trace.Received(1).Trace("textDocument/completion#step: 42.5ms", Arg.Any<Func<string>?>());
    }

    [Fact]
    public void Record_does_not_pass_a_verbose_callback_when_no_uri_is_given()
    {
        var trace = Substitute.For<ITraceService>();
        var sut = new OperationDurationRecorder(
            new CapturingLogger(), Ide(), telemetry: null, sampler: new FixedSampler(false), trace: trace);

        sut.Record("textDocument/definition", 10);

        trace.Received(1).Trace(Arg.Any<string>(), null);
    }

    [Fact]
    public void Record_works_without_a_trace_service()
    {
        var sut = new OperationDurationRecorder(
            new CapturingLogger(), Ide(), telemetry: null, sampler: new FixedSampler(false));

        var act = () => sut.Record("textDocument/definition", 10);

        act.Should().NotThrow();
    }

    [Fact]
    public void Record_includes_detail_in_log_line_when_provided()
    {
        var logger = new CapturingLogger();
        var sut = new OperationDurationRecorder(logger, Ide(), telemetry: null, sampler: new FixedSampler(false));

        sut.Record("internal/bindingRegistryReconcile", 10, detail: "scannedFiles=3 reparsedFiles=2");

        logger.Messages.Single().Should().Contain("scannedFiles=3 reparsedFiles=2");
    }

    [Fact]
    public void Record_includes_the_current_managed_thread_id_in_every_log_line()
    {
        var logger = new CapturingLogger();
        var sut = new OperationDurationRecorder(logger, Ide(), telemetry: null, sampler: new FixedSampler(false));

        sut.Record("textDocument/foldingRange", 1);

        logger.Messages.Single().Should().Contain($"thread={Environment.CurrentManagedThreadId}");
    }

    [Fact]
    public void Measure_carries_detail_through_to_the_recorded_line_on_dispose()
    {
        var logger = new CapturingLogger();
        var sut = new OperationDurationRecorder(logger, Ide(), telemetry: null, sampler: new FixedSampler(false));

        using (sut.Measure("textDocument/codeLens", detail: "cacheDocs=50 cacheSteps=1350"))
        {
            // no work
        }

        logger.Messages.Single().Should().Contain("cacheDocs=50 cacheSteps=1350");
    }

    [Theory]
    [InlineData(5, "<=10")]
    [InlineData(40, "<=50")]
    [InlineData(99, "<=100")]
    [InlineData(400, "<=500")]
    [InlineData(9000, ">5000")]
    public void Bucket_maps_durations_to_coarse_bands(double ms, string expected)
        => OperationDurationRecorder.Bucket(ms).Should().Be(expected);
}
