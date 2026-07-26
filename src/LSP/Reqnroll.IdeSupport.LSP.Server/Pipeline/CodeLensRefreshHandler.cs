using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Handles <see cref="MatchCacheChangedNotification"/> by asking the client to refresh the "N step
/// usages" code lens rendered over C# step-definition methods, so a <c>.feature</c> file edit that
/// changes a step's usage count is reflected without the user switching away from and back to the
/// <c>.cs</c> editor.
/// </summary>
/// <remarks>
/// <see cref="BindingRegistryChangedHandler"/> already requests this refresh after a binding-registry
/// change (a <c>.cs</c> edit or connector rebuild) — but until this handler existed, a <c>.feature</c>
/// file's own edits (which change usage counts just as much) had no refresh trigger of their own.
/// Clicking a stale lens still worked (it reruns <c>findStepUsages</c> fresh), but the displayed
/// count silently went stale until something else forced a recompute. Mirrors
/// <see cref="SemanticTokensRefreshHandler"/>/<see cref="InlayHintRefreshHandler"/>: debounces bursts
/// of match-cache notifications into a single refresh via the shared <see cref="IRefreshDebouncer"/>
/// singleton — not an instance field, since MediatR constructs a new handler instance per
/// notification (see <see cref="IRefreshDebouncer"/>'s remarks), which previously meant every
/// notification scheduled its own independent refresh instead of debouncing (issue #156).
/// </remarks>
public class CodeLensRefreshHandler : INotificationHandler<MatchCacheChangedNotification>
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);
    private const string DebounceKey = nameof(CodeLensRefreshHandler);

    private readonly ILanguageServerFacade _languageServer;
    private readonly ClientIdeContext _clientIde;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;
    private readonly IRefreshDebouncer _debouncer;

    /// <summary>Initializes a new instance of the <see cref="CodeLensRefreshHandler"/> class.</summary>
    public CodeLensRefreshHandler(
        ILanguageServerFacade languageServer,
        ClientIdeContext clientIde,
        IIdeSupportLogger logger,
        IRefreshDebouncer debouncer,
        IOperationDurationRecorder? recorder = null)
    {
        _languageServer = languageServer;
        _clientIde = clientIde;
        _logger = logger;
        _debouncer = debouncer;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles an internal <see cref="MatchCacheChangedNotification"/> by debouncing and requesting a code lens refresh.</summary>
    public Task Handle(MatchCacheChangedNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogVerbose($"MatchCacheChanged: scheduling code lens refresh for {notification.Uri} v{notification.Version}");

        _debouncer.Schedule(DebounceKey, DebounceDelay, SendRefreshAsync);
        return Task.CompletedTask;
    }

    private async Task SendRefreshAsync(CancellationToken cancellationToken)
    {
        using var _perf = _recorder.Measure(LspMethodNames.WorkspaceCodeLensRefresh);
        await CodeLensRefreshRequester
            .RequestRefreshAsync(_languageServer, _clientIde, _logger, projectName: string.Empty)
            .ConfigureAwait(false);
    }
}
