using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

public class ShutdownHandshakeInterceptorTests
{
    private static ShutdownHandshakeInterceptor Create() =>
        new(NullLogger<ShutdownHandshakeInterceptor>.Instance);

    private static LspMessage Send(JObject body)    => new(LspMessageDirection.Send,    body, DateTimeOffset.Now);
    private static LspMessage Receive(JObject body) => new(LspMessageDirection.Receive, body, DateTimeOffset.Now);

    private static JObject ShutdownRequest(object id) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = JToken.FromObject(id),
        ["method"]  = "shutdown",
    };

    private static JObject Response(object id, bool withResult = true) => withResult
        ? new JObject { ["jsonrpc"] = "2.0", ["id"] = JToken.FromObject(id), ["result"] = null }
        : new JObject { ["jsonrpc"] = "2.0", ["id"] = JToken.FromObject(id), ["error"] = new JObject { ["code"] = -32603 } };

    [Fact]
    public async Task Not_observed_before_any_shutdown_request_is_seen()
    {
        var sut = Create();

        var result = await sut.InterceptAsync(Receive(Response(1)), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        sut.ShutdownObserved.Should().BeFalse();
    }

    [Fact]
    public async Task Observed_once_the_matching_response_to_a_shutdown_request_is_seen()
    {
        var sut = Create();

        await sut.InterceptAsync(Send(ShutdownRequest(2)), CancellationToken.None);
        sut.ShutdownObserved.Should().BeFalse("only the request has been seen so far");

        var result = await sut.InterceptAsync(Receive(Response(2)), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough, "the interceptor only observes, never consumes");
        sut.ShutdownObserved.Should().BeTrue();
    }

    [Fact]
    public async Task Not_observed_when_the_response_id_does_not_match_the_shutdown_request()
    {
        var sut = Create();

        await sut.InterceptAsync(Send(ShutdownRequest(3)), CancellationToken.None);
        await sut.InterceptAsync(Receive(Response(999)), CancellationToken.None);

        sut.ShutdownObserved.Should().BeFalse();
    }

    [Fact]
    public async Task Not_observed_for_an_unrelated_response_with_no_prior_shutdown_request()
    {
        var sut = Create();

        var result = await sut.InterceptAsync(
            Receive(new JObject { ["jsonrpc"] = "2.0", ["id"] = 4, ["result"] = true }), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        sut.ShutdownObserved.Should().BeFalse();
    }

    [Fact]
    public async Task Not_observed_for_a_notification_or_a_request_with_a_different_method()
    {
        var sut = Create();

        await sut.InterceptAsync(
            Send(new JObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/didClose", ["id"] = 5 }),
            CancellationToken.None);
        await sut.InterceptAsync(Receive(Response(5)), CancellationToken.None);

        sut.ShutdownObserved.Should().BeFalse();
    }
}
