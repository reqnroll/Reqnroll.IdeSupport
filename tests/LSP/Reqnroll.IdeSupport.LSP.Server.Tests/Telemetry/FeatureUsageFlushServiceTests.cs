using System.Reactive.Subjects;
using System.Text.Json;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry;

/// <summary>Tests for <see cref="FeatureUsageFlushService"/> (issue #582).</summary>
public class FeatureUsageFlushServiceTests
{
    private static readonly TimeSpan Enabled = TimeSpan.FromMinutes(10);

    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private static Dictionary<string, object?> Sent(ILspTelemetryService telemetry)
        => (Dictionary<string, object?>)telemetry.ReceivedCalls().Single().GetArguments()[1]!;

    [Fact]
    public async Task FlushFinalAsync_emits_no_event_when_the_drain_is_empty()
    {
        var counters = new FeatureUsageCounters(); // nothing incremented
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, Enabled);

        await sut.FlushFinalAsync();

        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }

    [Fact]
    public async Task FlushFinalAsync_emits_the_drained_counts_marked_IsFinal_with_sequence_and_session_seconds()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("Completion.Step");
        counters.Increment("Completion.Step");
        counters.Increment("CodeAction");
        counters.Increment("InlayHint");
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, Enabled);

        await sut.FlushFinalAsync();

        telemetry.Received(1).SendEvent(TelemetryEvents.FeatureUsageSummary, Arg.Any<Dictionary<string, object?>>());
        var sent = Sent(telemetry);
        sent[TelemetryProperties.IsFinal].Should().Be(true);
        sent[TelemetryProperties.Sequence].Should().Be(1L);
        sent.Should().ContainKey(TelemetryProperties.SessionSeconds);
        sent.Should().ContainKey(TelemetryProperties.WindowSeconds);
        sent[TelemetryProperties.LookupCounts].Should().Be("""{"CodeAction":1,"Completion.Step":2}""");
        sent[TelemetryProperties.PassiveCounts].Should().Be("""{"InlayHint":1}""");
    }

    [Fact]
    public async Task Counts_are_sent_as_plain_strings_so_every_ide_forwarder_preserves_them()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("Completion.Step");
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, Enabled);

        await sut.FlushFinalAsync();

        // VS Code String(value), Rider toString() and VS JToken.ToString() each mangle a nested
        // object (as "[object Object]", "{k=1.0}", multi-line JSON); a string passes through intact.
        Sent(telemetry)[TelemetryProperties.LookupCounts].Should().BeOfType<string>();
        Sent(telemetry).Should().NotContainKey(TelemetryProperties.PassiveCounts, "an empty kind is omitted");
    }

    [Fact]
    public async Task Sequence_increases_with_each_emitted_event_and_is_not_consumed_by_silent_windows()
    {
        var counters = new FeatureUsageCounters();
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, Enabled);

        counters.Increment("CodeLens");
        await sut.FlushFinalAsync();
        await sut.FlushFinalAsync(); // silent: nothing counted
        counters.Increment("CodeLens");
        await sut.FlushFinalAsync();

        var sequences = telemetry.ReceivedCalls()
            .Select(c => (long)((Dictionary<string, object?>)c.GetArguments()[1]!)[TelemetryProperties.Sequence]!)
            .ToList();
        sequences.Should().Equal(1L, 2L);
    }

    [Fact]
    public async Task Keys_outside_the_catalogue_never_reach_the_event()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("Completion.Step");
        counters.Increment(@"C:\Users\someone\secret.feature");
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, Enabled);

        await sut.FlushFinalAsync();

        var text = JsonSerializer.Serialize(Sent(telemetry));
        text.Should().NotContain("secret");
    }

    [Fact]
    public async Task FlushFinalAsync_sends_nothing_when_only_unknown_keys_were_counted()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("not-in-the-catalogue");
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, Enabled);

        await sut.FlushFinalAsync();

        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }

    [Fact]
    public async Task FlushFinalAsync_leaves_the_counters_empty_afterward()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("CodeAction");
        var sut = new FeatureUsageFlushService(counters, _logger, Substitute.For<ILspTelemetryService>(), Enabled);

        await sut.FlushFinalAsync();

        counters.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task FlushFinalAsync_sends_nothing_when_the_service_is_disabled()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("CodeAction");
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, interval: null);

        await sut.FlushFinalAsync();

        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }

    [Fact]
    public async Task RunAsync_returns_immediately_and_sends_nothing_when_disabled()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("CodeAction");
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, interval: null);

        var runTask = sut.RunAsync(CancellationToken.None);
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));

        completed.Should().BeSameAs(runTask, "a disabled flush service must return immediately, not loop forever");
        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }

    [Fact]
    public async Task RunAsync_flushes_periodically_at_the_configured_interval()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("CodeAction");
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();
        var runTask = sut.RunAsync(cts.Token);

        // Poll rather than a fixed sleep: the first tick should land well within a couple of
        // hundred milliseconds at a 20ms interval, but avoid a flaky single-shot timing assumption.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!telemetry.ReceivedCalls().Any() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }

        telemetry.Received().SendEvent(
            TelemetryEvents.FeatureUsageSummary,
            Arg.Is<Dictionary<string, object?>>(p => false.Equals(p[TelemetryProperties.IsFinal])));
    }

    [Fact]
    public async Task RunAsync_does_not_emit_on_an_idle_tick()
    {
        var counters = new FeatureUsageCounters(); // nothing incremented
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();
        var runTask = sut.RunAsync(cts.Token);

        await Task.Delay(150);
        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }

        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }

    [Fact]
    public void FlushOnShutdown_emits_the_final_event_when_the_shutdown_request_arrives()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("CodeLens");
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, Enabled);
        var shutdown = new Subject<bool>();
        using var subscription = FeatureUsageFlushService.FlushOnShutdown(sut, shutdown);

        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
        shutdown.OnNext(true);

        telemetry.Received(1).SendEvent(TelemetryEvents.FeatureUsageSummary, Arg.Any<Dictionary<string, object?>>());
        Sent(telemetry)[TelemetryProperties.IsFinal].Should().Be(true);
    }

    [Fact]
    public void FlushOnShutdown_swallows_a_failing_telemetry_sink()
    {
        var counters = new FeatureUsageCounters();
        counters.Increment("CodeLens");
        var telemetry = Substitute.For<ILspTelemetryService>();
        telemetry.When(t => t.SendEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>()))
            .Do(_ => throw new InvalidOperationException("boom"));
        var sut = new FeatureUsageFlushService(counters, _logger, telemetry, Enabled);
        var shutdown = new Subject<bool>();
        using var subscription = FeatureUsageFlushService.FlushOnShutdown(sut, shutdown);

        var act = () => shutdown.OnNext(true);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void ResolveInterval_disables_for_a_non_positive_seconds_value(string raw)
        => FeatureUsageFlushService.ResolveInterval(raw).Should().BeNull();

    [Fact]
    public void ResolveInterval_uses_the_positive_seconds_value_when_given()
        => FeatureUsageFlushService.ResolveInterval("30").Should().Be(TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void ResolveInterval_falls_back_to_the_default_when_unset_or_unparseable(string? raw)
    {
        FeatureUsageFlushService.EnabledByDefault.Should().BeTrue();
        FeatureUsageFlushService.ResolveInterval(raw).Should().Be(FeatureUsageFlushService.DefaultInterval);
    }
}
