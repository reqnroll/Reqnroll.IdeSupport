using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Configuration.Model;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
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
    private readonly IDocumentGenerations           _documentGenerations   = new DocumentGenerations();
    private readonly IGherkinDocumentTaggerService  _taggerService         = Substitute.For<IGherkinDocumentTaggerService>();

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
            _logger,
            _documentGenerations,
            _taggerService,
            new FileSystemForIDE());

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

    // ── A discarded edit must leave every cache untouched ────

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

    // ── The VS push is the emission that actually applies the edit in Visual
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

    // ── A push VS does not apply is reported to the user, since the rename
    //    response has already gone and cannot carry the failure. ─────────────

    private void SetupApplyEditResponse(ApplyWorkspaceEditResponse? response)
    {
        var fakeReturns = Substitute.For<IResponseRouterReturns>();
        fakeReturns.Returning<ApplyWorkspaceEditResponse>(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response!));
        _languageServer.SendRequest(Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>())
            .Returns(fakeReturns);
    }

    [Fact]
    public async Task Push_reports_renameNotApplied_with_VS_reason_when_VS_rejects_the_edit_Async()
    {
        SetupApplyEditResponse(new ApplyWorkspaceEditResponse { Applied = false, FailureReason = "File is read-only" });
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), null, null);

        await sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        _languageServer.Received(1).SendNotification(
            CustomLspMethodNames.ReqnrollRenameNotApplied,
            Arg.Is<RenameNotAppliedParams>(p =>
                p.Uri == FeatureUri &&
                p.Message.StartsWith("The step was not renamed") &&
                p.Message.Contains("(File is read-only)")));
    }

    [Fact]
    public async Task Push_reports_renameNotApplied_when_VS_returns_no_response_Async()
    {
        SetupApplyEditResponse(null);
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), null, null);

        await sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        _languageServer.Received(1).SendNotification(
            CustomLspMethodNames.ReqnrollRenameNotApplied,
            Arg.Is<RenameNotAppliedParams>(p => p.Message == RenamePostApplyCoordinator.BuildRejectedMessage(null)));
    }

    [Fact]
    public async Task Push_reports_renameNotApplied_when_the_applyEdit_request_throws_Async()
    {
        _languageServer.SendRequest(Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>())
            .Returns(_ => throw new InvalidOperationException("pipe closed"));
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), null, null);

        await sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        _languageServer.Received(1).SendNotification(
            CustomLspMethodNames.ReqnrollRenameNotApplied,
            Arg.Is<RenameNotAppliedParams>(p => p.Uri == FeatureUri && p.Message.Contains("(pipe closed)")));
    }

    [Fact]
    public async Task Push_does_not_report_renameNotApplied_when_VS_applies_the_edit_Async()
    {
        SetupApplyEditRequest(applied: true);
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), null, null);

        await sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        _languageServer.DidNotReceive().SendNotification(
            CustomLspMethodNames.ReqnrollRenameNotApplied, Arg.Any<RenameNotAppliedParams>());
    }

    [Fact]
    public async Task Push_still_drops_the_staged_updates_when_reporting_renameNotApplied_throws_Async()
    {
        SetupApplyEditRequest(applied: false);
        _languageServer
            .When(s => s.SendNotification(CustomLspMethodNames.ReqnrollRenameNotApplied, Arg.Any<RenameNotAppliedParams>()))
            .Do(_ => throw new InvalidOperationException("pipe closed"));
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), CsUri, "class C {}");

        var push = () => sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        await push.Should().NotThrowAsync();
        await _csharpDiscoveryService.DidNotReceive().UpdateFromSourceAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Push_does_not_report_renameNotApplied_when_VS_applied_but_the_cache_commit_throws_Async()
    {
        SetupApplyEditRequest(applied: true);
        _csharpDiscoveryService
            .UpdateFromSourceAsync(Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("registry gone"));
        var sut = CreateSut(isVisualStudio: true);
        sut.StagePendingCommit(FeatureUri, BuilderWithOneEdit(FeatureUri), CsUri, "class C {}");

        var push = () => sut.PushAndCompleteAsync(FeatureUri, BuilderWithOneEdit(FeatureUri));

        await push.Should().NotThrowAsync();
        _languageServer.DidNotReceive().SendNotification(
            CustomLspMethodNames.ReqnrollRenameNotApplied, Arg.Any<RenameNotAppliedParams>());
    }

    [Fact]
    public void Rejected_message_omits_the_parenthesis_when_VS_gives_no_reason()
    {
        RenamePostApplyCoordinator.BuildRejectedMessage("  ").Should().Be(
            "The step was not renamed: Visual Studio could not apply the edit. " +
            "Check that the files it changes are not read-only or locked, then rename it again.");
    }

    // ── Document generation ──────────────────────────────────────────────

    [Fact]
    public async Task Confirmed_apply_bumps_a_closed_feature_files_generation_before_invalidating_it_Async()
    {
        DocumentBuffer? ignored;
        _documentBuffer.TryGet(FeatureUri, out ignored).Returns(false);
        long generationAtInvalidate = -1;
        _matchService.When(m => m.InvalidateAllForDocument(FeatureUri.ToString()))
                     .Do(_ => generationAtInvalidate = _documentGenerations.Current(FeatureUri.ToString()));

        await StageThenCompleteAsync(CreateSut(isVisualStudio: false), FeatureUri, BuilderWithOneEdit(FeatureUri), applied: true);

        _documentGenerations.Current(FeatureUri.ToString()).Should().Be(1);
        generationAtInvalidate.Should().Be(1);
    }

    // An open file's edit arrives as a didChange, which bumps the generation itself.
    [Fact]
    public async Task Confirmed_apply_does_not_bump_an_open_feature_files_generation_Async()
    {
        var buf = new DocumentBuffer(FeatureUri, 1, "Feature: F\n");
        DocumentBuffer? outBuf;
        _documentBuffer.TryGet(FeatureUri, out outBuf).Returns(x => { x[1] = buf; return true; });

        await StageThenCompleteAsync(CreateSut(isVisualStudio: false), FeatureUri, BuilderWithOneEdit(FeatureUri), applied: true);

        _documentGenerations.Current(FeatureUri.ToString()).Should().Be(0);
    }

    [Fact]
    public async Task Rejected_apply_does_not_bump_any_generation_Async()
    {
        DocumentBuffer? ignored;
        _documentBuffer.TryGet(FeatureUri, out ignored).Returns(false);

        await StageThenCompleteAsync(CreateSut(isVisualStudio: false), FeatureUri, BuilderWithOneEdit(FeatureUri), applied: false);

        _documentGenerations.Current(FeatureUri.ToString()).Should().Be(0);
    }

    // ── Closed feature files are re-derived from their post-rename text ──

    private const string BeforeRename = "Feature: F\n\nScenario: One\n    Given old step\n";

    // Replaces "old" (line 3, characters 10-13) with a longer word, so applying the edit a second time
    // to the already-renamed text would visibly corrupt it.
    private static WorkspaceEditBuilder RenameOldToBrandNew(DocumentUri uri)
    {
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: false);
        builder.Add(uri, new LspRange(new Position(3, 10), new Position(3, 13)), "brand new");
        return builder;
    }

    private const string AfterRename = "Feature: F\n\nScenario: One\n    Given brand new step\n";

    /// <summary>Creates a closed feature file with <see cref="BeforeRename"/> on disk in a fresh temp directory.</summary>
    private sealed class ClosedFeatureFile : IDisposable
    {
        private readonly DirectoryInfo _dir =
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rnr-rename-" + Guid.NewGuid().ToString("N")));

        public ClosedFeatureFile()
        {
            FilePath = Path.Combine(_dir.FullName, "closed.feature");
            File.WriteAllText(FilePath, BeforeRename);
            Uri = DocumentUri.FromFileSystemPath(FilePath);
        }

        public string FilePath { get; }
        public DocumentUri Uri { get; }
        public string Folder => _dir.FullName;

        public void Dispose()
        {
            try { _dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    private void MarkClosed(DocumentUri uri)
    {
        DocumentBuffer? ignored;
        _documentBuffer.TryGet(uri, out ignored).Returns(false);
    }

    [Fact]
    public async Task Confirmed_apply_rederives_a_closed_feature_file_from_its_post_rename_text_Async()
    {
        using var file = new ClosedFeatureFile();
        MarkClosed(file.Uri);

        await StageThenCompleteAsync(CreateSut(isVisualStudio: false), file.Uri, RenameOldToBrandNew(file.Uri), applied: true);

        _matchService.Received(1).InvalidateAllForDocument(file.Uri.ToString());
        await _taggerService.Received(1).ScanClosedFileForAllOwnersAsync(file.Uri, AfterRename, 1);
    }

    // A client may save the edited file before it confirms the rename. The post-rename text is computed
    // when the rename is staged, so the edit is never applied to text that already contains it.
    [Fact]
    public async Task Confirmed_apply_uses_the_text_staged_before_the_client_saved_the_edit_Async()
    {
        using var file = new ClosedFeatureFile();
        MarkClosed(file.Uri);
        var sut = CreateSut(isVisualStudio: false);

        sut.StagePendingCommit(file.Uri, RenameOldToBrandNew(file.Uri), null, null);
        await File.WriteAllTextAsync(file.FilePath, AfterRename); // the client applied and saved the rename
        await sut.CompletePendingCommitAsync(file.Uri, applied: true, CancellationToken.None);

        await _taggerService.Received(1).ScanClosedFileForAllOwnersAsync(file.Uri, AfterRename, Arg.Any<long>());
    }

    [Fact]
    public async Task Confirmed_apply_refreshes_the_bindings_before_rederiving_closed_feature_files_Async()
    {
        using var file = new ClosedFeatureFile();
        MarkClosed(file.Uri);

        await StageThenCompleteAsync(
            CreateSut(isVisualStudio: false), file.Uri, RenameOldToBrandNew(file.Uri), applied: true,
            csFileUri: CsUri, newCsText: "class C {}");

        Received.InOrder(() =>
        {
            _ = _csharpDiscoveryService.UpdateFromSourceAsync(CsUri, "class C {}", false, Arg.Any<CancellationToken>());
            _ = _taggerService.ScanClosedFileForAllOwnersAsync(file.Uri, AfterRename, Arg.Any<long>());
        });
    }

    [Fact]
    public async Task Confirmed_apply_only_invalidates_a_closed_feature_file_it_could_not_read_Async()
    {
        MarkClosed(FeatureUri); // /workspace/test.feature does not exist on disk

        await StageThenCompleteAsync(CreateSut(isVisualStudio: false), FeatureUri, BuilderWithOneEdit(FeatureUri), applied: true);

        _matchService.Received(1).InvalidateAllForDocument(FeatureUri.ToString());
        await _taggerService.DidNotReceiveWithAnyArgs().ScanClosedFileForAllOwnersAsync(default!, default!, default);
    }

    [Fact]
    public async Task Confirmed_apply_only_invalidates_a_closed_feature_file_its_edits_do_not_fit_Async()
    {
        using var file = new ClosedFeatureFile();
        MarkClosed(file.Uri);
        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: false);
        builder.Add(file.Uri, new LspRange(new Position(40, 0), new Position(40, 3)), "x"); // no line 40

        await StageThenCompleteAsync(CreateSut(isVisualStudio: false), file.Uri, builder, applied: true);

        _matchService.Received(1).InvalidateAllForDocument(file.Uri.ToString());
        await _taggerService.DidNotReceiveWithAnyArgs().ScanClosedFileForAllOwnersAsync(default!, default!, default);
    }

    [Fact]
    public async Task Confirmed_apply_does_not_rederive_an_open_feature_file_Async()
    {
        using var file = new ClosedFeatureFile();
        var buf = new DocumentBuffer(file.Uri, 1, BeforeRename);
        DocumentBuffer? outBuf;
        _documentBuffer.TryGet(file.Uri, out outBuf).Returns(x => { x[1] = buf; return true; });

        await StageThenCompleteAsync(CreateSut(isVisualStudio: false), file.Uri, RenameOldToBrandNew(file.Uri), applied: true);

        await _taggerService.DidNotReceiveWithAnyArgs().ScanClosedFileForAllOwnersAsync(default!, default!, default);
    }

    [Fact]
    public async Task Rejected_apply_does_not_rederive_a_closed_feature_file_Async()
    {
        using var file = new ClosedFeatureFile();
        MarkClosed(file.Uri);

        await StageThenCompleteAsync(CreateSut(isVisualStudio: false), file.Uri, RenameOldToBrandNew(file.Uri), applied: false);

        await _taggerService.DidNotReceiveWithAnyArgs().ScanClosedFileForAllOwnersAsync(default!, default!, default);
    }

    // End to end through the real tagger and match store: the closed file stays in the cache, and its
    // match set describes the renamed text, not the text on disk before the rename.
    [Fact]
    public async Task Confirmed_apply_leaves_a_closed_feature_files_match_set_describing_the_renamed_text_Async()
    {
        using var file = new ClosedFeatureFile();
        var project = new LspReqnrollProject(
            new ReqnrollProjectLoadedParams
            {
                WorkspaceFolder        = file.Folder,
                ProjectFile            = Path.Combine(file.Folder, "My.csproj"),
                ProjectFolder          = file.Folder,
                OutputAssemblyPath     = Path.Combine(file.Folder, "bin", "My.dll"),
                TargetFrameworkMoniker = "net10.0"
            },
            new LspIdeScope(_logger));
        var owner = new ProjectOwner(project.ProjectFullName, project.TargetFrameworkMoniker);
        var scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
        scopeManager.ResolveOwners(file.Uri).Returns(new[] { project });
        var registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
        registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>()).Returns(ProjectBindingRegistry.Invalid);

        var bufferService = new DocumentBufferService(); // empty: the file is closed
        var matchService  = new BindingMatchService(_documentGenerations, Substitute.For<IIdeSupportLogger>());
        var fileSystem    = new FileSystemForIDE();
        var tagger = new GherkinDocumentTaggerService(
            bufferService, Tagging.TaggerTestParser.Create(_logger), registryLookup,
            matchService, scopeManager, _logger, fileSystem,
            _documentGenerations, new ProjectConfigGenerations());
        await tagger.RescanClosedFileAsync(file.Uri); // the cache before the rename

        var builder = new WorkspaceEditBuilder(supportsChangeAnnotations: false);
        builder.Add(file.Uri, new LspRange(new Position(2, 10), new Position(2, 13)), "Renamed"); // "Scenario: One"
        var sut = new RenamePostApplyCoordinator(
            _languageServer, new ClientIdeContext("vscode"), matchService, bufferService,
            _csharpDiscoveryService, _csharpFileTextCache, _logger, _documentGenerations, tagger, fileSystem);

        await StageThenCompleteAsync(sut, file.Uri, builder, applied: true);

        matchService.TryGet(new MatchSetKey(file.Uri.ToString(), owner), out var cached)
            .Should().BeTrue("the closed file must stay visible to Find Usages and usage counts");
        cached.Scenarios.Select(s => (s.ScenarioTag.Data as Gherkin.Ast.IHasDescription)?.Name)
            .Should().Equal("Renamed");
    }

    // ── Step usage lenses after the closed files are re-derived ──────────────

    private static readonly DocumentUri ClosedFeatureUri = DocumentUri.FromFileSystemPath("/workspace/closed.feature");

    [Fact]
    public async Task Confirmed_apply_that_rewrote_a_closed_feature_file_refreshes_the_usage_lenses_for_other_clients_Async()
    {
        var sut = CreateSut(isVisualStudio: false);
        var builder = BuilderWithOneEdit(CsUri);
        builder.Add(ClosedFeatureUri, new LspRange(new Position(0, 0), new Position(0, 1)), "x");

        await StageThenCompleteAsync(sut, CsUri, builder, applied: true);

        _languageServer.Client.Received(1).SendRequest(LspStandardMethodNames.WorkspaceCodeLensRefresh);
    }

    [Fact]
    public async Task Confirmed_apply_that_rewrote_a_closed_feature_file_refreshes_the_usage_lenses_for_visual_studio_Async()
    {
        var sut = CreateSut(isVisualStudio: true);
        var builder = BuilderWithOneEdit(CsUri);
        builder.Add(ClosedFeatureUri, new LspRange(new Position(0, 0), new Position(0, 1)), "x");

        await StageThenCompleteAsync(sut, CsUri, builder, applied: true);

        _languageServer.Received(1).SendNotification(
            CustomLspMethodNames.ReqnrollRefreshCodeLens, Arg.Any<RefreshCodeLensParams>());
    }

    [Fact]
    public async Task The_refresh_is_requested_after_the_closed_feature_files_are_re_derived_Async()
    {
        using var file = new ClosedFeatureFile(); // on disk, so the post-rename text can be staged
        var sut = CreateSut(isVisualStudio: false);
        var builder = BuilderWithOneEdit(CsUri);
        builder.Add(file.Uri, new LspRange(new Position(2, 10), new Position(2, 13)), "Renamed");

        await StageThenCompleteAsync(sut, CsUri, builder, applied: true);

        Received.InOrder(() =>
        {
            _ = _taggerService.ScanClosedFileForAllOwnersAsync(file.Uri, Arg.Any<string>(), Arg.Any<long>());
            _ = _languageServer.Client.SendRequest(LspStandardMethodNames.WorkspaceCodeLensRefresh);
        });
    }

    [Fact]
    public async Task Confirmed_apply_that_touched_no_closed_feature_file_does_not_request_a_lens_refresh_Async()
    {
        var sut = CreateSut(isVisualStudio: false);

        await StageThenCompleteAsync(sut, CsUri, BuilderWithOneEdit(CsUri), applied: true);

        _languageServer.Client.DidNotReceive().SendRequest(LspStandardMethodNames.WorkspaceCodeLensRefresh);
        _languageServer.DidNotReceiveWithAnyArgs().SendNotification(default!, default(object)!);
    }

    [Fact]
    public async Task A_discarded_rename_does_not_request_a_lens_refresh_Async()
    {
        var sut = CreateSut(isVisualStudio: false);
        var builder = BuilderWithOneEdit(CsUri);
        builder.Add(ClosedFeatureUri, new LspRange(new Position(0, 0), new Position(0, 1)), "x");

        await StageThenCompleteAsync(sut, CsUri, builder, applied: false);

        _languageServer.Client.DidNotReceive().SendRequest(LspStandardMethodNames.WorkspaceCodeLensRefresh);
    }

    [Fact]
    public async Task A_feature_file_that_is_open_is_not_a_reason_to_request_a_lens_refresh_Async()
    {
        // An open file's own parse publishes MatchCacheChangedNotification, which refreshes the lenses.
        DocumentBuffer? ignored;
        _documentBuffer.TryGet(ClosedFeatureUri, out ignored).Returns(true);
        var sut = CreateSut(isVisualStudio: false);
        var builder = BuilderWithOneEdit(CsUri);
        builder.Add(ClosedFeatureUri, new LspRange(new Position(0, 0), new Position(0, 1)), "x");

        await StageThenCompleteAsync(sut, CsUri, builder, applied: true);

        _languageServer.Client.DidNotReceive().SendRequest(LspStandardMethodNames.WorkspaceCodeLensRefresh);
    }
}
