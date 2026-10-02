using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.LSP.Core.TestOutcomes;
using Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;
using Reqnroll.IdeSupport.LSP.Server.Performance;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestOutcomes;

/// <summary>
/// <see cref="RegisterTestRunHandler"/> — the server-side run registration for
/// <c>reqnroll/testOutcomes/registerRun</c>, replacing what used to be a same-process call to a
/// VS-only <c>TestOutcomeListener</c> (LSP-server outcome pipeline refactor).
/// </summary>
public class RegisterTestRunHandlerTests : IDisposable
{
    private readonly TestOutcomeStore _store = new();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly TestOutcomeTcpListener _listener;
    private readonly RegisterTestRunHandler _handler;

    public RegisterTestRunHandlerTests()
    {
        _listener = new TestOutcomeTcpListener(_store, _logger, () => { });
        _handler = new RegisterTestRunHandler(_listener, _logger);
    }

    public void Dispose() => _listener.Dispose();

    private async Task<IReadOnlyDictionary<string, long>> RegisterAndDrain(params string?[] runModes)
    {
        var counters = new FeatureUsageCounters();
        var handler = new RegisterTestRunHandler(_listener, _logger, usage: counters);
        foreach (var mode in runModes)
            await handler.HandleAsync(new RegisterTestRunParams { RunMode = mode }, CancellationToken.None);
        return counters.Drain();
    }

    [Fact]
    public async Task A_Run_registration_counts_one_TestRun_Run_per_call()
        => (await RegisterAndDrain(TestRunModes.Run, TestRunModes.Run))
            .Should().BeEquivalentTo(new Dictionary<string, long> { ["TestRun.Run"] = 2 });

    [Fact]
    public async Task A_Debug_registration_counts_one_TestRun_Debug()
        => (await RegisterAndDrain(TestRunModes.Debug))
            .Should().BeEquivalentTo(new Dictionary<string, long> { ["TestRun.Debug"] = 1 });

    [Fact]
    public async Task An_Unknown_registration_counts_one_TestRun_Unknown()
        => (await RegisterAndDrain(TestRunModes.Unknown))
            .Should().BeEquivalentTo(new Dictionary<string, long> { ["TestRun.Unknown"] = 1 });

    [Fact]
    public async Task A_registration_without_a_run_mode_is_not_counted_because_it_is_not_a_user_run()
        => (await RegisterAndDrain((string?)null)).Should().BeEmpty("VS Code registers once at activation, not per run");

    [Fact]
    public async Task An_unrecognised_run_mode_is_never_turned_into_a_counter_key()
        => (await RegisterAndDrain("C:/Users/someone/secret", "run", "")).Should().BeEmpty();

    [Fact]
    public async Task A_run_is_counted_even_when_the_listener_cannot_start()
    {
        _listener.Dispose();

        (await RegisterAndDrain(TestRunModes.Run)).Should().ContainKey("TestRun.Run");
    }

    [Fact]
    public void RunMode_deserialises_from_the_camelCase_wire_property()
    {
        var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<RegisterTestRunParams>("""{"runMode":"Debug"}""");
        var empty = Newtonsoft.Json.JsonConvert.DeserializeObject<RegisterTestRunParams>("{}");

        parsed!.RunMode.Should().Be("Debug");
        empty!.RunMode.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_returns_a_fresh_run_id_per_call_on_the_same_endpoint()
    {
        var first = await _handler.HandleAsync(new RegisterTestRunParams(), CancellationToken.None);
        var second = await _handler.HandleAsync(new RegisterTestRunParams(), CancellationToken.None);

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        first.Endpoint.Should().Be(second.Endpoint, "one listener per server instance");
        first.RunId.Should().NotBe(second.RunId);
    }

    [Fact]
    public async Task HandleAsync_reports_failure_once_the_listener_is_disposed()
    {
        _listener.Dispose();

        var response = await _handler.HandleAsync(new RegisterTestRunParams(), CancellationToken.None);

        response.Success.Should().BeFalse();
        response.RunId.Should().BeNull();
        response.Endpoint.Should().BeNull();
    }
}
