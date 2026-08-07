#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.CommentToggle;

/// <summary>
/// Sends a <c>workspace/executeCommand</c> request for <c>reqnroll.toggleComment</c>
/// to the LSP server (Comment/Uncomment toggle).
/// </summary>
/// <remarks>
/// The server responds with an acknowledgement and as a side-effect sends a
/// <c>workspace/applyEdit</c> notification back to the client, which VS's built-in LSP
/// infrastructure handles natively to apply the text edits.
/// </remarks>
internal sealed class CommentToggleService
{
    private const string ExecuteCommandMethod = "workspace/executeCommand";

    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<CommentToggleService> _logger;

    /// <summary>Creates the service over the LSP connection pipe for sending <c>workspace/executeCommand</c> requests.</summary>
    public CommentToggleService(LspInterceptingPipe pipe, ILogger<CommentToggleService> logger)
    {
        _pipe   = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Sends a <c>workspace/executeCommand</c> request for <c>reqnroll.toggleComment</c>
    /// to toggle <c>#</c> comments on the selected lines (0-based, inclusive).
    /// </summary>
    public async Task ToggleCommentAsync(
        string            fileUri,
        int               startLine,
        int               endLine,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildParams(fileUri, startLine, endLine);

        _logger.LogInformation(
            "CommentToggleService: sending workspace/executeCommand reqnroll.toggleComment uri={FileUri} lines[{StartLine}..{EndLine}]",
            fileUri, startLine, endLine);
        _logger.LogInformation(
            "CommentToggleService: sending reqnroll.toggleComment params={ParamsJson}", paramsJson);

        var result = await _pipe
            .SendRequestToServerAsync(ExecuteCommandMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "CommentToggleService: server response = {Result}", result is null ? "<null>" : result.ToString());

        _logger.LogInformation("CommentToggleService: server acknowledged reqnroll.toggleComment");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri, int startLine, int endLine) =>
        new LspParamsBuilder()
            .AddString("command", "reqnroll.toggleComment")
            .AddRaw("arguments", $"[{LspParamsBuilder.EscapeString(fileUri)},{startLine},{endLine}]")
            .Build();
}
