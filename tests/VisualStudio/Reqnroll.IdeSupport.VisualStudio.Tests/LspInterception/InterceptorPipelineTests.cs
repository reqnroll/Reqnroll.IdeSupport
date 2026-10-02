using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// The interceptor list's own rules (issue #587, step 3). Before the extraction these were only
/// reachable by running a pump; the fault-tolerance rule in particular is what keeps a buggy
/// interceptor from severing a live VS ↔ server connection.
/// </summary>
public class InterceptorPipelineTests
{
    private sealed class RecordingInterceptor : ILspMessageInterceptor
    {
        private readonly LspInterceptorResult _result;

        public RecordingInterceptor(LspInterceptorResult result = LspInterceptorResult.PassThrough) => _result = result;

        public int Calls { get; private set; }

        public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingInterceptor : ILspMessageInterceptor
    {
        public int Calls { get; private set; }

        public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("interceptor blew up");
        }
    }

    private static LspMessage AnyMessage() => new(
        LspMessageDirection.Receive,
        JObject.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"window/logMessage\"}"),
        DateTimeOffset.Now);

    private static InterceptorPipeline Pipeline(params ILspMessageInterceptor[] interceptors) =>
        new(interceptors, NullLogger.Instance);

    [Fact]
    public async Task Every_interceptor_sees_a_message_nobody_consumes()
    {
        var first  = new RecordingInterceptor();
        var second = new RecordingInterceptor();

        var result = await Pipeline(first, second).RunAsync(AnyMessage(), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        first.Calls.Should().Be(1);
        second.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Consumption_stops_the_list_and_tells_the_caller_not_to_forward()
    {
        var consuming = new RecordingInterceptor(LspInterceptorResult.Consume);
        var later     = new RecordingInterceptor();

        var result = await Pipeline(consuming, later).RunAsync(AnyMessage(), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.Consume);
        later.Calls.Should().Be(0, "nothing after a consuming interceptor should run");
    }

    [Fact]
    public async Task An_interceptor_that_throws_is_skipped_and_the_rest_still_run()
    {
        // The rule that keeps a buggy interceptor from severing a live connection: a fault degrades
        // to "this interceptor did not see this message", never to a broken pipe.
        var throwing  = new ThrowingInterceptor();
        var following = new RecordingInterceptor();

        var result = await Pipeline(throwing, following).RunAsync(AnyMessage(), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough, "the message must still be forwarded");
        throwing.Calls.Should().Be(1);
        following.Calls.Should().Be(1);
    }

    [Fact]
    public async Task An_empty_list_passes_everything_through()
    {
        var result = await Pipeline(Array.Empty<ILspMessageInterceptor>())
            .RunAsync(AnyMessage(), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }
}
