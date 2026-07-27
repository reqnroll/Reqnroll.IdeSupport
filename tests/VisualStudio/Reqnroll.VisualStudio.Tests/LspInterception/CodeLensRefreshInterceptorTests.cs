using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="CodeLensRefreshInterceptor"/> only reacts to a <c>reqnroll/refreshCodeLens</c>
/// (receive); every other message, including a <c>.cs</c> <c>textDocument/didChange</c> (send) —
/// whose own invalidation is disabled, see issue #156/#318 — must pass through untouched. The
/// <c>reqnroll/refreshCodeLens</c> invalidation branch dispatches onto the VS main thread
/// (<c>ThreadHelper.JoinableTaskFactory</c>) and therefore requires a VS host — it belongs in an
/// integration smoke test, not here.
/// </summary>
public class CodeLensRefreshInterceptorTests
{
    private static CodeLensRefreshInterceptor Create() =>
        new(new StepCodeLensState(), NullLogger<CodeLensRefreshInterceptor>.Instance);

    private static LspMessage Send(JObject body)    => new(LspMessageDirection.Send,    body, DateTimeOffset.Now);
    private static LspMessage Receive(JObject body) => new(LspMessageDirection.Receive, body, DateTimeOffset.Now);

    private static JObject DidChange(string uri) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"]  = "textDocument/didChange",
        ["params"]  = new JObject { ["textDocument"] = new JObject { ["uri"] = uri } },
    };

    [Fact]
    public async Task A_message_without_a_method_passes_through()
    {
        var result = await Create().InterceptAsync(
            Receive(new JObject { ["jsonrpc"] = "2.0", ["id"] = 1 }), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task A_didChange_on_a_non_cs_file_passes_through_without_invalidating()
    {
        var result = await Create().InterceptAsync(
            Send(DidChange("file:///c:/w/A.feature")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task A_didChange_on_a_cs_file_passes_through_without_invalidating()
    {
        // Per-.cs-edit invalidation is disabled (issue #156/#318) — a .cs didChange is now just
        // another pass-through, no different from a non-.cs file, testable directly (no VS host
        // needed) since it no longer reaches any UI-thread CodeLens.Invalidate() call.
        var result = await Create().InterceptAsync(
            Send(DidChange("file:///c:/w/Steps.cs")), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task A_didChange_without_a_uri_passes_through()
    {
        var body = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"]  = "textDocument/didChange",
            ["params"]  = new JObject { ["textDocument"] = new JObject() },
        };

        var result = await Create().InterceptAsync(Send(body), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task A_non_didChange_send_passes_through()
    {
        var result = await Create().InterceptAsync(
            Send(DidChange("file:///c:/w/Steps.cs").Tap(b => b["method"] = "textDocument/didOpen")),
            CancellationToken.None);

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

    [Fact]
    public async Task A_refreshCodeLens_with_isFullReplacement_false_passes_through_without_invalidating()
    {
        // Incremental refreshes (a Roslyn patch on a .cs edit, or a .feature edit changing usage
        // counts) must not call CodeLens.Invalidate() — same reconnect-churn reasoning as the
        // disabled per-.cs-edit trigger (#156/#318). Testable directly (no VS host needed) since,
        // like the disabled .cs didChange path, it no longer reaches any UI-thread call.
        var body = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"]  = "reqnroll/refreshCodeLens",
            ["params"]  = new JObject { ["projectName"] = "Proj", ["isFullReplacement"] = false },
        };

        var result = await Create().InterceptAsync(Receive(body), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task A_refreshCodeLens_without_isFullReplacement_defaults_to_incremental_and_passes_through()
    {
        // Absence of the field (e.g. an older/mismatched payload) must default to the safe,
        // non-invalidating behavior rather than assuming a full replacement.
        var body = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"]  = "reqnroll/refreshCodeLens",
            ["params"]  = new JObject { ["projectName"] = "Proj" },
        };

        var result = await Create().InterceptAsync(Receive(body), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }
}

internal static class JObjectTestExtensions
{
    /// <summary>Mutates and returns the object, for terse inline test fixtures.</summary>
    public static JObject Tap(this JObject obj, Action<JObject> mutate)
    {
        mutate(obj);
        return obj;
    }
}
