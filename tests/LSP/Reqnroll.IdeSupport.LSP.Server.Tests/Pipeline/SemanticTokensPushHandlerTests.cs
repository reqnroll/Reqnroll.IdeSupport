using System.Collections.Immutable;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

public class SemanticTokensPushHandlerTests
{
    private readonly ILanguageServerFacade _facade = Substitute.For<ILanguageServerFacade>();
    private readonly ISemanticTokensService _tokenService = Substitute.For<ISemanticTokensService>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private SemanticTokensPushHandler CreateSut(string? ide) =>
        new(_facade, _tokenService, new ClientIdeContext(ide), _logger);

    private void SetupTokens(SemanticTokens? tokens) =>
        _tokenService.GetSemanticTokensAsync(FeatureUri, 1, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(tokens));

    [Fact]
    public async Task Pushes_reqnroll_semanticTokens_notification_for_visual_studio()
    {
        SetupTokens(new SemanticTokens { Data = ImmutableArray.Create(0, 0, 8, 0, 0) });
        var sut = CreateSut("visualstudio");

        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        _facade.Received(1).SendNotification("reqnroll/semanticTokens", Arg.Any<PublishSemanticTokensParams>());
    }

    [Theory]
    [InlineData("vscode")]
    [InlineData("rider")]
    [InlineData(null)]
    public async Task Does_nothing_for_non_visual_studio_clients(string? ide)
    {
        var sut = CreateSut(ide);

        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        await _tokenService.DidNotReceive().GetSemanticTokensAsync(
            Arg.Any<DocumentUri>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _facade.DidNotReceive().SendNotification(Arg.Any<string>(), Arg.Any<PublishSemanticTokensParams>());
    }

    [Fact]
    public async Task Does_not_push_when_no_tokens_are_available()
    {
        SetupTokens(null);
        var sut = CreateSut("visualstudio");

        await sut.Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        _facade.DidNotReceive().SendNotification(Arg.Any<string>(), Arg.Any<PublishSemanticTokensParams>());
    }
}
