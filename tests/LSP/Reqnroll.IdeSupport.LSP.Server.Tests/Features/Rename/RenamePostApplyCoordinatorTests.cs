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

    /// <summary>Stages a commit for <paramref name="renameUri"/> then reports the client's outcome for it.</summary>
    private static Task StageThenCompleteAsync(
        RenamePostApplyCoordinator sut,
        DocumentUri                renameUri,
        WorkspaceEditBuilder       builder,
        bool                       applied,
        DocumentUri?               csFileUri = null,
        string?                    newCsText = null)
    {
        sut.StagePendingCommit(renameUri, builder, csFileUri, newCsText);
        return sut.CompletePendingCommitAsync(renameUri, applied, CancellationToken.None);
    }

    // ── Confirmed apply commits the staged updates ───────────────────────

    [Fact]
    public async Task Confirmed_apply_invalidates_a_closed_feature_file_Async()
    {
        DocumentBuffer? ignored;
        _documentBuffer.TryGet(FeatureUri, out ignored).Returns(false);
        var sut = CreateSut(isVisualStudio: false);

        await StageThenCompleteAsync(sut, FeatureUri, BuilderWithOneEdit(FeatureUri), applied: true);

        _matchService.Received(1).InvalidateAllForDocument(FeatureUri.ToString());
    }

    [Fact]
    public async Task Confirmed_apply_does_not_invalidate_an_open_feature_file_Async()
    {
        var buf = new DocumentBuffer(FeatureUri, 1, "Feature: F\n");
        DocumentBuffer? outBuf;
        _documentBuffer.TryGet(FeatureUri, out outBuf).Returns(x => { x[1] = buf; return true; });
        var sut = CreateSut(isVisualStudio: false);

        await StageThenCompleteAsync(sut, FeatureUri, BuilderWithOneEdit(FeatureUri), applied: true);

        _matchService.DidNotReceive().InvalidateAllForDocument(Arg.Any<string>());
    }

    [Fact]
    public async Task Confirmed_apply_does_not_invalidate_a_non_feature_file_Async()
    {
        DocumentBuffer? ignored;
        _documentBuffer.TryGet(Arg.Any<DocumentUri>(), out ignored).Returns(false);
        var sut = CreateSut(isVisualStudio: false);

        await StageThenCompleteAsync(sut, CsUri, BuilderWithOneEdit(CsUri), applied: true);

        _matchService.DidNotReceive().InvalidateAllForDocument(Arg.Any<string>());
    }

    [Fact]
    public async Task Confirmed_apply_updates_the_discovery_service_and_text_cache_Async()
    {
        var sut = CreateSut(isVisualStudio: false);

        await StageThenCompleteAsync(
            sut, FeatureUri, BuilderWithOneEdit(FeatureUri), applied: true,
            csFileUri: CsUri, newCsText: "class C {}");

        await _csharpDiscoveryService.Received(1).UpdateFromSourceAsync(CsUri, "class C {}", false, Arg.Any<CancellationToken>());
        _csharpFileTextCache.Received(1).Update(CsUri, "class C {}");
    }

    [Fact]
    public async Task Confirmed_apply_is_a_no_op_for_the_cs_registry_when_csFileUri_is_null_Async()
    {
        var sut = CreateSut(isVisualStudio: false);

        await StageThenCompleteAsync(
            sut, FeatureUri, BuilderWithOneEdit(FeatureUri), applied: true,
            csFileUri: null, newCsText: "class C {}");

        await _csharpDiscoveryService.DidNotReceive().UpdateFromSourceAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirmed_apply_is_a_no_op_for_the_cs_registry_when_newCsText_is_null_Async()
    {
        var sut = CreateSut(isVisualStudio: false);

        await StageThenCompleteAsync(
            sut, FeatureUri, BuilderWithOneEdit(FeatureUri), applied: true,
            csFileUri: CsUri, newCsText: null);

        await _csharpDiscoveryService.DidNotReceive().UpdateFromSourceAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── A discarded edit must leave every cache untouched (issue #670) ────

    [Fact]
    public async Task Discarded_edit_does_not_touch_the_cs_registry_or_text_cache_Async()
    {
        var sut = CreateSut(isVisualStudio: false);

        await StageThenCompleteAsync(
            sut, FeatureUri, BuilderWithOneEdit(FeatureUri), applied: false,
            csFileUri: CsUri, newCsText: "class C {}");

        await _csharpDiscoveryService.DidNotReceive().UpdateFromSourceAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        _csharpFileTextCache.DidNotReceive().Update(Arg.Any<DocumentUri>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Discarded_edit_does_not_invalidate_a_closed_feature_file_Async()
    {
        DocumentBuffer? ignored;
        _documentBuffer.TryGet(FeatureUri, out ignored).Returns(false);
        var sut = CreateSut(isVisualStudio: false);

        await StageThenCompleteAsync(sut, FeatureUri, BuilderWithOneEdit(FeatureUri), applied: false);

        _matchService.DidNotReceive().InvalidateAllForDocument(Arg.Any<string>());
    }

    // ── Staging lifecycle ────────────────────────────────────────────────

    [Fact]
    public async Task Confirmation_with_nothing_staged_is_a_no_op_Async()
    {
        var sut = CreateSut(isVisualStudio: false);

        await sut.CompletePendingCommitAsync(FeatureUri, applied: true, CancellationToken.None);

        _matchService.DidNotReceive().InvalidateAllForDocument(Arg.Any<string>());
        await _csharpDiscoveryService.DidNotReceive().UpdateFromSourceAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_repeated_confirmation_does_not_commit_twice_Async()
    {
        DocumentBuffer? ignored;
        _documentBuffer.TryGet(FeatureUri, out ignored).Returns(false);
        var sut = CreateSut(isVisualStudio: false);

        await StageThenCompleteAsync(sut, FeatureUri, BuilderWithOneEdit(FeatureUri), applied: true);
        await sut.CompletePendingCommitAsync(FeatureUri, applied: true, CancellationToken.None);

        _matchService.Received(1).InvalidateAllForDocument(FeatureUri.ToString());
    }

    // ── Visual Studio's post-response push ───────────────────────────────

    [Fact]
    public void SchedulePostResponseApply_is_a_no_op_for_non_VS_clients()
    {
        var sut = CreateSut(isVisualStudio: false);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), null, null);

        sut.SchedulePostResponseApply(FeatureUri, BuilderWithOneEdit(FeatureUri));

        _languageServer.DidNotReceive().SendRequest(Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>());
    }

    [Fact]
    public async Task Push_sends_workspace_applyEdit_for_VS_Async()
    {
        SetupApplyEditRequest(applied: true);
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), null, null);

        await sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        _languageServer.Received(1).SendRequest("workspace/applyEdit", Arg.Any<ApplyWorkspaceEditParams>());
    }

    [Fact]
    public async Task Push_commits_the_staged_updates_when_VS_applies_the_edit_Async()
    {
        SetupApplyEditRequest(applied: true);
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), CsUri, "class C {}");

        await sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        await _csharpDiscoveryService.Received(1).UpdateFromSourceAsync(CsUri, "class C {}", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Push_drops_the_staged_updates_when_VS_rejects_the_edit_Async()
    {
        SetupApplyEditRequest(applied: false);
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), CsUri, "class C {}");

        await sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        await _csharpDiscoveryService.DidNotReceive().UpdateFromSourceAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        _csharpFileTextCache.DidNotReceive().Update(Arg.Any<DocumentUri>(), Arg.Any<string>());
    }

    // ── Issue #671 (R2): the VS push is the emission that actually applies the edit in Visual
    //    Studio, so it is the one that has to carry a checkable document version. ──────────────

    [Fact]
    public async Task Push_stamps_the_open_documents_version_on_the_applyEdit_params_Async()
    {
        var buf = new DocumentBuffer(FeatureUri, 7, "Feature: F\n");
        DocumentBuffer? outBuf;
        _documentBuffer.TryGet(FeatureUri, out outBuf).Returns(x => { x[1] = buf; return true; });
        SetupApplyEditRequest(applied: true);
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), null, null);

        await sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        _languageServer.Received(1).SendRequest(
            "workspace/applyEdit",
            Arg.Is<ApplyWorkspaceEditParams>(p =>
                p.Edit.DocumentChanges!.Single().TextDocumentEdit!.TextDocument.Version == 7));
    }

    [Fact]
    public async Task Push_stamps_a_null_version_for_a_document_VS_has_not_opened_Async()
    {
        DocumentBuffer? ignored;
        _documentBuffer.TryGet(Arg.Any<DocumentUri>(), out ignored).Returns(false);
        SetupApplyEditRequest(applied: true);
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), null, null);

        await sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        _languageServer.Received(1).SendRequest(
            "workspace/applyEdit",
            Arg.Is<ApplyWorkspaceEditParams>(p =>
                p.Edit.DocumentChanges!.Single().TextDocumentEdit!.TextDocument.Version == null));
    }

    [Fact]
    public async Task Push_drops_the_staged_updates_when_the_applyEdit_request_throws_Async()
    {
        _languageServer.SendRequest(Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>())
            .Returns(_ => throw new InvalidOperationException("pipe closed"));
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), CsUri, "class C {}");

        await sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        await _csharpDiscoveryService.DidNotReceive().UpdateFromSourceAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
