using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

/// <summary>
/// Cross-language conformance check (issue #628) between this file's <see cref="LspInspectorLogger"/>
/// and the VS Code extension's independent reimplementation of the same
/// <see href="https://lampepfl.github.io/lsp-viewer/">lsp-viewer</see> wire format,
/// <c>src/VSCode/src/lsp/lspInspectorLogger.ts</c>'s <c>parseLspTraceMessage</c>.
/// </summary>
/// <remarks>
/// <para>
/// The two implementations consume different <i>inputs</i> by necessity — this class intercepts
/// already-structured JSON-RPC objects off VS's own duplex pipe, while the VS Code side only ever
/// sees vscode-languageclient's human-readable <c>TraceFormat.Text</c> summary lines and has to
/// reconstruct the envelope from them — so a single fixture can't feed both directly. Instead, the
/// fixture cases below are duplicated by hand into
/// <c>src/VSCode/src/test/lsp/lspInspectorFormatConformance.test.ts</c>, one input per language for
/// the same logical JSON-RPC event, asserting both produce the same <c>type</c> and <c>message</c>.
/// <b>Keep the two files' cases in sync</b> if either implementation's output shape changes.
/// </para>
/// <para>
/// Deliberately excluded from the comparison: <c>timestamp</c> (wall-clock, inherently different
/// per run) and the "extended, ignored by the tool" <c>latencyMs</c>/<c>traceId</c> fields — which
/// this test run incidentally discovered the VS Code side never emits at all (no <c>latencyMs</c>/
/// <c>traceId</c> in its <c>LspEntry</c> interface), an asymmetry between the two implementations'
/// own bonus diagnostic value that doesn't violate the external lsp-viewer contract (which ignores
/// both fields) and is intentionally left alone here rather than expanded into a feature addition.
/// </para>
/// </remarks>
public class LspInspectorLoggerFormatConformanceTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"lsp-inspector-conformance-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        try { File.Delete(_tempPath); } catch { /* best-effort cleanup */ }
    }

    private LspInspectorLogger CreateSut() => new(_tempPath, NullLogger<LspInspectorLogger>.Instance);

    private static JObject ExpectedMessage(string json) => JObject.Parse(json);

    [Fact]
    public void Send_request_matches_the_shared_fixture()
    {
        var sut = CreateSut();
        var body = JObject.Parse("""{"jsonrpc":"2.0","id":5,"method":"textDocument/completion","params":{"foo":"bar"}}""");
        var msg = new LspMessage(LspMessageDirection.Send, body, DateTimeOffset.UtcNow);

        var (type, message) = ParseEntry(sut.FormatEntry(msg));

        type.Should().Be("send-request");
        JToken.DeepEquals(message, ExpectedMessage(
            """{"jsonrpc":"2.0","method":"textDocument/completion","id":5,"params":{"foo":"bar"}}"""))
            .Should().BeTrue($"message was: {message}");
    }

    [Fact]
    public void Receive_response_with_result_matches_the_shared_fixture()
    {
        var sut = CreateSut();
        var body = JObject.Parse("""{"jsonrpc":"2.0","id":5,"result":{"items":[]}}""");
        var msg = new LspMessage(LspMessageDirection.Receive, body, DateTimeOffset.UtcNow);

        var (type, message) = ParseEntry(sut.FormatEntry(msg));

        type.Should().Be("receive-response");
        JToken.DeepEquals(message, ExpectedMessage("""{"jsonrpc":"2.0","id":5,"result":{"items":[]}}"""))
            .Should().BeTrue($"message was: {message}");
    }

    [Fact]
    public void Send_notification_matches_the_shared_fixture()
    {
        var sut = CreateSut();
        var body = JObject.Parse("""{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"uri":"file:///a.feature"}}""");
        var msg = new LspMessage(LspMessageDirection.Send, body, DateTimeOffset.UtcNow);

        var (type, message) = ParseEntry(sut.FormatEntry(msg));

        type.Should().Be("send-notification");
        JToken.DeepEquals(message, ExpectedMessage(
            """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"uri":"file:///a.feature"}}"""))
            .Should().BeTrue($"message was: {message}");
    }

    /// <summary>Strips the <c>[LSP   - HH:mm:ss] </c> prefix and parses the remaining JSON, returning its <c>type</c> and <c>message</c> fields.</summary>
    private static (string type, JObject message) ParseEntry(string formattedEntry)
    {
        var jsonStart = formattedEntry.IndexOf('{');
        var json = JObject.Parse(formattedEntry.Substring(jsonStart).TrimEnd());
        return (json["type"]!.Value<string>()!, (JObject)json["message"]!);
    }
}
