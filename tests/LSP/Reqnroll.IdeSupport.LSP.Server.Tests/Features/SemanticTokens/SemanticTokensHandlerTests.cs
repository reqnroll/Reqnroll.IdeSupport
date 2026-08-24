using System.Collections.Immutable;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.SemanticTokens;

public class SemanticTokensHandlerTests
{
    private readonly ISemanticTokenService _tokenService = Substitute.For<ISemanticTokenService>();
    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
    private static readonly DocumentUri NonFeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.cs");

    private SemanticTokensHandler CreateSut() =>
        new(_tokenService, _bufferService, _logger);

    private void SetupBufferVersion(DocumentUri uri, int version)
    {
        var buf = new DocumentBuffer(uri, version, "");
        DocumentBuffer? ignored;
        _bufferService.TryGet(uri, out ignored).Returns(x =>
        {
            x[1] = buf;
            return true;
        });
    }

    // ── Full ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_Full_returns_empty_for_non_feature_file()
    {
        var sut = CreateSut();
        var request = new SemanticTokensParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = NonFeatureUri }
        };
        var result = await sut.HandleAsync(request, CancellationToken.None);
        result.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_Full_delegates_to_token_service()
    {
        SetupBufferVersion(FeatureUri, 7);
        var expected = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens { Data = ImmutableArray.Create(0, 0, 5, 0, 0) };
        _tokenService.GetSemanticTokensAsync(FeatureUri, 7, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(expected));

        var sut = CreateSut();
        var request = new SemanticTokensParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri }
        };
        var result = await sut.HandleAsync(request, CancellationToken.None);
        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Handle_Full_returns_empty_when_service_returns_null()
    {
        SetupBufferVersion(FeatureUri, 1);
        _tokenService.GetSemanticTokensAsync(FeatureUri, 1, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(null));

        var sut = CreateSut();
        var request = new SemanticTokensParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri }
        };
        var result = await sut.HandleAsync(request, CancellationToken.None);
        result.Data.Should().BeEmpty();
    }

    // ── Range ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_Range_returns_empty_for_non_feature_file()
    {
        var sut = CreateSut();
        var request = new SemanticTokensRangeParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = NonFeatureUri },
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 1, 0)
        };
        var result = await sut.HandleAsync(request, CancellationToken.None);
        result.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_Range_delegates_to_the_range_scoped_token_service_method()
    {
        SetupBufferVersion(FeatureUri, 4);
        var expected = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens { Data = ImmutableArray.Create(0, 0, 7, 0, 0) };
        var range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 1, 0);
        _tokenService.GetSemanticTokensForRangeAsync(FeatureUri, 4, range, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(expected));

        var sut = CreateSut();
        var request = new SemanticTokensRangeParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri },
            Range = range
        };
        var result = await sut.HandleAsync(request, CancellationToken.None);

        result.Should().BeSameAs(expected);
        await _tokenService.DidNotReceive().GetSemanticTokensAsync(Arg.Any<DocumentUri>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Delta ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_Delta_returns_empty_for_non_feature_file()
    {
        var sut = CreateSut();
        var request = new SemanticTokensDeltaParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = NonFeatureUri },
            PreviousResultId = "prev"
        };
        var result = await sut.HandleAsync(request, CancellationToken.None);
        result!.Full!.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_Delta_wraps_full_tokens_in_SemanticTokensFullOrDelta()
    {
        SetupBufferVersion(FeatureUri, 2);
        var tokens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens { Data = ImmutableArray.Create(1, 0, 5, 0, 0) };
        _tokenService.GetSemanticTokensAsync(FeatureUri, 2, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(tokens));

        var sut = CreateSut();
        var request = new SemanticTokensDeltaParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri },
            PreviousResultId = "prev"
        };
        var result = await sut.HandleAsync(request, CancellationToken.None);
        result.Should().NotBeNull();
        result!.IsFull.Should().BeTrue();
        result.Full.Should().BeSameAs(tokens);
    }
}
