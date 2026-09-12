using System.Collections.Concurrent;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Owns what happens after a rename's <see cref="WorkspaceEditBuilder"/> is built: staging the
/// server-side cache updates the edit implies, pushing the edit to Visual Studio (which needs a
/// genuine <c>workspace/applyEdit</c> push, unlike other clients), and committing — or
/// discarding — the staged updates once the client says whether it actually applied the edit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is committed until the client confirms (issue #671, R3).</b> Previously the cache
/// updates ran inline at the end of <see cref="RenameHandler.HandleRenameAsync"/>, gated on a
/// confirmed apply only for Visual Studio; for every other client they ran unconditionally, on
/// the optimistic assumption the client would apply the edit it was handed. Rider disproved that
/// assumption (issue #670): its own pre-apply staleness check can discard the edit *after* the
/// rename response has been sent, leaving the registry describing a step expression that exists
/// in no file — "0 step usages" plus unbound-step diagnostics that survive a rebuild and a
/// close/reopen. Staging and waiting for <c>reqnroll/renameApplied</c> makes the server's view
/// derive from what the client actually did rather than from what it was asked to do.
/// </para>
/// <para>
/// <b>The Visual Studio push happens after the rename response, not inside it (R1).</b> See
/// <see cref="SchedulePostResponseApply"/>.
/// </para>
/// </remarks>
internal sealed class RenamePostApplyCoordinator
{
    /// <summary>The server-side cache work a single rename implies, held until the client confirms.</summary>
    private sealed record PendingCommit(
        WorkspaceEditBuilder Builder,
        DocumentUri?         CsFileUri,
        string?              NewCsText);

    private readonly ILanguageServerFacade          _languageServer;
    private readonly ClientIdeContext               _clientIdeContext;
    private readonly IBindingMatchService           _matchService;
    private readonly IDocumentBufferService         _documentBuffer;
    private readonly ICSharpBindingDiscoveryService _csharpDiscoveryService;
    private readonly ICSharpFileTextCache           _csharpFileTextCache;
    private readonly IIdeSupportLogger              _logger;
    private readonly IOperationDurationRecorder     _recorder;

    // Keyed by the rename request's own document URI (DocumentUri.ToString(), matching how every
    // other URI-keyed table in this codebase derives its key — see SelectRenameTargetParams's
    // remarks for the bug that convention exists to prevent). A rename is user-driven and rare,
    // so at most one entry per file is ever outstanding; a second rename on the same file simply
    // supersedes the first.
    private readonly ConcurrentDictionary<string, PendingCommit> _pendingCommits = new();

    public RenamePostApplyCoordinator(
        ILanguageServerFacade          languageServer,
        ClientIdeContext               clientIdeContext,
        IBindingMatchService           matchService,
        IDocumentBufferService         documentBuffer,
        ICSharpBindingDiscoveryService csharpDiscoveryService,
        ICSharpFileTextCache           csharpFileTextCache,
        IIdeSupportLogger              logger,
        IOperationDurationRecorder?    recorder = null)
    {
        _recorder                = recorder ?? NullOperationDurationRecorder.Instance;
        _languageServer          = languageServer;
        _clientIdeContext        = clientIdeContext;
        _matchService            = matchService;
        _documentBuffer          = documentBuffer;
        _csharpDiscoveryService  = csharpDiscoveryService;
        _csharpFileTextCache     = csharpFileTextCache;
        _logger                  = logger;
    }

    /// <summary>
    /// Records the cache work <paramref name="builder"/>'s edit implies, to be applied only once
    /// the client confirms it applied the edit (via <c>reqnroll/renameApplied</c>, or via Visual
    /// Studio's <c>workspace/applyEdit</c> response — see <see cref="SchedulePostResponseApply"/>).
    /// </summary>
    public void StagePendingCommit(
        DocumentUri          renameUri,
        WorkspaceEditBuilder builder,
        DocumentUri?         csFileUri,
        string?              newCsText)
    {
        _pendingCommits[renameUri.ToString()] = new PendingCommit(builder, csFileUri, newCsText);
        _logger.LogVerbose($"RenamePostApplyCoordinator: staged pending commit for '{renameUri}'");
    }

    /// <summary>
    /// Applies the staged cache updates for <paramref name="renameUri"/> when
    /// <paramref name="applied"/> is <see langword="true"/>, or drops them when the client reports
    /// it discarded the edit. A no-op when nothing is staged for that URI (a duplicate or late
    /// confirmation, or a rename that never staged anything).
    /// </summary>
    public async Task CompletePendingCommitAsync(DocumentUri renameUri, bool applied, CancellationToken cancellationToken)
    {
        if (!_pendingCommits.TryRemove(renameUri.ToString(), out var pending))
        {
            _logger.LogVerbose($"RenamePostApplyCoordinator: no pending commit staged for '{renameUri}'; ignoring confirmation (applied={applied})");
            return;
        }

        if (!applied)
        {
            _logger.LogVerbose($"RenamePostApplyCoordinator: client discarded the rename edit for '{renameUri}'; dropping staged cache updates");
            return;
        }

        InvalidateClosedFeatureCaches(pending.Builder);
        await RefreshCSharpRegistryAsync(pending.CsFileUri, pending.NewCsText, cancellationToken);
    }

    /// <summary>
    /// For Visual Studio, pushes the edit via <c>workspace/applyEdit</c> <i>after</i> the rename
    /// response has been returned, then self-confirms the staged commit from that push's
    /// <c>Applied</c> flag. A no-op for every other client, which applies the
    /// <see cref="WorkspaceEdit"/> returned from <c>textDocument/rename</c> itself and reports back
    /// with <c>reqnroll/renameApplied</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// VS's Rename Step command sends <c>textDocument/rename</c> over a custom interception pipe
    /// that swallows this method's return value before VS's built-in LSP client ever sees it, so
    /// VS needs the edit pushed via a genuine <c>workspace/applyEdit</c> request instead — the
    /// same mechanism already proven for the Comment/Uncomment toggle (<c>CommentToggleHandler</c>).
    /// </para>
    /// <para>
    /// <b>Why this must not be awaited inside the rename handler (issue #671, R1).</b> Applying a
    /// multi-file edit makes VS <i>open</i> any touched file that was closed, which fires a
    /// <c>textDocument/didOpen</c>. OmniSharp's <c>ProcessScheduler</c> treats didOpen/didChange/
    /// didSave as <c>Serial</c> items and, on any Parallel→Serial transition, cancels <b>every</b>
    /// outstanding Parallel request with <c>ContentModified</c> — globally, with no per-document
    /// scoping. So pushing the edit from inside the request deterministically cancelled the very
    /// request that produced it: the rename applied correctly but came back to the client as
    /// <c>-32801 Content Modified</c> (issue #654). Returning the response first leaves no
    /// in-flight request for that self-inflicted didOpen to cancel.
    /// </para>
    /// <para>
    /// This also keeps the handler off the shared <c>Serial</c> dispatch lane for the duration of a
    /// client round-trip. <c>textDocument/rename</c> is registered <c>Serial</c> (R8, for
    /// multi-document snapshot consistency) and an in-flight Serial item blocks every other
    /// didOpen/didChange <i>and</i> the start of newly-arriving Parallel requests (see
    /// <c>IParseCoordinator</c>'s remarks, issue #471). The #654 repro's push took 443ms — including
    /// VS opening a closed file — which would otherwise be 443ms of whole-server stall.
    /// </para>
    /// <para>
    /// Deliberately fire-and-forget: there is no caller left to await it, since the response has
    /// already gone. A rejected push (locked/read-only file, or unsaved conflicting changes) can
    /// therefore no longer be reported to the user as a failed rename the way it once was — it is
    /// logged, and the staged cache updates are dropped so server state still matches reality,
    /// which is the part that matters.
    /// </para>
    /// </remarks>
    public void SchedulePostResponseApply(DocumentUri renameUri, WorkspaceEditBuilder builder)
    {
        if (!_clientIdeContext.IsVisualStudio)
            return;

        _ = Task.Run(() => PushAndCompleteAsync(renameUri, builder));
    }

    /// <summary>
    /// The body <see cref="SchedulePostResponseApply"/> runs off-thread: pushes the edit to Visual
    /// Studio and completes the staged commit from the result. Separated from the scheduling so
    /// tests can await it deterministically instead of polling a fire-and-forget task; production
    /// code must go through <see cref="SchedulePostResponseApply"/>, which is what keeps the rename
    /// request from awaiting a client round-trip.
    /// </summary>
    internal async Task PushAndCompleteAsync(DocumentUri renameUri, WorkspaceEditBuilder builder)
    {
        // Measured under its own label because this work used to be inside the measured
        // textDocument/rename request (issue #671, R1) — the applyEdit round trip and the cache
        // commit it confirms. Without this it would simply disappear from the PERF log for VS,
        // leaving a future "rename got slow" report with nothing to look at. The other clients'
        // equivalent is measured as reqnroll/renameApplied.
        using var _perf = _recorder.Measure(LspMethodNames.InternalRenamePostResponseApply, renameUri);

        // CancellationToken.None, not the request's token: by the time this runs the request has
        // completed and OmniSharp has cancelled its token, which would abort the push immediately.
        try
        {
            var applied = await PushEditToVisualStudioAsync(builder, CancellationToken.None);
            await CompletePendingCommitAsync(renameUri, applied, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError($"RenamePostApplyCoordinator: post-response workspace/applyEdit for '{renameUri}' failed: {ex}");
            await CompletePendingCommitAsync(renameUri, applied: false, CancellationToken.None);
        }
    }

    private async Task<bool> PushEditToVisualStudioAsync(WorkspaceEditBuilder builder, CancellationToken cancellationToken)
    {
        // VS never advertises changeAnnotationSupport (Phase 0), so builder's edits are
        // already plain TextEdit here — this push is unannotated DocumentChanges regardless.
        //
        // Version-stamped (issue #671, R2): this push is what actually applies the edit in VS, so
        // it is the one emission where a stale-document check protects real content. `null` for a
        // document VS has not opened is the spec's own meaning ("the content on disk is the
        // master"), which is the common case here — a rename routinely touches closed .feature
        // files and a .cs file VS never opened against this server.
        var pushParams = new ApplyWorkspaceEditParams
        {
            Edit = new WorkspaceEdit
            {
                DocumentChanges = new Container<WorkspaceEditDocumentChange>(
                    builder.GetEditsByUri().Select(kvp => new WorkspaceEditDocumentChange(new TextDocumentEdit
                    {
                        TextDocument = new OptionalVersionedTextDocumentIdentifier
                        {
                            Uri = kvp.Key,
                            Version = ResolveDocumentVersion(kvp.Key)
                        },
                        Edits = new TextEditContainer(kvp.Value)
                    })))
            }
        };

        var response = await _languageServer.SendRequest(LspMethodNames.WorkspaceApplyEdit, pushParams)
            .Returning<ApplyWorkspaceEditResponse>(cancellationToken);

        if (response is not { Applied: true })
        {
            _logger.LogWarning($"RenamePostApplyCoordinator: VS rejected workspace/applyEdit (reason: '{response?.FailureReason}') — not refreshing server caches");
            return false;
        }

        _logger.LogVerbose("RenamePostApplyCoordinator: VS applied workspace/applyEdit");
        return true;
    }

    /// <summary>
    /// The LSP document version an edit for <paramref name="uri"/> is computed against, or
    /// <see langword="null"/> when the client has not opened it — see
    /// <see cref="RenameHandler.ResolveDocumentVersion"/>, which this mirrors for the VS push.
    /// </summary>
    private int? ResolveDocumentVersion(DocumentUri uri)
        => _documentBuffer.TryGet(uri, out var buffer) ? buffer?.Version : null;

    /// <summary>
    /// Invalidates the match cache for CLOSED feature files that were modified by the rename.
    /// When a feature file is closed at rename time, no didChange notification fires, so the
    /// server's in-memory match cache would otherwise retain the old step text until the file is
    /// re-opened and re-parsed. For OPEN files, applying the edit already triggers a real
    /// textDocument/didChange, which reparses and correctly rebuilds the match cache through the
    /// normal sync pipeline — invalidating here too would race with that rebuild. Losing that race
    /// leaves the cache empty with nothing left to repopulate it, since the file's content isn't
    /// changing again: confirmed live as inlay hints silently disappearing for the whole file
    /// post-rename.
    /// </summary>
    private void InvalidateClosedFeatureCaches(WorkspaceEditBuilder builder)
    {
        foreach (var changedUri in builder.TouchedUris)
        {
            var changedPath = changedUri.GetFileSystemPath();
            if (!string.IsNullOrEmpty(changedPath) && changedPath.EndsWith(".feature", StringComparison.OrdinalIgnoreCase) &&
                !_documentBuffer.TryGet(changedUri, out _))
            {
                _matchService.InvalidateAllForDocument(changedUri.ToString());
                _logger.LogVerbose($"RenamePostApplyCoordinator: invalidated match cache for closed '{changedUri}'");
            }
        }
    }

    /// <summary>
    /// Self-refreshes the C# binding registry for the edited .cs file directly, rather than
    /// relying on a client-echoed textDocument/didChange: a closed .cs file is edited in the
    /// client's in-memory document without ever being opened against the LSP server, so it never
    /// round-trips one. No-op when <paramref name="csFileUri"/> or <paramref name="newCsText"/> is
    /// <see langword="null"/> (no .cs edit was built).
    /// </summary>
    private async Task RefreshCSharpRegistryAsync(DocumentUri? csFileUri, string? newCsText, CancellationToken cancellationToken)
    {
        if (csFileUri is null || newCsText == null)
            return;

        // Cache updated before the discovery call, not after: UpdateFromSourceAsync has its own
        // staleness short-circuit that compares its `text` parameter against the live-text cache's
        // current content (see its remarks), so the cache must already reflect this edit's text by
        // the time that call runs, or it would look superseded and be skipped.
        _csharpFileTextCache.Update(csFileUri, newCsText);
        await _csharpDiscoveryService.UpdateFromSourceAsync(csFileUri, newCsText, isOpen: false, cancellationToken);
        _logger.LogVerbose($"RenamePostApplyCoordinator: refreshed C# binding registry for '{csFileUri}'");
    }
}
