using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.TestOutcomes;
using Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

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
