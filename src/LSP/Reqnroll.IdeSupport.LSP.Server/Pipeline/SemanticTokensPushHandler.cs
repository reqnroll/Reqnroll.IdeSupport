using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;

using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
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

    /// <summary>Initializes a new instance of the <see cref="SemanticTokensPushHandler"/> class.</summary>
    public SemanticTokensPushHandler(
        ILanguageServerFacade languageServer,
        ISemanticTokensService tokenService,
        ClientIdeContext clientIde,
        IIdeSupportLogger logger,
        IOperationDurationRecorder? recorder = null)
    {
        _languageServer = languageServer;
        _tokenService = tokenService;
        _clientIde = clientIde;
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles an internal <see cref="MatchCacheChangedNotification"/> by computing and pushing updated semantic tokens to Visual Studio clients (VS does not pull tokens itself; all other clients are skipped).</summary>
    public async Task Handle(MatchCacheChangedNotification notification, CancellationToken cancellationToken)
    {
        // Only Visual Studio needs the push; all other clients pull semantic tokens themselves.
        if (!_clientIde.IsVisualStudio)
        {
            _logger.LogVerbose($"SemanticTokensPushHandler: skipped (client is not VS) for {notification.Uri} v{notification.Version}");
            return;
        }

        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollSemanticTokens, notification.Uri);

        var tokens = await _tokenService
            .GetSemanticTokensAsync(notification.Uri, notification.Version, cancellationToken)
            .ConfigureAwait(false);
        if (tokens is null)
            return;

        var data = tokens.Data.ToArray();
        _languageServer.SendNotification(LspMethodNames.ReqnrollSemanticTokens, new PublishSemanticTokensParams
        {
            Uri = notification.Uri.ToString(),
            Version = notification.Version,
            Data = data,
        });

        _logger.LogInfo(
            $"SemanticTokensPushHandler: pushed {data.Length / 5} tokens for {notification.Uri} v{notification.Version}");
    }
}
