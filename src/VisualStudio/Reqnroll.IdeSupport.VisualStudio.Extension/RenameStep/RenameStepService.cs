#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;

/// <summary>
/// Sends custom <c>reqnroll/renameTargets</c> and <c>reqnroll/selectRenameTarget</c>
/// requests over the <c>LspInterceptingPipe</c> for the Step Rename refactoring feature.
/// </summary>
internal sealed class RenameStepService
{
    private readonly LspInterception.LspInterceptingPipe _pipe;
    private readonly ILogger<RenameStepService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public RenameStepService(LspInterception.LspInterceptingPipe pipe, ILogger<RenameStepService> logger)
    {
        _pipe   = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Queries the server for renameable binding targets at the given position.
    /// Returns a <see cref="RenameTargetsResult"/> with the available targets,
    /// or null if no renameable binding was found at the cursor position.
    /// </summary>
    /// <exception cref="LspRequestFailedException">The request failed (server error, timeout, no server).</exception>
    /// <exception cref="Reqnroll.IdeSupport.VisualStudio.Utilities.LspContentModifiedException">The server answered ContentModified.</exception>
    public async Task<RenameTargetsResult?> GetRenameTargetsAsync(
        string fileUri, int line0, int char0,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildPositionParams(fileUri, line0, char0);

        _logger.LogDebug(
            "RenameStepService: querying {RenameTargetsMethod} at {FileUri}:{Line0}:{Char0}", CustomLspMethodNames.ReqnrollRenameTargets, fileUri, line0, char0);

        var outcome = await _pipe
            .SendRequestAsync(CustomLspMethodNames.ReqnrollRenameTargets, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        // A failed request throws rather than reading as "no renameable binding here".
        var result = outcome.GetResultOrThrow(CustomLspMethodNames.ReqnrollRenameTargets, fileUri, cancellationToken);

        try
        {
            var mapped = MapTargets(result);
            _logger.LogDebug(
                "RenameStepService: {TargetCount} target(s) returned", mapped?.Targets.Count ?? 0);
            return mapped;
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "RenameStepService: failed to parse targets.");
            return null;
        }
    }

    /// <summary>
    /// Pure mapping from a raw <c>reqnroll/renameTargets</c> JSON result to a
    /// <see cref="RenameTargetsResult"/>. Separated from transport so it can be unit-tested.
    /// Returns <c>null</c> when the result is not an object or carries no targets.
    /// </summary>
    internal static RenameTargetsResult? MapTargets(JToken? result)
    {
        if (result is not JObject obj)
            return null;

        var targets = obj["targets"] as JArray;
        if (targets is null || targets.Count == 0)
            return null;

        var response = new RenameTargetsResult();
        foreach (var t in targets)
        {
            if (t is not JObject item) continue;
            response.Targets.Add(new RenameTargetItem
            {
                Label = item["label"]?.Value<string>() ?? "",
                Expression = item["expression"]?.Value<string>() ?? "",
                AttributeIndex = item["attributeIndex"]?.Value<int>() ?? 0
            });
        }

        return response.Targets.Count > 0 ? response : null;
    }

    /// <summary>
    /// Tells the server to remember the selected attribute index for the next rename.
    /// </summary>
    public async Task SelectRenameTargetAsync(
        string fileUri, int version, int attributeIndex, int line, int character,
        CancellationToken cancellationToken)
    {
        // `position` lets the server resolve `attributeIndex` to the binding it
        // denotes while that candidate list is still current, instead of carrying a bare index
        // across the rename prompt and re-applying it to a list rebuilt later.
        var paramsJson =
            $"{{\"uri\":{JsonEscape(fileUri)},\"version\":{version},\"attributeIndex\":{attributeIndex}," +
            $"\"position\":{{\"line\":{line},\"character\":{character}}}}}";
        _logger.LogDebug(
            "RenameStepService: sending {SelectRenameTargetMethod} for attrIndex={AttributeIndex}", CustomLspMethodNames.ReqnrollSelectRenameTarget, attributeIndex);

        await _pipe
            .SendNotificationToServerAsync(CustomLspMethodNames.ReqnrollSelectRenameTarget, paramsJson, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the standard <c>textDocument/rename</c> request. Returning normally means the server
    /// accepted the rename; the edit itself arrives separately, pushed by the server via
    /// <c>workspace/applyEdit</c> a moment after this response. The response
    /// body is deliberately empty for Visual Studio — VS's native rename client applies whatever
    /// WorkspaceEdit a rename response carries, and with the push in play that applied the edit
    /// twice — so there is nothing in it to parse.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled before the server answered.</exception>
    /// <exception cref="EditRejectedException">
    /// The server rejected the rename with a user-correctable reason — e.g. a
    /// step-rename validation rule failed. Carries the server's human-readable reason as
    /// <see cref="Exception.Message"/>, to be shown to the user verbatim.
    /// </exception>
    /// <exception cref="RenameFailedException">
    /// The server failed the rename for any other reason, or gave no answer; the
    /// message is generic or describes why no answer arrived.
    /// </exception>
    public async Task SendRenameRequestAsync(
        string fileUri, int line0, int char0, string newName,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildRenameParams(fileUri, line0, char0, newName);
        _logger.LogDebug(
            "RenameStepService: sending {RenameMethod} at {FileUri}:{Line0}:{Char0}",
            LspStandardMethodNames.TextDocumentRename, fileUri, line0, char0);

        var outcome = await _pipe
            .SendRequestAsync(LspStandardMethodNames.TextDocumentRename, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        switch (outcome.Status)
        {
            case LspRequestStatus.Succeeded:
                break;

            case LspRequestStatus.ServerError:
                // A user-correctable rejection (-32803) carries a message meant to be shown verbatim; any
                // other error is an unexpected failure — logged, and reported only generically.
                if (EditRejection.TryCreateException(outcome.Error!) is { } rejection)
                    throw rejection;

                _logger.LogWarning(
                    "RenameStepService: {RenameMethod} failed on the server: {Error}",
                    LspStandardMethodNames.TextDocumentRename, outcome.Error!.ToString(Newtonsoft.Json.Formatting.None));
                throw new RenameFailedException("Rename failed.");

            case LspRequestStatus.Cancelled:
                throw new OperationCanceledException(cancellationToken);

            default:
                // No answer at all (timeout, no server) is not a successful rename. The
                // edit may still have been applied if the server was merely slow, so don't claim it wasn't.
                _logger.LogWarning(
                    "RenameStepService: {RenameMethod} got no answer: {Status}",
                    LspStandardMethodNames.TextDocumentRename, outcome.Status);
                throw new RenameFailedException($"Rename did not complete - {outcome.DescribeFailure()}.");
        }

        _logger.LogDebug("RenameStepService: {RenameMethod} accepted by server", LspStandardMethodNames.TextDocumentRename);
    }

    private static string BuildPositionParams(string fileUri, int line0, int char0)
    {
        var escapedUri = JsonEscape(fileUri);
        return $"{{\"textDocument\":{{\"uri\":{escapedUri}}},\"position\":{{\"line\":{line0},\"character\":{char0}}}}}";
    }

    private static string BuildRenameParams(string fileUri, int line0, int char0, string newName)
    {
        var escapedUri = JsonEscape(fileUri);
        var escapedNewName = JsonEscape(newName);
        return $"{{\"textDocument\":{{\"uri\":{escapedUri}}},\"position\":{{\"line\":{line0},\"character\":{char0}}},\"newName\":{escapedNewName}}}";
    }

    internal static string JsonEscape(string value) =>
        Newtonsoft.Json.JsonConvert.ToString(value);
}

/// <summary>
/// Thrown by <see cref="RenameStepService.SendRenameRequestAsync"/> when the server fails a
/// <c>textDocument/rename</c> request for a reason that is <i>not</i> a user-correctable rejection
/// (see <see cref="EditRejectedException"/> for that). The message is generic.
/// </summary>
internal sealed class RenameFailedException : Exception
{
    /// <summary>Creates the exception with the server's error message.</summary>
    public RenameFailedException(string message) : base(message) { }
}
