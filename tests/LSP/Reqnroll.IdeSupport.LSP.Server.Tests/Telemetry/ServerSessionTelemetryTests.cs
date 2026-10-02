#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry;

public class ServerSessionTelemetryTests
{
    private readonly ILspTelemetryService _telemetry = Substitute.For<ILspTelemetryService>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private ServerSessionTelemetry CreateSut(ClientIdeContext? ide = null, TimeSpan? uptime = null) =>
        new(_telemetry, ide ?? new ClientIdeContext("vscode"), _logger, () => uptime ?? TimeSpan.FromMilliseconds(1234));

    [Fact]
    public void ReportStarted_sends_the_environment_and_startup_time()
    {
        var ide = new ClientIdeContext("rider");
        ide.ApplyClientInfo(new ClientInfo { Name = "Rider", Version = "2025.1" });

        CreateSut(ide).ReportStarted();

        _telemetry.Received(1).SendEvent(
            TelemetryEvents.ServerSessionStarted,
            Arg.Is<Dictionary<string, object?>>(d =>
                "2025.1".Equals(d["ClientVersion"]) &&
                new[] { "Windows", "macOS", "Linux", "Other" }.Contains((string)d["OperatingSystem"]!) &&
                !string.IsNullOrEmpty((string)d["Architecture"]!) &&
                !string.IsNullOrEmpty((string)d["Runtime"]!) &&
                1234L.Equals(d["StartupMs"])));
    }

    [Fact]
    public void ReportStarted_omits_ClientVersion_when_the_client_sent_none_and_sends_no_other_keys()
    {
        CreateSut().ReportStarted();

        _telemetry.Received(1).SendEvent(
            TelemetryEvents.ServerSessionStarted,
            Arg.Is<Dictionary<string, object?>>(d =>
                !d.ContainsKey("ClientVersion") &&
                d.Keys.All(k => new[] { "OperatingSystem", "Architecture", "Runtime", "StartupMs" }.Contains(k))));
    }

    [Fact]
    public void ReportStarted_sends_only_once()
    {
        var sut = CreateSut();

        sut.ReportStarted();
        sut.ReportStarted();

        _telemetry.Received(1).SendEvent(TelemetryEvents.ServerSessionStarted, Arg.Any<Dictionary<string, object?>>());
    }

    [Fact]
    public void ReportEnded_sends_the_session_length_once()
    {
        var sut = CreateSut();

        sut.ReportEnded();
        sut.ReportEnded();

        _telemetry.Received(1).SendEvent(
            TelemetryEvents.ServerSessionEnded,
            Arg.Is<Dictionary<string, object?>>(d => d.Count == 1 && d["SessionSeconds"] is double));
    }

    [Fact]
    public void A_failing_telemetry_sink_never_propagates()
    {
        _telemetry.When(t => t.SendEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>()))
            .Do(_ => throw new InvalidOperationException("transport closed"));
        var sut = CreateSut();

        var act = () => { sut.ReportStarted(); sut.ReportEnded(); };

        act.Should().NotThrow();
    }

    [Fact]
    public void A_null_sink_is_a_no_op()
    {
        var sut = new ServerSessionTelemetry(null, new ClientIdeContext("vscode"), _logger, () => TimeSpan.Zero);

        var act = () => { sut.ReportStarted(); sut.ReportEnded(); };

        act.Should().NotThrow();
    }

    [Fact]
    public void EndOnShutdown_sends_ServerSessionEnded_when_the_shutdown_request_arrives()
    {
        var shutdown = new TestSubject();
        using var subscription = CreateSut().EndOnShutdown(shutdown);

        _telemetry.DidNotReceive().SendEvent(TelemetryEvents.ServerSessionEnded, Arg.Any<Dictionary<string, object?>>());
        shutdown.Raise();

        _telemetry.Received(1).SendEvent(TelemetryEvents.ServerSessionEnded, Arg.Any<Dictionary<string, object?>>());
    }

    [Fact]
    public void The_final_FeatureUsageSummary_flush_precedes_ServerSessionEnded_when_subscribed_in_that_order()
    {
        // Mirrors Program.Main's subscription order on the single shutdown observable.
        var order = new List<string>();
        _telemetry.When(t => t.SendEvent(TelemetryEvents.ServerSessionEnded, Arg.Any<Dictionary<string, object?>>()))
            .Do(_ => order.Add("ended"));
        var flush = Substitute.For<IFeatureUsageFlushService>();
        flush.FlushFinalAsync().Returns(_ =>
        {
            order.Add("flush");
            return Task.CompletedTask;
        });
        var shutdown = new TestSubject();

        using var a = FeatureUsageFlushService.FlushOnShutdown(flush, shutdown);
        using var b = CreateSut().EndOnShutdown(shutdown);
        shutdown.Raise();

        order.Should().Equal("flush", "ended");
    }

    private sealed class TestSubject : IObservable<bool>
    {
        private readonly List<IObserver<bool>> _observers = new();

        public IDisposable Subscribe(IObserver<bool> observer)
        {
            _observers.Add(observer);
            return new Unsubscriber(() => _observers.Remove(observer));
        }

        public void Raise()
        {
            foreach (var observer in _observers.ToArray())
                observer.OnNext(true);
        }

        private sealed class Unsubscriber(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }
}
