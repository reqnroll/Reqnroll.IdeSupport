using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

public class ReqnrollProjectDetectedInterceptorTests
{
    private readonly ReqnrollProjectTelemetryGate _gate = new();

    private static LspMessage Message(LspMessageDirection direction, string method) => new(
        direction, new JObject { ["jsonrpc"] = "2.0", ["method"] = method }, DateTimeOffset.Now);

    [Fact]
    public async Task Opens_the_telemetry_gate_on_the_server_notification_and_passes_it_through()
    {
        var sut = new ReqnrollProjectDetectedInterceptor(_gate);

        var result = await sut.InterceptAsync(
            Message(LspMessageDirection.Receive, "reqnroll/reqnrollProjectDetected"), CancellationToken.None);

        _gate.IsOpen.Should().BeTrue();
        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Theory]
    [InlineData(true, "telemetry/event")]
    [InlineData(false, "reqnroll/reqnrollProjectDetected")]
    public async Task Leaves_the_gate_closed_for_any_other_message(bool received, string method)
    {
        var sut = new ReqnrollProjectDetectedInterceptor(_gate);
        var direction = received ? LspMessageDirection.Receive : LspMessageDirection.Send;

        await sut.InterceptAsync(Message(direction, method), CancellationToken.None);

        _gate.IsOpen.Should().BeFalse();
    }
}
