using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

public class SemanticTokensRefreshHandlerTests : IDisposable
{
    private readonly ILanguageServerFacade _languageServer = Substitute.For<ILanguageServerFacade>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    // A real, shared RefreshDebouncer — not a per-test-instance field on the handler itself —
    // because that's exactly the distinction issue #156 turned on: MediatR constructs a new
    // handler instance per notification, so only debounce state living outside the handler
    // (here, this shared singleton stand-in) can actually collapse a burst across those instances.
    private readonly RefreshDebouncer _debouncer;

    public SemanticTokensRefreshHandlerTests() => _debouncer = new RefreshDebouncer(_logger);

    public void Dispose() => _debouncer.Dispose();

    private SemanticTokensRefreshHandler CreateSut() => new(_languageServer, _logger, _debouncer);

    private void SetRefreshSupport(bool? supported)
    {
        _languageServer.ClientSettings.Returns(new InitializeParams
        {
            Capabilities = new ClientCapabilities
            {
                Workspace = new WorkspaceClientCapabilities
                {
                    SemanticTokens = supported.HasValue
                        ? new Supports<SemanticTokensWorkspaceCapability>(true,
                            new SemanticTokensWorkspaceCapability { RefreshSupport = supported.Value })
                        : new Supports<SemanticTokensWorkspaceCapability>(false)
                }
            }
        });
    }

    [Fact]
    public async Task Handle_sends_refresh_when_the_client_supports_it()
    {
        SetRefreshSupport(true);
        var sent = new TaskCompletionSource();
        _languageServer.Client.When(c => c.SendRequest(Arg.Any<string>())).Do(_ => sent.TrySetResult());

        await CreateSut().Handle(new MatchCacheChangedNotification(DocumentUri.From("file:///f.feature"), 1), CancellationToken.None);
        await Task.WhenAny(sent.Task, Task.Delay(5000));

        _languageServer.Client.Received(1).SendRequest(WorkspaceNames.SemanticTokensRefresh);
    }

    [Fact]
    public async Task Handle_does_not_send_refresh_when_the_client_declares_no_support()
    {
        SetRefreshSupport(false);

        await CreateSut().Handle(new MatchCacheChangedNotification(DocumentUri.From("file:///f.feature"), 1), CancellationToken.None);
        await Task.Delay(700);

        _languageServer.Client.DidNotReceive().SendRequest(WorkspaceNames.SemanticTokensRefresh);
    }

    [Fact]
    public async Task Handle_does_not_send_refresh_when_workspace_capabilities_are_absent()
    {
        _languageServer.ClientSettings.Returns(new InitializeParams { Capabilities = new ClientCapabilities() });

        await CreateSut().Handle(new MatchCacheChangedNotification(DocumentUri.From("file:///f.feature"), 1), CancellationToken.None);
        await Task.Delay(700);

        _languageServer.Client.DidNotReceive().SendRequest(WorkspaceNames.SemanticTokensRefresh);
    }

    // Issue #471: passing CancellationToken.None here (as this used to, silently) means a
    // superseding MatchCacheChangedNotification can never cancel a refresh already dispatched to
    // the client -- only one still waiting out the debounce delay. See
    // IRefreshDebouncer.Schedule's remarks.
    [Fact]
    public async Task Handle_passes_the_debouncer_supplied_token_to_the_refresh_request()
    {
        SetRefreshSupport(true);
        var fakeReturns = Substitute.For<IResponseRouterReturns>();
        _languageServer.Client.SendRequest(Arg.Any<string>()).Returns(fakeReturns);

        await CreateSut().Handle(new MatchCacheChangedNotification(DocumentUri.From("file:///f.feature"), 1), CancellationToken.None);
        await Task.Delay(700);

        await fakeReturns.Received(1).ReturningVoid(Arg.Is<CancellationToken>(t => t != CancellationToken.None));
    }

    [Fact]
    public async Task Handle_debounces_bursts_into_a_single_refresh()
    {
        SetRefreshSupport(true);
        var sent = new TaskCompletionSource();
        _languageServer.Client.When(c => c.SendRequest(Arg.Any<string>())).Do(_ => sent.TrySetResult());

        var uri = DocumentUri.From("file:///f.feature");
        await CreateSut().Handle(new MatchCacheChangedNotification(uri, 1), CancellationToken.None);
        await CreateSut().Handle(new MatchCacheChangedNotification(uri, 2), CancellationToken.None);
        await CreateSut().Handle(new MatchCacheChangedNotification(uri, 3), CancellationToken.None);
        await Task.WhenAny(sent.Task, Task.Delay(5000));

        _languageServer.Client.Received(1).SendRequest(WorkspaceNames.SemanticTokensRefresh);
    }

    [Fact]
    public async Task Handle_debounces_bursts_across_separate_handler_instances_sharing_the_debouncer()
    {
        // Reproduces the real production shape: MediatR constructs a new handler instance for
        // every notification (issue #156). Three separate instances here, each backed by the same
        // shared IRefreshDebouncer, should still collapse into a single refresh -- unlike the old
        // instance-field debounce, which could never see across handler instances at all.
        SetRefreshSupport(true);
        var sent = new TaskCompletionSource();
        _languageServer.Client.When(c => c.SendRequest(Arg.Any<string>())).Do(_ => sent.TrySetResult());

        var uri = DocumentUri.From("file:///f.feature");
        await new SemanticTokensRefreshHandler(_languageServer, _logger, _debouncer)
            .Handle(new MatchCacheChangedNotification(uri, 1), CancellationToken.None);
        await new SemanticTokensRefreshHandler(_languageServer, _logger, _debouncer)
            .Handle(new MatchCacheChangedNotification(uri, 2), CancellationToken.None);
        await new SemanticTokensRefreshHandler(_languageServer, _logger, _debouncer)
            .Handle(new MatchCacheChangedNotification(uri, 3), CancellationToken.None);
        await Task.WhenAny(sent.Task, Task.Delay(5000));

        // Give any incorrectly-surviving earlier-instance runs a chance to fire before asserting.
        await Task.Delay(200);

        _languageServer.Client.Received(1).SendRequest(WorkspaceNames.SemanticTokensRefresh);
    }
}
