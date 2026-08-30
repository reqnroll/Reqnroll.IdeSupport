using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Rename;

public class RenamePostApplyCoordinatorTests
{
    private readonly ILanguageServerFacade          _languageServer         = Substitute.For<ILanguageServerFacade>();
    private readonly IBindingMatchService           _matchService          = Substitute.For<IBindingMatchService>();
    private readonly IDocumentBufferService         _documentBuffer        = Substitute.For<IDocumentBufferService>();
    private readonly ICSharpBindingDiscoveryService _csharpDiscoveryService = Substitute.For<ICSharpBindingDiscoveryService>();
    private readonly ICSharpFileTextCache           _csharpFileTextCache   = Substitute.For<ICSharpFileTextCache>();
    private readonly IIdeSupportLogger              _logger                = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
    private static readonly DocumentUri CsUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    private RenamePostApplyCoordinator CreateSut(bool isVisualStudio) =>
        new(
            _languageServer,
            new ClientIdeContext(isVisualStudio ? "visualstudio" : "vscode"),
            _matchService,
            _documentBuffer,
            _csharpDiscoveryService,
            _csharpFileTextCache,
            _logger);

    private void SetupApplyEditRequest(bool applied)
    {
        var fakeReturns = Substitute.For<IResponseRouterReturns>();
        fakeReturns.Returning<ApplyWorkspaceEditResponse>(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ApplyWorkspaceEditResponse { Applied = applied }));
        _languageServer.SendRequest(Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>())
            .Returns(fakeReturns);
    }

    private static WorkspaceEditBuilder BuilderWithOneEdit(DocumentUri uri)
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: false);
        builder.Add(uri, new LspRange(new Position(0, 0), new Position(0, 1)), "x");
        return builder;
    }

    // ── PushEditIfVisualStudioAsync ──────────────────────────────────────

    [Fact]
    public async Task PushEditIfVisualStudioAsync_is_a_no_op_for_non_VS_clients_Async()
    {
        var sut = CreateSut(isVisualStudio: false);

        var result = await sut.PushEditIfVisualStudioAsync(BuilderWithOneEdit(FeatureUri), CancellationToken.None);

        result.Should().BeTrue();
        _languageServer.DidNotReceive().SendRequest(Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>());
    }

    [Fact]
    public async Task PushEditIfVisualStudioAsync_sends_workspace_applyEdit_for_VS_Async()
    {
        SetupApplyEditRequest(applied: true);
        var sut = CreateSut(isVisualStudio: true);

        await sut.PushEditIfVisualStudioAsync(BuilderWithOneEdit(FeatureUri), CancellationToken.None);

        _languageServer.Received(1).SendRequest("workspace/applyEdit", Arg.Any<ApplyWorkspaceEditParams>());
    }

    [Fact]
    public async Task PushEditIfVisualStudioAsync_returns_true_when_VS_applies_the_edit_Async()
    {
        SetupApplyEditRequest(applied: true);
        var sut = CreateSut(isVisualStudio: true);

        var result = await sut.PushEditIfVisualStudioAsync(BuilderWithOneEdit(FeatureUri), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task PushEditIfVisualStudioAsync_returns_false_when_VS_rejects_the_edit_Async()
    {
        SetupApplyEditRequest(applied: false);
        var sut = CreateSut(isVisualStudio: true);

        var result = await sut.PushEditIfVisualStudioAsync(BuilderWithOneEdit(FeatureUri), CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── InvalidateClosedFeatureCaches ────────────────────────────────────

    [Fact]
    public void InvalidateClosedFeatureCaches_invalidates_a_closed_feature_file()
    {
        DocumentBuffer? ignored;
        _documentBuffer.TryGet(FeatureUri, out ignored).Returns(false);
        var sut = CreateSut(isVisualStudio: false);

        sut.InvalidateClosedFeatureCaches(BuilderWithOneEdit(FeatureUri));

        _matchService.Received(1).InvalidateAllForDocument(FeatureUri.ToString());
    }

    [Fact]
    public void InvalidateClosedFeatureCaches_does_not_invalidate_an_open_feature_file()
    {
        var buf = new DocumentBuffer(FeatureUri, 1, "Feature: F\n");
        DocumentBuffer? outBuf;
        _documentBuffer.TryGet(FeatureUri, out outBuf).Returns(x => { x[1] = buf; return true; });
        var sut = CreateSut(isVisualStudio: false);

        sut.InvalidateClosedFeatureCaches(BuilderWithOneEdit(FeatureUri));

        _matchService.DidNotReceive().InvalidateAllForDocument(Arg.Any<string>());
    }

    [Fact]
    public void InvalidateClosedFeatureCaches_does_not_invalidate_a_non_feature_file()
    {
        DocumentBuffer? ignored;
        _documentBuffer.TryGet(Arg.Any<DocumentUri>(), out ignored).Returns(false);
        var sut = CreateSut(isVisualStudio: false);

        sut.InvalidateClosedFeatureCaches(BuilderWithOneEdit(CsUri));

        _matchService.DidNotReceive().InvalidateAllForDocument(Arg.Any<string>());
    }

    // ── RefreshCSharpRegistryAsync ────────────────────────────────────────

    [Fact]
    public async Task RefreshCSharpRegistryAsync_is_a_no_op_when_csFileUri_is_null_Async()
    {
        var sut = CreateSut(isVisualStudio: false);

        await sut.RefreshCSharpRegistryAsync(null, "class C {}", CancellationToken.None);

        await _csharpDiscoveryService.DidNotReceive().UpdateFromSourceAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshCSharpRegistryAsync_is_a_no_op_when_newCsText_is_null_Async()
    {
        var sut = CreateSut(isVisualStudio: false);

        await sut.RefreshCSharpRegistryAsync(CsUri, null, CancellationToken.None);

        await _csharpDiscoveryService.DidNotReceive().UpdateFromSourceAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshCSharpRegistryAsync_updates_the_discovery_service_and_text_cache_Async()
    {
        var sut = CreateSut(isVisualStudio: false);

        await sut.RefreshCSharpRegistryAsync(CsUri, "class C {}", CancellationToken.None);

        await _csharpDiscoveryService.Received(1).UpdateFromSourceAsync(CsUri, "class C {}", false, Arg.Any<CancellationToken>());
        _csharpFileTextCache.Received(1).Update(CsUri, "class C {}");
    }
}
