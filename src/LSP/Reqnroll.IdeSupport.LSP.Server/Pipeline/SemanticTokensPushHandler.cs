using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.LSP.Server.Concurrency;
using Reqnroll.IdeSupport.LSP.Server.Documents;
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
/// <para>
/// Each push re-encodes and serializes every token of the document, and the client decodes it on its
/// UI thread, so a burst of edits must not produce one push per edit. Notifications are debounced per
/// document through the shared <see cref="IRefreshDebouncer"/> singleton (not an instance field — this
/// handler is constructed per notification); when the quiet period ends, the document's newest
/// tokens and version are pushed, and nothing is pushed if the document has been closed meanwhile.
/// </para>
/// </remarks>
public class SemanticTokensPushHandler : INotificationHandler<MatchCacheChangedNotification>
{
    /// <summary>Quiet period after the last match-cache change of a document before its tokens are pushed.</summary>
    internal static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(250);

    private readonly ILanguageServerFacade _languageServer;
    private readonly ISemanticTokensService _tokenService;
    private readonly ClientIdeContext _clientIde;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;
    private readonly IDocumentBufferService _documents;
    private readonly IRefreshDebouncer _debouncer;

    /// <summary>Initializes a new instance of the <see cref="SemanticTokensPushHandler"/> class.</summary>
    public SemanticTokensPushHandler(
        ILanguageServerFacade languageServer,
        ISemanticTokensService tokenService,
        ClientIdeContext clientIde,
        IIdeSupportLogger logger,
        IDocumentBufferService documents,
        IRefreshDebouncer debouncer,
        IOperationDurationRecorder? recorder = null)
    {
        _languageServer = languageServer;
        _tokenService = tokenService;
        _clientIde = clientIde;
        _logger = logger;
        _documents = documents;
        _debouncer = debouncer;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles an internal <see cref="MatchCacheChangedNotification"/> by scheduling a debounced push of the document's latest semantic tokens to Visual Studio clients (VS does not pull tokens itself; all other clients are skipped).</summary>
    public Task Handle(MatchCacheChangedNotification notification, CancellationToken cancellationToken)
    {
        // Only Visual Studio needs the push; all other clients pull semantic tokens themselves.
        if (!_clientIde.Behaviours.RequiresPushedSemanticTokens)
        {
            _logger.LogVerbose($"SemanticTokensPushHandler: skipped (client is not VS) for {notification.Uri} v{notification.Version}");
            return Task.CompletedTask;
        }

        var uri = notification.Uri;
        var fallbackVersion = notification.Version;
        _debouncer.Schedule(
            $"{nameof(SemanticTokensPushHandler)}:{uri}",
            CoalesceDelay,
            ct => PushLatestAsync(uri, fallbackVersion, ct));
        return Task.CompletedTask;
    }

    private async Task PushLatestAsync(DocumentUri uri, int fallbackVersion, CancellationToken cancellationToken)
    {
        // Read the buffer when the quiet period ends, not when the notification arrived, so the
        // version and tokens describe the same, newest text.
        if (!_documents.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"SemanticTokensPushHandler: skipped (document closed) for {uri}");
            return;
        }

        var version = buffer.Version ?? fallbackVersion;
        using var _perf = _recorder.Measure(CustomLspMethodNames.ReqnrollSemanticTokens, uri);

        var tokens = await _tokenService
            .GetSemanticTokensAsync(uri, version, cancellationToken)
            .ConfigureAwait(false);
        if (tokens is null)
            return;

        var data = tokens.Data.ToArray();
        _languageServer.SendNotification(CustomLspMethodNames.ReqnrollSemanticTokens, new PublishSemanticTokensParams
        {
            Uri = uri.ToString(),
            Version = version,
            Data = data,
        });

        _logger.LogInfo($"SemanticTokensPushHandler: pushed {data.Length / 5} tokens for {uri} v{version}");
    }
}
