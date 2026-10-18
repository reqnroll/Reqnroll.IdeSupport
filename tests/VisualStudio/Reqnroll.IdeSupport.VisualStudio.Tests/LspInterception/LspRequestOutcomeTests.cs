using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Utilities;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

/// <summary>
/// Issues #1017/#1019/#1020: <see cref="LspInterceptingPipe.SendRequestAsync"/> must say <em>how</em> a
/// request ended, so a failure can never be mistaken for a server that answered "nothing" (JSON null,
/// <c>[]</c>, <c>{}</c>). Drives a real pipe over a scripted in-memory server.
/// </summary>
public class LspRequestOutcomeTests
{
    private const string Method = "reqnroll/somethingCustom";
    private const string Uri = "file:///C:/w/Sample.feature";
    private const int ContentModified = -32801;

    [Fact]
    public async Task A_JSON_null_answer_is_a_success_not_a_failure()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("null");

        var outcome = await server.Pipe.SendRequestAsync(Method, "{}", CancellationToken.None);

        outcome.Status.Should().Be(LspRequestStatus.Succeeded);
        outcome.Result.Should().NotBeNull();
        outcome.Result!.Type.Should().Be(JTokenType.Null);
        outcome.ResultOrNull.Should().BeNull();
    }

    [Fact]
    public async Task A_response_without_a_result_member_is_still_an_answer()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithRaw("\"unrelated\":true");

        var outcome = await server.Pipe.SendRequestAsync(Method, "{}", CancellationToken.None);

        outcome.Status.Should().Be(LspRequestStatus.Succeeded, "a null Result must only ever mean released/cancelled");
    }

    [Fact]
    public async Task An_empty_array_answer_is_a_success_with_the_array()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("[]");

        var outcome = await server.Pipe.SendRequestAsync(Method, "{}", CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        outcome.ResultOrNull.Should().BeOfType<JArray>();
    }

    [Fact]
    public async Task A_server_error_carries_the_error_object()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(-32603, "handler blew up");

        var outcome = await server.Pipe.SendRequestAsync(Method, "{}", CancellationToken.None);

        outcome.Status.Should().Be(LspRequestStatus.ServerError);
        outcome.Result.Should().BeNull();
        outcome.Error!["message"]!.Value<string>().Should().Be("handler blew up");
        outcome.IsContentModified.Should().BeFalse();
        outcome.DescribeFailure().Should().Contain("handler blew up");
    }

    [Fact]
    public async Task ContentModified_is_recognised()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(ContentModified, "content modified");

        var outcome = await server.Pipe.SendRequestAsync(Method, "{}", CancellationToken.None);

        outcome.IsContentModified.Should().BeTrue();
        outcome.CreateException(Method, Uri).Should().BeOfType<LspContentModifiedException>();
    }

    [Fact]
    public async Task No_response_within_the_timeout_is_TimedOut()
    {
        await using var server = await ScriptedServerPipe.StartAsync(ownedRequestTimeout: TimeSpan.FromMilliseconds(200));
        server.NeverReply();

        var outcome = await server.Pipe.SendRequestAsync(Method, "{}", CancellationToken.None);

        outcome.Status.Should().Be(LspRequestStatus.TimedOut);
        outcome.Succeeded.Should().BeFalse();
        outcome.CreateException(Method, Uri).Should().BeOfType<LspRequestFailedException>().Which.Status.Should().Be(LspRequestStatus.TimedOut);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_Cancelled_not_TimedOut()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.NeverReply();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var outcome = await server.Pipe.SendRequestAsync(Method, "{}", cts.Token);

        outcome.Status.Should().Be(LspRequestStatus.Cancelled);
    }

    [Fact]
    public async Task A_request_to_a_terminated_server_is_Unavailable_immediately()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.Pipe.MarkServerTerminated("test");

        var outcome = await server.Pipe.SendRequestAsync(Method, "{}", CancellationToken.None);

        outcome.Status.Should().Be(LspRequestStatus.Unavailable);
    }

    [Fact]
    public async Task A_request_in_flight_when_the_server_terminates_is_Unavailable()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.NeverReply();

        var pending = server.Pipe.SendRequestAsync(Method, "{}", CancellationToken.None);
        await WaitForAsync(() => server.RequestCount == 1);
        server.Pipe.MarkServerTerminated("test");

        (await pending).Status.Should().Be(LspRequestStatus.Unavailable);
    }

    [Fact]
    public async Task A_request_after_dispose_is_Unavailable()
    {
        var server = await ScriptedServerPipe.StartAsync();
        await server.DisposeAsync();

        var outcome = await server.Pipe.SendRequestAsync(Method, "{}", CancellationToken.None);

        outcome.Status.Should().Be(LspRequestStatus.Unavailable);
    }

    [Fact]
    public async Task A_request_in_flight_when_the_pipe_is_disposed_is_Unavailable()
    {
        var server = await ScriptedServerPipe.StartAsync();
        server.NeverReply();

        var pending = server.Pipe.SendRequestAsync(Method, "{}", CancellationToken.None);
        await WaitForAsync(() => server.RequestCount == 1);
        await server.DisposeAsync();

        (await pending).Status.Should().Be(LspRequestStatus.Unavailable);
    }

    [Fact]
    public void GetResultOrThrow_returns_a_JSON_null_result_without_throwing()
    {
        var outcome = LspRequestOutcome.Success(JValue.CreateNull());

        outcome.GetResultOrThrow(Method, Uri).Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void GetResultOrThrow_maps_each_failure_to_its_exception()
    {
        var contentModified = LspRequestOutcome.ServerError(JObject.Parse("{\"code\":-32801,\"message\":\"m\"}"));
        var otherError = LspRequestOutcome.ServerError(JObject.Parse("{\"code\":-32603,\"message\":\"boom\"}"));

        Assert.Throws<LspContentModifiedException>(() => contentModified.GetResultOrThrow(Method, Uri));
        Assert.Throws<LspRequestFailedException>(() => otherError.GetResultOrThrow(Method, Uri)).Message.Should().Contain("boom");
        Assert.Throws<LspRequestFailedException>(() => LspRequestOutcome.TimedOut().GetResultOrThrow(Method, Uri));
        Assert.Throws<LspRequestFailedException>(() => LspRequestOutcome.Unavailable().GetResultOrThrow(Method, Uri));
        Assert.Throws<OperationCanceledException>(() => LspRequestOutcome.Cancelled().GetResultOrThrow(Method, Uri));
    }

    [Fact]
    public void LspRequestFailure_describes_owned_request_failures_but_not_other_exceptions()
    {
        LspRequestFailure.TryGetReason(LspRequestOutcome.TimedOut().CreateException(Method, Uri), out var timedOut).Should().BeTrue();
        timedOut.Should().Be("the server did not respond in time");

        LspRequestFailure.TryGetReason(new LspContentModifiedException(Method, Uri), out var modified).Should().BeTrue();
        modified.Should().Contain("document changed");

        var error = LspRequestOutcome.ServerError(JObject.Parse("{\"code\":-32603,\"message\":\"boom\"}")).CreateException(Method, Uri);
        LspRequestFailure.TryGetReason(error, out var server).Should().BeTrue();
        server.Should().Contain("boom");

        LspRequestFailure.TryGetReason(new OperationCanceledException(), out _).Should().BeFalse("caller cancellation is not a failure to report");
        LspRequestFailure.TryGetReason(new InvalidOperationException(), out _).Should().BeFalse();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }
}
