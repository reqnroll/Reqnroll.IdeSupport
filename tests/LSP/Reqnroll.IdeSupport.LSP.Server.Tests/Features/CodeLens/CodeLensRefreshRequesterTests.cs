using System.Diagnostics;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeLens;

/// <summary>
/// Direct coverage for <see cref="CodeLensRefreshRequester"/> (previously only exercised
/// indirectly via <c>BindingRegistryChangedHandlerTests</c>): the VS/non-VS client branching and
/// the <c>isFullReplacement</c>/<c>projectName</c> pass-through.
/// </summary>
public class CodeLensRefreshRequesterTests
{
    private readonly ILanguageServerFacade _languageServer = Substitute.For<ILanguageServerFacade>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    [Fact]
    public async Task RequestRefreshAsync_sends_reqnroll_refreshCodeLens_for_visual_studio()
    {
        await CodeLensRefreshRequester.RequestRefreshAsync(
            _languageServer, new ClientIdeContext("visualstudio"), _logger, "MyProject", isFullReplacement: true);

        _languageServer.Received(1).SendNotification(
            LspMethodNames.ReqnrollRefreshCodeLens,
            Arg.Is<RefreshCodeLensParams>(p => p.ProjectName == "MyProject" && p.IsFullReplacement));
    }

    [Fact]
    public async Task RequestRefreshAsync_passes_isFullReplacement_false_through_for_visual_studio()
    {
        await CodeLensRefreshRequester.RequestRefreshAsync(
            _languageServer, new ClientIdeContext("visualstudio"), _logger, "MyProject", isFullReplacement: false);

        _languageServer.Received(1).SendNotification(
            LspMethodNames.ReqnrollRefreshCodeLens,
            Arg.Is<RefreshCodeLensParams>(p => !p.IsFullReplacement));
    }

    [Fact]
    public async Task RequestRefreshAsync_defaults_isFullReplacement_to_false_when_not_specified()
    {
        await CodeLensRefreshRequester.RequestRefreshAsync(
            _languageServer, new ClientIdeContext("visualstudio"), _logger, "MyProject");

        _languageServer.Received(1).SendNotification(
            LspMethodNames.ReqnrollRefreshCodeLens,
            Arg.Is<RefreshCodeLensParams>(p => !p.IsFullReplacement));
    }

    [Fact]
    public async Task RequestRefreshAsync_sends_workspace_codeLens_refresh_for_non_visual_studio_clients()
    {
        await CodeLensRefreshRequester.RequestRefreshAsync(
            _languageServer, new ClientIdeContext("vscode"), _logger, "MyProject", isFullReplacement: true);

        _languageServer.Client.Received(1).SendRequest(LspMethodNames.WorkspaceCodeLensRefresh);
        _languageServer.DidNotReceiveWithAnyArgs().SendNotification(default!, default(object)!);
    }

    [Fact]
    public async Task RequestRefreshAsync_sends_workspace_codeLens_refresh_when_ide_is_null()
    {
        await CodeLensRefreshRequester.RequestRefreshAsync(
            _languageServer, new ClientIdeContext(null), _logger, "MyProject");

        _languageServer.Client.Received(1).SendRequest(LspMethodNames.WorkspaceCodeLensRefresh);
    }

    [Fact]
    public async Task RequestRefreshAsync_swallows_an_exception_from_SendNotification_for_visual_studio()
    {
        _languageServer.When(l => l.SendNotification(Arg.Any<string>(), Arg.Any<object>()))
            .Do(_ => throw new InvalidOperationException("boom"));

        var act = () => CodeLensRefreshRequester.RequestRefreshAsync(
            _languageServer, new ClientIdeContext("visualstudio"), _logger, "MyProject");

        await act.Should().NotThrowAsync();
        _logger.Received(1).Log(Arg.Is<LogMessage>(m => m.Level == TraceLevel.Warning && m.Message.Contains("boom")));
    }

    [Fact]
    public async Task RequestRefreshAsync_swallows_an_exception_from_SendRequest_for_non_visual_studio()
    {
        _languageServer.Client.SendRequest(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("boom"));

        var act = () => CodeLensRefreshRequester.RequestRefreshAsync(
            _languageServer, new ClientIdeContext("vscode"), _logger, "MyProject");

        await act.Should().NotThrowAsync();
        _logger.Received(1).Log(Arg.Is<LogMessage>(m => m.Level == TraceLevel.Warning && m.Message.Contains("boom")));
    }

    // Issue #471: the debouncer can only collapse a refresh already in flight if the request
    // actually observes the token it was given -- passing CancellationToken.None here (as this
    // used to, silently) makes a superseding trigger unable to cancel a slow send already
    // dispatched to the client (see IRefreshDebouncer.Schedule's remarks).
    [Fact]
    public async Task RequestRefreshAsync_passes_the_given_cancellationToken_to_the_request_for_non_visual_studio()
    {
        var fakeReturns = Substitute.For<IResponseRouterReturns>();
        _languageServer.Client.SendRequest(Arg.Any<string>()).Returns(fakeReturns);
        using var cts = new CancellationTokenSource();

        await CodeLensRefreshRequester.RequestRefreshAsync(
            _languageServer, new ClientIdeContext("vscode"), _logger, "MyProject", cancellationToken: cts.Token);

        await fakeReturns.Received(1).ReturningVoid(cts.Token);
    }
}
