using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Concurrency;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

/// <summary>
/// Covers <see cref="CodeLensRefreshHandler"/>'s <c>isFullReplacement: false</c> call site to
/// <see cref="CodeLensRefreshRequester.RequestRefreshAsync"/> (from PR #342), so a <c>.feature</c>
/// file edit's own code-lens refresh always requests an incremental refresh — unlike
/// <c>BindingRegistryChangedHandler</c>, which requests a full replacement for a full binding
/// registry rebuild. Mirrors sibling call-site coverage in
/// <c>BindingRegistryChangedHandlerTests</c>/<c>CodeLensRefreshInterceptorTests</c>.
/// </summary>
public class CodeLensRefreshHandlerTests : IDisposable
{
    private readonly ILanguageServerFacade _languageServer = Substitute.For<ILanguageServerFacade>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly RefreshDebouncer _debouncer;

    public CodeLensRefreshHandlerTests() => _debouncer = new RefreshDebouncer(_logger);

    public void Dispose() => _debouncer.Dispose();

    private CodeLensRefreshHandler CreateSut(ClientIdeContext clientIde) =>
        new(_languageServer, clientIde, _logger, _debouncer);

    [Fact]
    public async Task Handle_sends_reqnroll_refreshCodeLens_with_isFullReplacement_false_for_visual_studio()
    {
        var sent = new TaskCompletionSource();
        _languageServer.When(l => l.SendNotification(Arg.Any<string>(), Arg.Any<object>()))
            .Do(_ => sent.TrySetResult());

        await CreateSut(new ClientIdeContext("visualstudio"))
            .Handle(new MatchCacheChangedNotification(DocumentUri.From("file:///f.feature"), 1), CancellationToken.None);
        await Task.WhenAny(sent.Task, Task.Delay(5000));

        _languageServer.Received(1).SendNotification(
            LspMethodNames.ReqnrollRefreshCodeLens,
            Arg.Is<RefreshCodeLensParams>(p => !p.IsFullReplacement));
    }

    [Fact]
    public async Task Handle_sends_workspace_codeLens_refresh_for_non_visual_studio_clients()
    {
        var sent = new TaskCompletionSource();
        _languageServer.Client.When(c => c.SendRequest(Arg.Any<string>())).Do(_ => sent.TrySetResult());

        await CreateSut(new ClientIdeContext("vscode"))
            .Handle(new MatchCacheChangedNotification(DocumentUri.From("file:///f.feature"), 1), CancellationToken.None);
        await Task.WhenAny(sent.Task, Task.Delay(5000));

        _languageServer.Client.Received(1).SendRequest(LspMethodNames.WorkspaceCodeLensRefresh);
        _languageServer.DidNotReceiveWithAnyArgs().SendNotification(default!, default(object)!);
    }

    // Issue #471: passing CancellationToken.None here (as this used to, silently) means a
    // superseding MatchCacheChangedNotification can never cancel a refresh already dispatched to
    // the client -- only one still waiting out the debounce delay. See
    // IRefreshDebouncer.Schedule's remarks.
    [Fact]
    public async Task Handle_passes_the_debouncer_supplied_token_to_the_refresh_request_for_non_visual_studio()
    {
        var fakeReturns = Substitute.For<IResponseRouterReturns>();
        _languageServer.Client.SendRequest(Arg.Any<string>()).Returns(fakeReturns);

        await CreateSut(new ClientIdeContext("vscode"))
            .Handle(new MatchCacheChangedNotification(DocumentUri.From("file:///f.feature"), 1), CancellationToken.None);
        await Task.Delay(700);

        await fakeReturns.Received(1).ReturningVoid(Arg.Is<CancellationToken>(t => t != CancellationToken.None));
    }

    [Fact]
    public async Task Handle_debounces_bursts_into_a_single_refresh()
    {
        var sent = new TaskCompletionSource();
        _languageServer.When(l => l.SendNotification(Arg.Any<string>(), Arg.Any<object>()))
            .Do(_ => sent.TrySetResult());

        var uri = DocumentUri.From("file:///f.feature");
        var clientIde = new ClientIdeContext("visualstudio");
        await CreateSut(clientIde).Handle(new MatchCacheChangedNotification(uri, 1), CancellationToken.None);
        await CreateSut(clientIde).Handle(new MatchCacheChangedNotification(uri, 2), CancellationToken.None);
        await CreateSut(clientIde).Handle(new MatchCacheChangedNotification(uri, 3), CancellationToken.None);
        await Task.WhenAny(sent.Task, Task.Delay(5000));

        _languageServer.Received(1).SendNotification(
            LspMethodNames.ReqnrollRefreshCodeLens,
            Arg.Is<RefreshCodeLensParams>(p => !p.IsFullReplacement));
    }
}
