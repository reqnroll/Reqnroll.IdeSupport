using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.LSP.Server.Concurrency;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Pushes encoded semantic tokens to the Visual Studio client whenever a feature file's match
/// cache changes.
/// </summary>
/// <remarks>
/// Visual Studio's built-in LSP semantic-token colorizer maps token-type names through a fixed
/// internal table (so it cannot honour Reqnroll's custom <c>reqnroll.*</c> types) and only pulls
/// tokens lazily and unreliably. For VS we therefore push the tokens proactively via the custom
/// <c>reqnroll/semanticTokens</c> notification; the VS client decodes them and colours the file
/// with its own classifier. This handler is a no-op for every other client, which use the standard
/// pull-based flow handled by <see cref="SemanticTokensRefreshHandler"/>.
/// </remarks>
public class SemanticTokensPushHandler : INotificationHandler<MatchCacheChangedNotification>
{
    private readonly ILanguageServerFacade _languageServer;
    private readonly ISemanticTokensService _tokenService;
    private readonly ClientIdeContext _clientIde;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;
    private readonly IPushedSemanticTokens _pushedTokens;
    private readonly IOutputThrottle _throttle;

    /// <summary>Initializes a new instance of the <see cref="SemanticTokensPushHandler"/> class.</summary>
    public SemanticTokensPushHandler(
        ILanguageServerFacade languageServer,
        ISemanticTokensService tokenService,
        ClientIdeContext clientIde,
        IIdeSupportLogger logger,
        IOperationDurationRecorder? recorder = null,
        IPushedSemanticTokens? pushedTokens = null,
        IOutputThrottle? throttle = null)
    {
        _languageServer = languageServer;
        _tokenService = tokenService;
        _clientIde = clientIde;
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
        _pushedTokens = pushedTokens ?? new PushedSemanticTokens();
        _throttle = throttle ?? NullOutputThrottle.Instance;
    }

    /// <summary>Handles an internal <see cref="MatchCacheChangedNotification"/> by computing and pushing updated semantic tokens to Visual Studio clients (VS does not pull tokens itself; all other clients are skipped). For a client whose behaviours say so (<see cref="Reqnroll.IdeSupport.LSP.Core.Ide.IdeBehaviours.ThrottlesOutputWhileEditing"/>), the push runs at most once per <see cref="OutputThrottleDefaults.Interval"/> while edits keep arriving and always carries the latest tokens.</summary>
    public Task Handle(MatchCacheChangedNotification notification, CancellationToken cancellationToken)
    {
        // Only Visual Studio needs the push; all other clients pull semantic tokens themselves.
        if (!_clientIde.Behaviours.RequiresPushedSemanticTokens)
        {
            _logger.LogVerbose($"SemanticTokensPushHandler: skipped (client is not VS) for {notification.Uri} v{notification.Version}");
            return Task.CompletedTask;
        }

        return _clientIde.Behaviours.ThrottlesOutputWhileEditing
            ? _throttle.RunThrottledAsync(
                OutputThrottleKeys.SemanticTokens(notification.Uri),
                OutputThrottleDefaults.Interval,
                () => PushTokensAsync(notification))
            : PushTokensAsync(notification);
    }

    private async Task PushTokensAsync(MatchCacheChangedNotification notification)
    {
        using var _perf = _recorder.Measure(CustomLspMethodNames.ReqnrollSemanticTokens, notification.Uri);

        var tokens = await _tokenService
            .GetSemanticTokensAsync(notification.Uri, notification.Version, CancellationToken.None)
            .ConfigureAwait(false);
        if (tokens is null)
            return;

        var uri = notification.Uri.ToString();
        var push = _pushedTokens.Plan(uri, tokens.Data.ToArray());
        if (push.Unchanged)
        {
            _logger.LogVerbose(
                $"SemanticTokensPushHandler: tokens unchanged for {uri} v{notification.Version} — nothing to push");
            return;
        }

        try
        {
            _languageServer.SendNotification(CustomLspMethodNames.ReqnrollSemanticTokens, new PublishSemanticTokensParams
            {
                Uri = uri,
                Version = notification.Version,
                Data = push.Data,
                StartLine = push.StartLine,
                EndLine = push.EndLine,
                TokenCount = push.TokenCount,
            });
        }
        catch
        {
            // The baseline now describes tokens the client never received; the next push must be the full set.
            _pushedTokens.Forget(uri);
            throw;
        }

        _logger.LogInfo(push.IsRanged
            ? $"SemanticTokensPushHandler: pushed {push.Data.Length / 5} of {push.TokenCount} tokens (lines {push.StartLine}-{push.EndLine}) for {uri} v{notification.Version}"
            : $"SemanticTokensPushHandler: pushed {push.TokenCount} tokens for {uri} v{notification.Version}");
    }
}
