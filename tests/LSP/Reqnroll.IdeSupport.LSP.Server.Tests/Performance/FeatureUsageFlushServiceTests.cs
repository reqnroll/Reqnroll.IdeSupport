using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

/// <summary>Tests for <see cref="FeatureUsageFlushService"/> (issue #582).</summary>
public class FeatureUsageFlushServiceTests
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private static ClientIdeContext Ide(string? ide = "visualstudio") => new(ide);

    [Fact]
    public async Task FlushFinalAsync_emits_no_event_when_the_drain_is_empty()
    {
        var counters = new FeatureUsageCounters(); // nothing incremented
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, Ide(), _logger, telemetry);

        await sut.FlushFinalAsync();

        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }

    [Fact]
    public async Task FlushFinalAsync_emits_the_drained_counts_marked_IsFinal()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("textDocument/definition");
        counters.Increment("textDocument/definition");
        counters.Increment("textDocument/rename");
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, Ide("vscode"), _logger, telemetry);

        await sut.FlushFinalAsync();

        telemetry.Received(1).SendEvent(
            FeatureUsageFlushService.FeatureUsageSummaryEventName,
            Arg.Is<Dictionary<string, object?>>(p =>
                true.Equals(p["IsFinal"]) &&
                "vscode".Equals(p["IDEClient"]) &&
                ((IReadOnlyDictionary<string, long>)p["Counts"]!)["textDocument/definition"] == 2 &&
                ((IReadOnlyDictionary<string, long>)p["Counts"]!)["textDocument/rename"] == 1));
    }

    [Fact]
    public async Task FlushFinalAsync_leaves_the_counters_empty_afterward()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("textDocument/definition");
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, Ide(), _logger, telemetry);

        await sut.FlushFinalAsync();

        counters.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_returns_immediately_and_sends_nothing_when_no_interval_is_configured()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("textDocument/definition");
        var telemetry = Substitute.For<ILspTelemetryService>();
        // interval: null forces the "disabled" path deterministically, rather than depending on
        // the ambient environment variable being unset in the test-running environment.
        var sut = new FeatureUsageFlushService(counters, Ide(), _logger, telemetry, interval: null);

        var runTask = sut.RunAsync(CancellationToken.None);
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));

        completed.Should().BeSameAs(runTask, "a disabled flush service must return immediately, not loop forever");
        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }

    [Fact]
    public async Task RunAsync_flushes_periodically_at_the_configured_interval()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("textDocument/definition");
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(
            counters, Ide(), _logger, telemetry, interval: TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();
        var runTask = sut.RunAsync(cts.Token);

        // Poll rather than a fixed sleep: the first tick should land well within a couple of
        // hundred milliseconds at a 20ms interval, but avoid a flaky single-shot timing assumption.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (telemetry.ReceivedCalls().Count() == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }

        telemetry.Received().SendEvent(
            FeatureUsageFlushService.FeatureUsageSummaryEventName,
            Arg.Is<Dictionary<string, object?>>(p => false.Equals(p["IsFinal"])));
    }

    [Fact]
    public async Task RunAsync_does_not_emit_on_an_idle_tick()
    {
        var counters = new FeatureUsageCounters(); // nothing incremented
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(
            counters, Ide(), _logger, telemetry, interval: TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();
        var runTask = sut.RunAsync(cts.Token);

        await Task.Delay(150);
        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }

        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }
}
