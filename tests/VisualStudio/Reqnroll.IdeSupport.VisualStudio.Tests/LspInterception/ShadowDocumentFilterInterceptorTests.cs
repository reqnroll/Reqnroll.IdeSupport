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
/// under a <c>CopilotBaseline</c> folder inside the system temp directory (issue #562). Both
/// conditions — under temp, and the specific <c>CopilotBaseline</c> folder name — are required,
/// so a user's own file under temp, or a "copilot"-named path outside temp, is left alone.
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
    public async Task Passes_through_a_copilot_named_path_outside_the_system_temp_directory()
    {
        var path = @"C:\w\.copilot\scratch\~languagesupport.feature";

        var result = await Create().InterceptAsync(
            Send(TextDocumentMessage("textDocument/didOpen", ToFileUri(path))), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
    }

    [Fact]
    public async Task Passes_through_the_users_own_file_under_temp_with_no_copilot_baseline_folder()
    {
        var path = Path.Combine(Path.GetTempPath(), "MyScratch", "Calculator.feature");

        var result = await Create().InterceptAsync(
            Send(TextDocumentMessage("textDocument/didOpen", ToFileUri(path))), CancellationToken.None);

        result.Should().Be(LspInterceptorResult.PassThrough);
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

    [Fact]
    public void IsShadowDocumentPath_requires_both_temp_and_the_exact_copilotbaseline_segment()
    {
        var underTempWithBaseline    = Path.Combine(Path.GetTempPath(), "CopilotBaseline", "x", "a.feature");
        var underTempWithoutBaseline = Path.Combine(Path.GetTempPath(), "MyScratch", "a.feature");
        var copilotNamedOutsideTemp  = @"C:\w\.copilot\a.feature";
        var looseCopilotWordUnderTemp = Path.Combine(Path.GetTempPath(), "my-copilot-notes", "a.feature");
        var ordinaryWorkspaceFile    = @"C:\w\Calculator.feature";

        ShadowDocumentFilterInterceptor.IsShadowDocumentPath(underTempWithBaseline).Should().BeTrue();
        ShadowDocumentFilterInterceptor.IsShadowDocumentPath(underTempWithoutBaseline).Should().BeFalse();
        ShadowDocumentFilterInterceptor.IsShadowDocumentPath(copilotNamedOutsideTemp).Should().BeFalse();
        ShadowDocumentFilterInterceptor.IsShadowDocumentPath(looseCopilotWordUnderTemp).Should().BeFalse();
        ShadowDocumentFilterInterceptor.IsShadowDocumentPath(ordinaryWorkspaceFile).Should().BeFalse();
    }
}
