using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="ServerLogMessageInterceptor"/> writes the server's <c>reqnroll/logMessage</c> to the
/// "Reqnroll" output pane logger and lets every message, including that one, pass through.
/// </summary>
public class ServerLogMessageInterceptorTests
{
    private sealed class RecordingLogger : IIdeSupportLogger
    {
        public List<LogMessage> Messages { get; } = new();
        public TraceLevel Level => TraceLevel.Verbose;
        public void Log(LogMessage message) => Messages.Add(message);
    }

    private static LspMessage Receive(JObject body) => new(LspMessageDirection.Receive, body, DateTimeOffset.Now);
    private static LspMessage Send(JObject body) => new(LspMessageDirection.Send, body, DateTimeOffset.Now);

    private static JObject Notification(string method, int type, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = new JObject { ["type"] = type, ["message"] = message },
    };

    [Fact]
    public async Task Writes_a_server_warning_to_the_output_pane_and_passes_it_through()
    {
        var pane = new RecordingLogger();
        var sut = new ServerLogMessageInterceptor(pane);

        var result = await sut.InterceptAsync(
            Receive(Notification("reqnroll/logMessage", 2, "[Sample] Binding discovery failed")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
        pane.Messages.Should().ContainSingle()
            .Which.Should().Match<LogMessage>(m =>
                m.Level == TraceLevel.Warning && m.Message == "[Sample] Binding discovery failed");
    }

    [Theory]
    [InlineData(1, TraceLevel.Error)]
    [InlineData(2, TraceLevel.Warning)]
    [InlineData(3, TraceLevel.Info)]
    [InlineData(4, TraceLevel.Verbose)]
    public void Maps_the_LSP_message_type_onto_a_trace_level(int messageType, TraceLevel expected)
    {
        ServerLogMessageInterceptor.ToTraceLevel(messageType).Should().Be(expected);
    }

    [Fact]
    public async Task Ignores_the_standard_window_logMessage()
    {
        var pane = new RecordingLogger();
        var sut = new ServerLogMessageInterceptor(pane);

        await sut.InterceptAsync(Receive(Notification("window/logMessage", 2, "library noise")), CancellationToken.None);

        pane.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Ignores_messages_sent_to_the_server()
    {
        var pane = new RecordingLogger();
        var sut = new ServerLogMessageInterceptor(pane);

        await sut.InterceptAsync(Send(Notification("reqnroll/logMessage", 2, "not from the server")), CancellationToken.None);

        pane.Messages.Should().BeEmpty();
    }
}
