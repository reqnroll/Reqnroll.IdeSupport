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
public class ReqnrollConfigChangedHandler : INotificationHandler<ReqnrollConfigChangedNotification>
{
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IGherkinDocumentTaggerService _taggerService;
    private readonly IMediator _mediator;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="ReqnrollConfigChangedHandler"/> class.</summary>
    public ReqnrollConfigChangedHandler(
        IDocumentBufferService documentBufferService,
        IGherkinDocumentTaggerService taggerService,
        IMediator mediator,
        IIdeSupportLogger logger,
        IOperationDurationRecorder? recorder = null)
    {
        _documentBufferService = documentBufferService;
        _taggerService = taggerService;
        _mediator = mediator;
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles an internal <see cref="ReqnrollConfigChangedNotification"/> (a <c>reqnroll.json</c> edit) by re-parsing every open feature-file buffer under the affected workspace root.</summary>
    public async Task Handle(ReqnrollConfigChangedNotification notification, CancellationToken cancellationToken)
    {
        // Performance Verification (Layer 4): time the reqnroll.json-change reconciliation.
        using var _perf = _recorder.Measure(LspMethodNames.InternalReqnrollConfigReconcile);

        var affectedBuffers = _documentBufferService.All
            .Where(b => IsUnderWorkspaceRoot(b.Uri, notification.WorkspaceRootPath))
            .ToList();

        if (affectedBuffers.Count == 0)
        {
            _logger.LogVerbose($"ReqnrollConfigChanged for '{notification.WorkspaceRootPath}' — no open feature files to reparse.");
            return;
        }

        _logger.LogInfo($"ReqnrollConfigChanged for '{notification.WorkspaceRootPath}' — reparsing {affectedBuffers.Count} feature file(s).");

        foreach (var buffer in affectedBuffers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ParseAndNotifyAsync(buffer.Uri, buffer.Version, cancellationToken).ConfigureAwait(false);
        }
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
