using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Handles <see cref="MatchCacheChangedNotification"/> by asking the LSP client
/// to refresh its semantic tokens. No tag encoding is performed here; encoding is
/// deferred until the client sends a <c>textDocument/semanticTokens/full</c> request.
/// </summary>
/// <remarks>
/// The refresh is driven by the match-cache notification (rather than a raw parse
/// notification) so that it fires only after binding matches have been recomputed and
/// the binding-overlay tags are current; refreshing earlier would re-encode pre-binding
/// tokens. Multiple notifications can arrive in quick succession (e.g. when several files
/// open at once, or a build replaces the registry). A debounce window, held in the shared
/// <see cref="IRefreshDebouncer"/> singleton (not an instance field — MediatR constructs a new
/// handler instance per notification, see that type's remarks), collapses those bursts into a
/// single <c>workspace/semanticTokens/refresh</c> request so the client is not flooded. The
/// refresh is also guarded by a client-capability check: if the client did not advertise
/// <c>workspace.semanticTokens.refreshSupport</c> the request is skipped.
/// </remarks>
public class SemanticTokensRefreshHandler : INotificationHandler<MatchCacheChangedNotification>
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);
    private const string DebounceKey = nameof(SemanticTokensRefreshHandler);

    private readonly ILanguageServerFacade _languageServer;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;
    private readonly IRefreshDebouncer _debouncer;

    /// <summary>Initializes a new instance of the <see cref="SemanticTokensRefreshHandler"/> class.</summary>
    public SemanticTokensRefreshHandler(
        ILanguageServerFacade languageServer,
        IIdeSupportLogger logger,
        IRefreshDebouncer debouncer,
        IOperationDurationRecorder? recorder = null)
    {
        _languageServer = languageServer;
        _logger = logger;
        _debouncer = debouncer;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles an internal <see cref="MatchCacheChangedNotification"/> by debouncing and, if the client advertised refresh support, sending a <c>workspace/semanticTokens/refresh</c> request.</summary>
    public Task Handle(MatchCacheChangedNotification notification, CancellationToken cancellationToken)
    {
        // Guard: only send the refresh if the client advertised support for it.
        var semanticTokensWorkspace = _languageServer.ClientSettings.Capabilities?.Workspace?.SemanticTokens;
        if (semanticTokensWorkspace is null || !semanticTokensWorkspace.Value.IsSupported ||
            semanticTokensWorkspace.Value.Value?.RefreshSupport != true)
            return Task.CompletedTask;

        _logger.LogVerbose($"MatchCacheChanged: scheduling semantic token refresh for {notification.Uri} v{notification.Version}");

        _debouncer.Schedule(DebounceKey, DebounceDelay, SendRefreshAsync);
        return Task.CompletedTask;
    }

    private async Task SendRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var _perf = _recorder.Measure(LspMethodNames.WorkspaceSemanticTokensRefresh);

            _logger.LogVerbose("SemanticTokensRefreshHandler: sending workspace/semanticTokens/refresh");
            await _languageServer.Client
                .SendRequest(WorkspaceNames.SemanticTokensRefresh)
                .ReturningVoid(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"SemanticTokens refresh request failed: {ex.Message}");
        }
    }
}
