#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Lsp;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Before Visual Studio applies a <c>workspace/applyEdit</c> the server pushed, asks Visual Studio's
/// source control integration whether the read-only files it changes may be edited. Refused, the
/// edit never reaches Visual Studio and the server is answered <c>applied: false</c> with the reason.
/// </summary>
/// <remarks>
/// <para>
/// Without this, Visual Studio asks only once it is already applying the edit, one file at a time:
/// the editor raises its "edit read-only file?" prompt from inside the buffer change. If the user
/// cancels, that file's change is dropped, but the files already changed stay changed, and Visual
/// Studio's LSP client still answers <c>applied: true</c>, because it does not check whether a
/// buffer edit was cancelled. A Step Rename then renames the <c>.feature</c> steps but not the
/// step definition, and the server, told the edit applied, commits it. Asking first, for all the
/// files at once, means a refusal changes nothing and is reported as one. It also keeps the prompt
/// out of the middle of an edit, where its message loop let other editor work fail with "Master
/// edit already in progress".
/// </para>
/// <para>
/// Only files that are read-only on disk are asked about, so an edit to writable files, the usual
/// case, goes straight through without waiting for the UI thread.
/// </para>
/// <para>
/// The question may show a dialog, so it is asked off the receive pump: the push is held back and
/// sent on afterwards, and server messages read after it go ahead of it. Nothing the server sends
/// in the meantime depends on the edit, since the server waits for this request's answer first.
/// </para>
/// </remarks>
internal sealed class SourceControlEditCheckInterceptor : ILspMessageInterceptor
{
    private readonly ILogger _logger;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<(bool Allowed, uint MoreInfo)>> _queryEdit;
    private readonly Func<JObject, CancellationToken, Task<bool>> _forwardToVs;
    private readonly Func<JToken, JObject, CancellationToken, Task<bool>> _respondToServer;
    private readonly Func<string, bool> _isReadOnly;

    /// <param name="logger">Logger.</param>
    /// <param name="queryEdit">Asks whether the files may be edited, possibly prompting the user.</param>
    /// <param name="forwardToVs">Sends the held-back request on to Visual Studio.</param>
    /// <param name="respondToServer">Answers the server's request in Visual Studio's place.</param>
    /// <param name="isReadOnly">Whether a file is read-only on disk; defaults to the file's attributes.</param>
    public SourceControlEditCheckInterceptor(
        ILogger<SourceControlEditCheckInterceptor> logger,
        Func<IReadOnlyList<string>, CancellationToken, Task<(bool Allowed, uint MoreInfo)>> queryEdit,
        Func<JObject, CancellationToken, Task<bool>> forwardToVs,
        Func<JToken, JObject, CancellationToken, Task<bool>> respondToServer,
        Func<string, bool>? isReadOnly = null)
    {
        _logger          = logger;
        _queryEdit       = queryEdit;
        _forwardToVs     = forwardToVs;
        _respondToServer = respondToServer;
        _isReadOnly      = isReadOnly ?? IsReadOnlyOnDisk;
    }

    /// <summary>The most recent check of a held-back request; lets tests wait for it.</summary>
    internal Task LastCheck { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
    {
        if (message.Direction != LspMessageDirection.Receive)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var body = message.Body;
        if (body is null
            || !string.Equals(body["method"]?.Value<string>(), LspStandardMethodNames.WorkspaceApplyEdit, StringComparison.Ordinal)
            || body["id"] is not { } id)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var readOnlyFiles = GetTargetFilePaths(body["params"]?["edit"] as JObject)
            .Where(SafeIsReadOnly)
            .ToList();
        if (readOnlyFiles.Count == 0)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        _logger.LogInformation(
            "SourceControlEditCheckInterceptor: workspace/applyEdit changes read-only file(s) {Files}; asking source control before Visual Studio applies it.",
            string.Join(", ", readOnlyFiles));

        LastCheck = Task.Run(() => CheckThenRouteAsync(body, id, readOnlyFiles));
        return Task.FromResult(LspInterceptorResult.Consume);
    }

    private async Task CheckThenRouteAsync(JObject request, JToken id, IReadOnlyList<string> readOnlyFiles)
    {
        // CancellationToken.None throughout: the server is waiting for an answer to this request,
        // and must get one whatever happens to the message that brought it.
        (bool Allowed, uint MoreInfo) outcome;
        try
        {
            outcome = await _queryEdit(readOnlyFiles, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Without an answer, leave it to Visual Studio as before.
            _logger.LogWarning(ex, "SourceControlEditCheckInterceptor: asking source control failed; passing the edit on to Visual Studio.");
            outcome = (true, 0);
        }

        if (outcome.Allowed)
        {
            if (await TryForwardAsync(request).ConfigureAwait(false))
                return;

            await TryRespondNotAppliedAsync(id, "the edit could not be passed on to Visual Studio").ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "SourceControlEditCheckInterceptor: editing {Files} was not allowed (moreInfo=0x{MoreInfo:X}); answering applied: false.",
            string.Join(", ", readOnlyFiles), outcome.MoreInfo);
        await TryRespondNotAppliedAsync(id, DescribeRefusal(readOnlyFiles)).ConfigureAwait(false);
    }

    private async Task<bool> TryForwardAsync(JObject request)
    {
        try
        {
            return await _forwardToVs(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SourceControlEditCheckInterceptor: passing the held-back workspace/applyEdit on to Visual Studio failed.");
            return false;
        }
    }

    private async Task TryRespondNotAppliedAsync(JToken id, string failureReason)
    {
        try
        {
            var result = new JObject { ["applied"] = false, ["failureReason"] = failureReason };
            if (!await _respondToServer(id, result, CancellationToken.None).ConfigureAwait(false))
                _logger.LogWarning("SourceControlEditCheckInterceptor: could not answer the server's workspace/applyEdit ({Reason}).", failureReason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SourceControlEditCheckInterceptor: answering the server's workspace/applyEdit failed.");
        }
    }

    /// <summary>The reason given to the server, naming the files that stay read-only.</summary>
    internal static string DescribeRefusal(IReadOnlyList<string> readOnlyFiles)
    {
        var names = readOnlyFiles.Select(Path.GetFileName).ToList();
        var shown = names.Count <= 3
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";
        return names.Count == 1
            ? $"{shown} is read-only and was not made writable"
            : $"{shown} are read-only and were not made writable";
    }

    /// <summary>
    /// The local paths of the files whose text <paramref name="edit"/> changes, from both its
    /// <c>documentChanges</c> and its <c>changes</c> map. Non-file URIs are left out.
    /// </summary>
    internal static IReadOnlyList<string> GetTargetFilePaths(JObject? edit)
    {
        var uris = new List<string>();
        if (edit?["documentChanges"] is JArray documentChanges)
        {
            foreach (var change in documentChanges.OfType<JObject>())
            {
                if (change["textDocument"]?["uri"]?.Value<string>() is { } uri)
                    uris.Add(uri);
            }
        }
        else if (edit?["changes"] is JObject changes)
        {
            uris.AddRange(changes.Properties().Select(p => p.Name));
        }

        return uris
            .Select(ToLocalPath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ToLocalPath(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;

    private bool SafeIsReadOnly(string path)
    {
        try
        {
            return _isReadOnly(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SourceControlEditCheckInterceptor: could not read the attributes of {Path}.", path);
            return false;
        }
    }

    private static bool IsReadOnlyOnDisk(string path) =>
        File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
}
