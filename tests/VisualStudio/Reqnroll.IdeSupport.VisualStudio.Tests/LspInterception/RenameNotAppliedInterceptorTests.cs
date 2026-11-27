using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks -- no JoinableTaskFactory/UI thread in these tests

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="RenameNotAppliedInterceptor"/> shows the server's <c>reqnroll/renameNotApplied</c>
/// message to the user, off the receive pump, and lets every message pass through.
/// </summary>
public class RenameNotAppliedInterceptorTests
{
    private readonly List<string> _shown = new();

    private RenameNotAppliedInterceptor CreateSut(Func<string, CancellationToken, Task>? show = null) =>
        new(NullLogger<RenameNotAppliedInterceptor>.Instance,
            show ?? ((message, _) => { _shown.Add(message); return Task.CompletedTask; }));

    private static LspMessage Receive(JObject body) => new(LspMessageDirection.Receive, body, DateTimeOffset.Now);
    private static LspMessage Send(JObject body) => new(LspMessageDirection.Send, body, DateTimeOffset.Now);

    private static JObject Notification(string method, string? message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = new JObject { ["uri"] = "file:///c:/work/a.feature", ["message"] = message },
    };

    [Fact]
    public async Task Shows_the_servers_message_and_passes_it_through()
    {
        var sut = CreateSut();

        var result = await sut.InterceptAsync(
            Receive(Notification("reqnroll/renameNotApplied", "The step was not renamed: read-only")), CancellationToken.None);
        await sut.LastReport;

        result.Should().Be(LspInterceptorResult.PassThrough);
        _shown.Should().Equal("The step was not renamed: read-only");
    }

    [Fact]
    public async Task Does_not_wait_for_the_dialog_to_close()
    {
        var dialogClosed = new TaskCompletionSource<bool>();
        var sut = CreateSut((_, _) => dialogClosed.Task);

        var intercept = sut.InterceptAsync(
            Receive(Notification("reqnroll/renameNotApplied", "The step was not renamed")), CancellationToken.None);

        intercept.IsCompleted.Should().BeTrue("a modal dialog must not hold up the messages behind this one");
        (await intercept).Should().Be(LspInterceptorResult.PassThrough);
        dialogClosed.SetResult(true);
        await sut.LastReport;
    }

    [Fact]
    public async Task A_failure_to_show_the_dialog_does_not_escape()
    {
        var sut = CreateSut((_, _) => throw new InvalidOperationException("no shell"));

        await sut.InterceptAsync(
            Receive(Notification("reqnroll/renameNotApplied", "The step was not renamed")), CancellationToken.None);

        var report = () => sut.LastReport;
        await report.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Shows_nothing_for_a_notification_without_a_message(string? message)
    {
        var sut = CreateSut();

        var result = await sut.InterceptAsync(Receive(Notification("reqnroll/renameNotApplied", message)), CancellationToken.None);
        await sut.LastReport;

        result.Should().Be(LspInterceptorResult.PassThrough);
        _shown.Should().BeEmpty();
    }

    [Fact]
    public async Task Ignores_other_methods_and_outgoing_messages()
    {
        var sut = CreateSut();

        await sut.InterceptAsync(Receive(Notification("reqnroll/logMessage", "x")), CancellationToken.None);
        await sut.InterceptAsync(Send(Notification("reqnroll/renameNotApplied", "x")), CancellationToken.None);
        await sut.LastReport;

        _shown.Should().BeEmpty();
    }
}
