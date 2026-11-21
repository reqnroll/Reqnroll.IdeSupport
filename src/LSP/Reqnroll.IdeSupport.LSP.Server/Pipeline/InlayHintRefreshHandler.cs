using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.LSP.Server.Concurrency;
using Reqnroll.IdeSupport.LSP.Server.Performance;
namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Handles <see cref="MatchCacheChangedNotification"/> by asking the LSP client to refresh its
/// inlay hints (F23), so binding-info hints stay in sync after edits/build without the user
/// having to scroll the viewport to re-pull them.
/// </summary>
/// <remarks>
/// Mirrors <see cref="SemanticTokensRefreshHandler"/>: debounces bursts of match-cache
/// notifications into a single <c>workspace/inlayHint/refresh</c> request via the shared
/// <see cref="IRefreshDebouncer"/> singleton (not an instance field — see that type's remarks for
/// why), and only sends it when the client advertised <c>workspace.inlayHint.refreshSupport</c>.
/// <see cref="SendRefreshAsync"/> passes the debouncer's own token into the request,
/// so a superseding notification cancels a refresh already in flight, not just one still waiting
/// out the debounce delay.
/// </remarks>
public class InlayHintRefreshHandler : INotificationHandler<MatchCacheChangedNotification>
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);
    private const string DebounceKey = nameof(InlayHintRefreshHandler);

    private readonly ILanguageServerFacade _languageServer;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;
    private readonly IRefreshDebouncer _debouncer;
    private readonly IRefreshGate? _gate;

    /// <summary>Initializes a new instance of the <see cref="InlayHintRefreshHandler"/> class.</summary>
    public InlayHintRefreshHandler(
        ILanguageServerFacade languageServer,
        IIdeSupportLogger logger,
        IRefreshDebouncer debouncer,
        IOperationDurationRecorder? recorder = null,
        IRefreshGate? gate = null)
    {
        _languageServer = languageServer;
        _logger = logger;
        _debouncer = debouncer;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
        _gate = gate;
    }

    /// <summary>Handles an internal <see cref="MatchCacheChangedNotification"/> by debouncing and, if the client supports it, sending a <c>workspace/inlayHint/refresh</c> request.</summary>
    public Task Handle(MatchCacheChangedNotification notification, CancellationToken cancellationToken)
    {
        var inlayHintWorkspace = _languageServer.ClientSettings.Capabilities?.Workspace?.InlayHint;
        if (inlayHintWorkspace is null || !inlayHintWorkspace.Value.IsSupported ||
            inlayHintWorkspace.Value.Value?.RefreshSupport != true)
            return Task.CompletedTask;

        _logger.LogVerbose($"MatchCacheChanged: scheduling inlay hint refresh for {notification.Uri} v{notification.Version}");

        _debouncer.Schedule(DebounceKey, DebounceDelay, GatedSendRefreshAsync);
        return Task.CompletedTask;
    }

    // With the gate, at most one refresh request is outstanding at a time: a client that answers slowly (Visual Studio
    // in a very large file) is told about the latest change when it is ready, not buried under requests for states
    // that are already out of date.
    private Task GatedSendRefreshAsync(CancellationToken cancellationToken) =>
        _gate is null
            ? SendRefreshAsync(cancellationToken)
            : _gate.RunAsync(DebounceKey, SendRefreshAsync);

    private async Task SendRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var _perf = _recorder.Measure(LspStandardMethodNames.WorkspaceInlayHintRefresh);

            _logger.LogVerbose("InlayHintRefreshHandler: sending workspace/inlayHint/refresh");
            await _languageServer.Client
                .SendRequest(WorkspaceNames.InlayHintRefresh)
                .ReturningVoid(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"InlayHint refresh request failed: {ex.Message}");
        }
    }
}
