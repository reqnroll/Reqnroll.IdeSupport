using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Handles <see cref="ReqnrollConfigChangedNotification"/> by re-parsing every open
/// feature file that belongs to the affected workspace root, then publishing a
/// <see cref="MatchCacheChangedNotification"/> for each so that semantic tokens
/// (and any other consumers) are refreshed.
/// </summary>
/// <remarks>
/// Routes each buffer's reparse through <see cref="IParseCoordinator"/> (issue #576) rather than
/// awaiting it inline, matching every other open-document reparse path
/// (<see cref="Features.TextSync.TextDocumentSyncHandler"/>,
/// <see cref="BindingRegistryChangedHandler.ReparseOpenFilesAsync"/>). Without this, a
/// <c>reqnroll.json</c>/<c>.editorconfig</c> save landing while a <c>didChange</c> reparse is
/// already in flight for the same URI could run two concurrent <c>ParseAsync</c> calls against
/// the same document — the shape issue #554 fixed for the didChange/registry-cascade paths, but
/// which this handler had never been routed through. It also means
/// <see cref="Features.Folding.FoldingRangeHandler"/>/<see cref="Features.DocumentOutline.DocumentSymbolHandler"/>'s
/// <c>WaitForReadyAsync</c> calls — the only guard those two refresh-incapable pull handlers have
/// against reading a stale buffer — now see a pending entry for a config-driven reparse, not just
/// a direct-edit one.
/// </remarks>
public class ReqnrollConfigChangedHandler : INotificationHandler<ReqnrollConfigChangedNotification>
{
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IGherkinDocumentTaggerService _taggerService;
    private readonly IMediator _mediator;
    private readonly IParseCoordinator _parseCoordinator;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="ReqnrollConfigChangedHandler"/> class.</summary>
    public ReqnrollConfigChangedHandler(
        IDocumentBufferService documentBufferService,
        IGherkinDocumentTaggerService taggerService,
        IMediator mediator,
        IParseCoordinator parseCoordinator,
        IIdeSupportLogger logger,
        IOperationDurationRecorder? recorder = null)
    {
        _documentBufferService = documentBufferService;
        _taggerService = taggerService;
        _mediator = mediator;
        _parseCoordinator = parseCoordinator;
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles an internal <see cref="ReqnrollConfigChangedNotification"/> (a <c>reqnroll.json</c> edit) by scheduling a reparse of every open feature-file buffer under the affected workspace root.</summary>
    public Task Handle(ReqnrollConfigChangedNotification notification, CancellationToken cancellationToken)
    {
        // Performance Verification (Layer 4): time the reqnroll.json-change reconciliation. Now
        // measures only the (near-instant) scheduling of each reparse, not the reparses
        // themselves -- mirrors BindingRegistryChangedHandler.ReparseOpenFilesAsync's equivalent
        // note, since the actual parse work now runs after this method returns.
        using var _perf = _recorder.Measure(LspMethodNames.InternalReqnrollConfigReconcile);

        var affectedBuffers = _documentBufferService.All
            .Where(b => IsUnderWorkspaceRoot(b.Uri, notification.WorkspaceRootPath))
            .ToList();

        if (affectedBuffers.Count == 0)
        {
            _logger.LogVerbose($"ReqnrollConfigChanged for '{notification.WorkspaceRootPath}' — no open feature files to reparse.");
            return Task.CompletedTask;
        }

        _logger.LogInfo($"ReqnrollConfigChanged for '{notification.WorkspaceRootPath}' — scheduling reparse of {affectedBuffers.Count} feature file(s).");

        foreach (var buffer in affectedBuffers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = buffer.Uri;
            var version = buffer.Version;
            _parseCoordinator.Schedule(uri, ct => ParseAndNotifyAsync(uri, version, ct));
        }

        return Task.CompletedTask;
    }

    private async Task ParseAndNotifyAsync(DocumentUri uri, int? version, CancellationToken cancellationToken)
    {
        // ParseAsync stores updated tags, recomputes/stores the binding match set, and
        // invalidates the semantic token cache internally before this notification fires.
        await _taggerService.ParseAsync(uri, version).ConfigureAwait(false);
        await _mediator.Publish(
            new MatchCacheChangedNotification(uri, version ?? 0),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsUnderWorkspaceRoot(DocumentUri uri, string workspaceRootPath)
        => PathUtils.IsUnderFolder(uri.GetFileSystemPath(), workspaceRootPath);
}
