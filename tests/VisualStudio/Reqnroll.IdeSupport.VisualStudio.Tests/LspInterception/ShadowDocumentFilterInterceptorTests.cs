using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="ShadowDocumentFilterInterceptor"/> drops didOpen/didChange/didClose for documents
/// under the system temp directory or a copilot-named path (issue #562).
/// </summary>
public class ShadowDocumentFilterInterceptorTests
{
    private static ShadowDocumentFilterInterceptor Create() =>
        new(NullLogger<ShadowDocumentFilterInterceptor>.Instance);

    private static LspMessage Send(JObject body) => new(LspMessageDirection.Send, body, DateTimeOffset.Now);

    private static JObject TextDocumentMessage(string method, string uri) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"]  = method,
        ["params"]  = new JObject { ["textDocument"] = new JObject { ["uri"] = uri } },
    };

    private static string ToFileUri(string localPath) => new Uri(localPath).AbsoluteUri;

    [Theory]
    [InlineData("textDocument/didOpen")]
    [InlineData("textDocument/didChange")]
    [InlineData("textDocument/didClose")]
    public async Task Drops_text_sync_notifications_under_the_copilot_baseline_temp_path(string method)
    {
        var shadowPath = Path.Combine(Path.GetTempPath(), "CopilotBaseline", "56588fc1", "~languagesupport.feature");

        var result = await Create().InterceptAsync(
            Send(TextDocumentMessage(method, ToFileUri(shadowPath))), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.Consume);
    }

    [Fact]
    public async Task Drops_a_copilot_named_path_outside_the_system_temp_directory()
    {
        var shadowPath = @"C:\w\.copilot\scratch\~languagesupport.feature";

        var result = await Create().InterceptAsync(
            Send(TextDocumentMessage("textDocument/didOpen", ToFileUri(shadowPath))), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.Consume);
    }

    [Fact]
    public async Task Passes_through_a_real_workspace_feature_file()
    {
        var result = await Create().InterceptAsync(
            Send(TextDocumentMessage("textDocument/didOpen", "file:///c:/w/Calculator.feature")),
            CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task Passes_through_an_unrelated_method()
    {
        var result = await Create().InterceptAsync(
            Send(new JObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/foldingRange" }),
            CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp\CopilotBaseline\x\a.feature", true)]
    [InlineData(@"C:\w\.copilot\a.feature", true)]
    [InlineData(@"C:\w\Calculator.feature", false)]
    public void IsShadowDocumentPath_matches_temp_and_copilot_paths(string path, bool expected)
    {
        ShadowDocumentFilterInterceptor.IsShadowDocumentPath(path).Should().Be(expected);
    }
}
