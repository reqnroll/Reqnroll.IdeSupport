using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="ScaffoldTrackingInterceptor"/> records scaffolded <c>.cs</c> files from a code-action
/// response and injects a project-membership delta when their <c>didOpen</c> is later sent. The
/// injection target (<c>VsProjectEventMonitor</c>) is VS-bound, so here we verify the observable
/// contract that does not require it: every message passes through, and the
/// track-then-didOpen path is safe when the monitor is not yet available (returns null).
/// </summary>
public class ScaffoldTrackingInterceptorTests
{
    private static ScaffoldTrackingInterceptor Create() =>
        new(getMonitor: () => null, logger: NullLogger<ScaffoldTrackingInterceptor>.Instance);

    private static LspMessage Receive(JObject body) => new(LspMessageDirection.Receive, body, DateTimeOffset.Now);
    private static LspMessage Send(JObject body)    => new(LspMessageDirection.Send,    body, DateTimeOffset.Now);

    private static JObject CodeActionResponseCreating(string uri) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"]      = 7, // response: id present, no method
        ["result"]  = new JArray(new JObject
        {
            ["edit"] = new JObject
            {
                ["documentChanges"] = new JArray(new JObject
                {
                    ["kind"] = "create",
                    ["uri"]  = uri,
                }),
            },
        }),
    };

    private static JObject DidOpen(string uri) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"]  = "textDocument/didOpen",
        ["params"]  = new JObject { ["textDocument"] = new JObject { ["uri"] = uri } },
    };

    [Fact]
    public async Task A_code_action_response_creating_a_cs_file_passes_through()
    {
        var result = await Create().InterceptAsync(
            Receive(CodeActionResponseCreating("file:///c:/w/NewSteps.cs")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task Tracking_then_didOpen_is_safe_when_the_monitor_is_unavailable()
    {
        const string uri = "file:///c:/w/NewSteps.cs";
        var sut = Create();

        await sut.InterceptAsync(Receive(CodeActionResponseCreating(uri)), CancellationToken.None);

        // The send-side didOpen for the tracked file resolves the monitor (null here) and must not throw.
        var act = async () => await sut.InterceptAsync(Send(DidOpen(uri)), CancellationToken.None);

        (await act.Should().NotThrowAsync()).Which.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task A_didOpen_for_an_untracked_file_passes_through()
    {
        var result = await Create().InterceptAsync(
            Send(DidOpen("file:///c:/w/Unrelated.cs")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task A_create_for_a_non_cs_file_is_ignored_and_passes_through()
    {
        var result = await Create().InterceptAsync(
            Receive(CodeActionResponseCreating("file:///c:/w/notes.txt")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task An_unrelated_received_message_passes_through()
    {
        var result = await Create().InterceptAsync(
            Receive(new JObject { ["jsonrpc"] = "2.0", ["method"] = "window/logMessage" }),
            CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }
}
