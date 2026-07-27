using System.Diagnostics;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

/// <summary>
/// Tests for <see cref="BindingRegistryChangedHandler"/>.
/// Verifies that closed-file scanning uses the membership index (I1) when a baseline has
/// been received and falls back to folder-glob otherwise, and that open-file reparsing
/// uses index ownership rather than folder-prefix when a baseline exists.
/// </summary>
public class BindingRegistryChangedHandlerTests : IDisposable
{
    private readonly IDocumentBufferService       _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly ICSharpFileTextCache         _csharpFileTextCache = new CSharpFileTextCache();
    private readonly IGherkinDocumentTaggerService _taggerService = Substitute.For<IGherkinDocumentTaggerService>();
    private readonly ILspWorkspaceScopeManager    _scopeManager  = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly ILanguageServerFacade        _languageServer = Substitute.For<ILanguageServerFacade>();
    private readonly ClientIdeContext             _clientIde     = new("visualstudio");
    private readonly IMediator                    _mediator      = Substitute.For<IMediator>();
    private readonly ICSharpBindingDiscoveryService _csharpDiscovery = Substitute.For<ICSharpBindingDiscoveryService>();
    private readonly IFeatureRescanDebouncer      _rescanDebouncer = Substitute.For<IFeatureRescanDebouncer>();
    private readonly IIdeSupportLogger              _logger        = Substitute.For<IIdeSupportLogger>();
    private readonly IFileSystemForIDE            _fileSystem    = new FileSystemForIDE();

    private readonly IIdeSupportLogger _ideLogger = Substitute.For<IIdeSupportLogger>();
    private readonly LspIdeScope     _ideScope;

    // Two on-disk roots — project folder and linked/external folder.
    private readonly string _projectFolder;
    private readonly string _externalFolder;
    private readonly LspReqnrollProject _project;

    public BindingRegistryChangedHandlerTests()
    {
        _ideScope      = new LspIdeScope(_ideLogger);
        _projectFolder = Path.Combine(Path.GetTempPath(), "BRCHTests_" + Guid.NewGuid().ToString("N"));
        _externalFolder = Path.Combine(Path.GetTempPath(), "BRCHTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectFolder);
        Directory.CreateDirectory(_externalFolder);

        _project = DiscoveryTestSupport.MakeProject(_ideScope, _projectFolder);

        // Default: all buffers are empty (no open files).
        _bufferService.All.Returns(Enumerable.Empty<DocumentBuffer>());

        // ScanClosedFileAsync must return a completed Task (NSubstitute default for Task
        // void is already CompletedTask, but be explicit here for clarity).
        _taggerService.ScanClosedFileAsync(
                Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<LspReqnrollProject>())
            .Returns(Task.CompletedTask);
        // ParseAsync: NSubstitute returns Task.FromResult(null) by default — that is fine
        // since the handler discards the return value.
    }

    public void Dispose()
    {
        _project.Dispose();
        // LspIdeScope is not IDisposable; no cleanup needed.
        try { if (Directory.Exists(_projectFolder))  Directory.Delete(_projectFolder,  recursive: true); } catch (Exception ex) { Debug.WriteLine($"BindingRegistryChangedHandlerTests: failed to clean up {_projectFolder}: {ex.Message}"); }
        try { if (Directory.Exists(_externalFolder)) Directory.Delete(_externalFolder, recursive: true); } catch (Exception ex) { Debug.WriteLine($"BindingRegistryChangedHandlerTests: failed to clean up {_externalFolder}: {ex.Message}"); }
    }

    private BindingRegistryChangedHandler CreateSut()
        => CreateSut(_clientIde);

    private BindingRegistryChangedHandler CreateSut(ClientIdeContext clientIde)
        => new(_bufferService, _csharpFileTextCache, _taggerService, _scopeManager, _languageServer, clientIde, _mediator, _csharpDiscovery, _rescanDebouncer, _logger, _fileSystem);

    // ── Closed-file scanning — index-driven (baseline received) ───────────────

    [Fact]
    public async Task ScanAllFeatureFiles_uses_indexed_files_when_baseline_received()
    {
        var f1 = Path.Combine(_projectFolder,  "A.feature");
        var f2 = Path.Combine(_externalFolder, "Linked.feature");
        File.WriteAllText(f1, "Feature: A\n");
        File.WriteAllText(f2, "Feature: Linked\n");

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(new[] { f1, f2 });

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        await _taggerService.Received(1).ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, f1)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
        await _taggerService.Received(1).ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, f2)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
    }

    [Fact]
    public async Task ScanAllFeatureFiles_includes_linked_feature_outside_project_folder()
    {
        // Only a linked file — inside _externalFolder, outside _projectFolder.
        var linked = Path.Combine(_externalFolder, "External.feature");
        File.WriteAllText(linked, "Feature: External\n");

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(new[] { linked });

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        await _taggerService.Received(1).ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, linked)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
    }

    [Fact]
    public async Task ScanAllFeatureFiles_excludes_project_folder_feature_not_in_index()
    {
        // Index contains only f1; f2 is in the project folder but NOT in the index.
        var f1 = Path.Combine(_projectFolder, "Included.feature");
        var f2 = Path.Combine(_projectFolder, "Excluded.feature");
        File.WriteAllText(f1, "Feature: Included\n");
        File.WriteAllText(f2, "Feature: Excluded\n");

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(new[] { f1 }); // f2 absent

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        await _taggerService.Received(1).ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, f1)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
        await _taggerService.DidNotReceive().ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, f2)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
    }

    // ── Closed-file scanning — folder-glob fallback (no baseline) ────────────

    [Fact]
    public async Task ScanAllFeatureFiles_falls_back_to_folder_glob_when_no_baseline()
    {
        var featureFile = Path.Combine(_projectFolder, "Glob.feature");
        File.WriteAllText(featureFile, "Feature: Glob\n");

        _scopeManager.HasBaselineForProject(_project).Returns(false);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        await _taggerService.Received(1).ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, featureFile)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
    }

    [Fact]
    public async Task ScanAllFeatureFiles_glob_fallback_returns_early_when_folder_does_not_exist()
    {
        var project = DiscoveryTestSupport.MakeProject(
            _ideScope,
            Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N")));
        _scopeManager.HasBaselineForProject(project).Returns(false);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(project, IsFullReplacement: true),
            CancellationToken.None);

        await _taggerService.DidNotReceive().ScanClosedFileAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());

        project.Dispose();
    }

    // ── Open-file skip during closed-file scan ────────────────────────────────

    [Fact]
    public async Task ScanAllFeatureFiles_skips_already_open_feature_files()
    {
        var featureFile = Path.Combine(_projectFolder, "Open.feature");
        File.WriteAllText(featureFile, "Feature: Open\n");

        var openUri = DocumentUri.FromFileSystemPath(featureFile);
        _bufferService.All.Returns(new[] { new DocumentBuffer(openUri, 1, "Feature: Open\n") });

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(new[] { featureFile });

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        // Already open → ScanClosedFileAsync must NOT be called.
        await _taggerService.DidNotReceive().ScanClosedFileAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
    }

    // ── Open-file reparsing ───────────────────────────────────────────────────

    [Fact]
    public async Task ReparseOpenFiles_uses_index_ownership_when_baseline_received()
    {
        var ownedUri   = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "Owned.feature"));
        var foreignUri = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "Foreign.feature"));

        _bufferService.All.Returns(new[]
        {
            new DocumentBuffer(ownedUri,   1, "Feature: Owned\n"),
            new DocumentBuffer(foreignUri, 1, "Feature: Foreign\n")
        });

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetProjectsForUri(ownedUri).Returns(new[] { _project });
        _scopeManager.GetProjectsForUri(foreignUri).Returns(Array.Empty<LspReqnrollProject>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        await _taggerService.Received(1).ParseAsync(ownedUri,   Arg.Any<int?>());
        await _taggerService.DidNotReceive().ParseAsync(foreignUri, Arg.Any<int?>());
    }

    [Fact]
    public async Task ReparseOpenFiles_uses_folder_prefix_when_no_baseline()
    {
        var inFolderUri  = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder,  "Inside.feature"));
        var outsideUri   = DocumentUri.FromFileSystemPath(Path.Combine(_externalFolder, "Outside.feature"));

        _bufferService.All.Returns(new[]
        {
            new DocumentBuffer(inFolderUri, 1, "Feature: Inside\n"),
            new DocumentBuffer(outsideUri,  1, "Feature: Outside\n")
        });

        _scopeManager.HasBaselineForProject(_project).Returns(false);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        await _taggerService.Received(1).ParseAsync(inFolderUri, Arg.Any<int?>());
        await _taggerService.DidNotReceive().ParseAsync(outsideUri, Arg.Any<int?>());
    }

    // ── IsFullReplacement = false does not trigger closed-file scan ───────────

    [Fact]
    public async Task Handle_incremental_does_not_trigger_ScanAllFeatureFiles()
    {
        // IsFullReplacement = false → only open files are reparsed, no closed-file scan.
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        await _taggerService.DidNotReceive().ScanClosedFileAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
        _scopeManager.DidNotReceive().GetIndexedFeatureFiles(Arg.Any<LspReqnrollProject>());
    }

    // ── workspace/codeLens/refresh — correct client guard ─────────────────────

    [Fact]
    public async Task Handle_fullReplacement_sends_codeLens_refresh_for_non_vs_client()
    {
        var nonVsIde = new ClientIdeContext("vscode");
        var sut = new BindingRegistryChangedHandler(
            _bufferService, _csharpFileTextCache, _taggerService, _scopeManager, _languageServer, nonVsIde, _mediator, _csharpDiscovery, _rescanDebouncer, _logger, _fileSystem);

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await sut.Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        _languageServer.Client.Received(1).SendRequest("workspace/codeLens/refresh");
    }

    [Fact]
    public async Task Handle_fullReplacement_does_not_send_codeLens_refresh_for_vs_client()
    {
        // _clientIde is constructed with "visualstudio" in the test fixture
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        _languageServer.Client.DidNotReceive().SendRequest("workspace/codeLens/refresh");
    }

    [Fact]
    public async Task Handle_incremental_does_not_send_codeLens_refresh_even_for_non_vs_client()
    {
        var nonVsIde = new ClientIdeContext("vscode");
        var sut = new BindingRegistryChangedHandler(
            _bufferService, _csharpFileTextCache, _taggerService, _scopeManager, _languageServer, nonVsIde, _mediator, _csharpDiscovery, _rescanDebouncer, _logger, _fileSystem);

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await sut.Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        _languageServer.Client.DidNotReceive().SendRequest("workspace/codeLens/refresh");
    }

    // ── Debounced rescan on incremental (Roslyn) changes ──────────────────────
    //
    // ConnectorBindingRegistryProvider only publishes an incremental (IsFullReplacement: false)
    // notification when a binding's matched expression actually changed -- see
    // ConnectorBindingRegistryProviderTests.ApplyRoslynFileUpdate_does_not_raise_event_when_only_a_method_body_changes.
    // So the handler doesn't need to re-check that here: any incremental notification it
    // receives should schedule a debounced rescan.

    [Fact]
    public async Task Handle_incremental_schedules_a_debounced_rescan()
    {
        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        _rescanDebouncer.Received(1).ScheduleRescan(
            _project, Arg.Any<Func<CancellationToken, Task>>());
    }

    [Fact]
    public async Task Handle_fullReplacement_does_not_schedule_a_debounced_rescan()
    {
        // Full replacement already runs ScanAllFeatureFilesAsync synchronously and
        // unconditionally; the debounced path is only for incremental Roslyn patches.
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        _rescanDebouncer.DidNotReceiveWithAnyArgs().ScheduleRescan(default!, default!);
    }

    [Fact]
    public async Task Handle_incremental_debounced_action_rescans_and_refreshes_codeLens()
    {
        var nonVsIde = new ClientIdeContext("vscode");
        var sut = new BindingRegistryChangedHandler(
            _bufferService, _csharpFileTextCache, _taggerService, _scopeManager, _languageServer, nonVsIde, _mediator, _csharpDiscovery, _rescanDebouncer, _logger, _fileSystem);

        var featureFile = Path.Combine(_projectFolder, "A.feature");
        File.WriteAllText(featureFile, "Feature: A\n");
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(new[] { featureFile });

        Func<CancellationToken, Task>? capturedRescan = null;
        _rescanDebouncer
            .When(d => d.ScheduleRescan(_project, Arg.Any<Func<CancellationToken, Task>>()))
            .Do(ci => capturedRescan = ci.Arg<Func<CancellationToken, Task>>());

        await sut.Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        capturedRescan.Should().NotBeNull();
        await capturedRescan!(CancellationToken.None);

        await _taggerService.Received(1).ScanClosedFileAsync(
            Arg.Is<DocumentUri>(u => FilePathMatches(u, featureFile)), Arg.Any<string>(), Arg.Any<LspReqnrollProject>());
        _languageServer.Client.Received(1).SendRequest("workspace/codeLens/refresh");
    }

    // ── .cs rediscovery after full replacement (stale-DLL reconciliation) ─────

    [Fact]
    public async Task Rediscover_reconciles_closed_cs_file_edited_since_build()
    {
        // Assembly built an hour ago; Steps.cs saved just now (edited but not rebuilt) — the
        // exact "edit, save, restart VS" scenario where the compiled binding is stale.
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project   = MakeProjectWithBuiltAssembly(buildTime);

        var stepsPath = WriteCsFile("Steps.cs", "// renamed step", DateTime.UtcNow);

        IndexBindingFiles(project, stepsPath);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(project, IsFullReplacement: true),
            CancellationToken.None);

        await _csharpDiscovery.Received(1).UpdateFromSourceForProjectAsync(
            project,
            Arg.Is<string>(p => PathEq(p, stepsPath)),
            Arg.Is<string>(t => t.Contains("renamed step")),
            Arg.Any<CancellationToken>());

        project.Dispose();
    }

    [Fact]
    public async Task Rediscover_skips_closed_cs_file_unchanged_since_build()
    {
        // Steps.cs is older than the assembly → the DLL faithfully represents it → skip.
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project   = MakeProjectWithBuiltAssembly(buildTime);

        var stepsPath = WriteCsFile("Steps.cs", "// in sync with DLL", DateTime.UtcNow.AddHours(-2));

        IndexBindingFiles(project, stepsPath);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(project, IsFullReplacement: true),
            CancellationToken.None);

        await _csharpDiscovery.DidNotReceive().UpdateFromSourceForProjectAsync(
            Arg.Any<LspReqnrollProject>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        project.Dispose();
    }

    [Fact]
    public async Task Rediscover_reconciles_open_dirty_cs_buffer_regardless_of_timestamp()
    {
        // Disk copy is older than the build, but the open buffer has unsaved edits that the DLL
        // can never reflect → must reconcile using the buffer text, not the disk text.
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project   = MakeProjectWithBuiltAssembly(buildTime);

        var openPath = WriteCsFile("OpenSteps.cs", "// stale disk text", DateTime.UtcNow.AddHours(-2));
        var openUri  = DocumentUri.FromFileSystemPath(openPath);
        // .cs files are never tracked in IDocumentBufferService (Gherkin-only, by design) — the
        // live/unsaved text for an open .cs file comes from ICSharpFileTextCache instead.
        _csharpFileTextCache.Update(openUri, "// unsaved buffer edit");

        IndexBindingFiles(project, openPath);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(project, IsFullReplacement: true),
            CancellationToken.None);

        // Reconciled exactly once, with the BUFFER text — not the on-disk text.
        await _csharpDiscovery.Received(1).UpdateFromSourceForProjectAsync(
            project,
            Arg.Is<string>(p => PathEq(p, openPath)),
            Arg.Is<string>(t => t.Contains("unsaved buffer edit")),
            Arg.Any<CancellationToken>());
        await _csharpDiscovery.DidNotReceive().UpdateFromSourceForProjectAsync(
            project, Arg.Any<string>(), Arg.Is<string>(t => t.Contains("stale disk text")), Arg.Any<CancellationToken>());

        project.Dispose();
    }

    [Fact]
    public async Task Rediscover_ignores_an_open_cs_buffer_owned_by_a_different_project()
    {
        // Regression: ownership must come from the membership index (ResolveOwners), not a
        // folder-prefix check. A sibling project folder whose name extends this project's folder
        // name (e.g. "Minimalnet481" vs "Minimal") must never have its open buffers reconciled
        // into this project's registry just because the path happens to start with this folder.
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project    = MakeProjectWithBuiltAssembly(buildTime);
        var otherProject = DiscoveryTestSupport.MakeProject(
            _ideScope, _projectFolder + "net481", outputAssemblyPath: null);

        var openPath = WriteCsFile("OpenSteps.cs", "// stale disk text", DateTime.UtcNow.AddHours(-2));
        var openUri  = DocumentUri.FromFileSystemPath(openPath);
        _csharpFileTextCache.Update(openUri, "// unsaved buffer edit");

        // The buffer is indexed as owned by the OTHER project, not this one.
        IndexBindingFiles(otherProject, openPath);
        _scopeManager.HasBaselineForProject(project).Returns(true);
        _scopeManager.GetBindingFilePathsForProject(project).Returns(Array.Empty<string>());
        _scopeManager.GetIndexedFeatureFiles(project).Returns(Array.Empty<string>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(project, IsFullReplacement: true),
            CancellationToken.None);

        await _csharpDiscovery.DidNotReceive().UpdateFromSourceForProjectAsync(
            project, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        project.Dispose();
        otherProject.Dispose();
    }

    [Fact]
    public async Task Rediscover_skips_closed_files_when_project_not_built()
    {
        // No output assembly exists → nothing compiled can be stale → closed files are not read.
        // (_project's default OutputAssemblyPath points at a file that was never created.)
        var stepsPath = WriteCsFile("Steps.cs", "// newer than nothing", DateTime.UtcNow);

        IndexBindingFiles(_project, stepsPath);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        await _csharpDiscovery.DidNotReceive().UpdateFromSourceForProjectAsync(
            Arg.Any<LspReqnrollProject>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rediscover_does_not_run_on_incremental_change()
    {
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project   = MakeProjectWithBuiltAssembly(buildTime);
        var stepsPath = WriteCsFile("Steps.cs", "// edited", DateTime.UtcNow);
        IndexBindingFiles(project, stepsPath);

        // IsFullReplacement = false → no stale-DLL reconciliation (it's a live Roslyn patch path).
        await CreateSut().Handle(
            new BindingRegistryChangedNotification(project, IsFullReplacement: false),
            CancellationToken.None);

        await _csharpDiscovery.DidNotReceive().UpdateFromSourceForProjectAsync(
            Arg.Any<LspReqnrollProject>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        project.Dispose();
    }

    // ── Removed-binding-file cleanup (issue #94) ──────────────────────────────

    [Fact]
    public async Task Handle_removes_bindings_for_deleted_binding_file()
    {
        var deletedPath = Path.Combine(_projectFolder, "DeletedSteps.cs");
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(
                _project, IsFullReplacement: false, RemovedBindingFilePaths: new[] { deletedPath }),
            CancellationToken.None);

        await _csharpDiscovery.Received(1).UpdateFromSourceForProjectAsync(
            _project,
            Arg.Is<string>(p => PathEq(p, deletedPath)),
            string.Empty,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_without_removed_paths_does_not_call_removal()
    {
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        await _csharpDiscovery.DidNotReceive().UpdateFromSourceForProjectAsync(
            Arg.Any<LspReqnrollProject>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_continues_when_removal_throws_for_one_file()
    {
        var badPath  = Path.Combine(_projectFolder, "Bad.cs");
        var goodPath = Path.Combine(_projectFolder, "Good.cs");
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        _csharpDiscovery
            .UpdateFromSourceForProjectAsync(_project, badPath, string.Empty, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(
                _project, IsFullReplacement: false, RemovedBindingFilePaths: new[] { badPath, goodPath }),
            CancellationToken.None);

        await _csharpDiscovery.Received(1).UpdateFromSourceForProjectAsync(
            _project, goodPath, string.Empty, Arg.Any<CancellationToken>());
    }

    // ── Code-lens refresh signal after full replacement ──────────────────────

    [Fact]
    public async Task FullReplacement_pushes_reqnroll_refreshCodeLens_for_visual_studio()
    {
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        _languageServer.Received(1).SendNotification(
            "reqnroll/refreshCodeLens",
            Arg.Is<RefreshCodeLensParams>(p => p.ProjectName == _project.ProjectName && p.IsFullReplacement));
    }

    [Fact]
    public async Task Incremental_debounced_action_pushes_refreshCodeLens_with_isFullReplacement_false_for_visual_studio()
    {
        var featureFile = Path.Combine(_projectFolder, "A.feature");
        File.WriteAllText(featureFile, "Feature: A\n");
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(new[] { featureFile });

        Func<CancellationToken, Task>? capturedRescan = null;
        _rescanDebouncer
            .When(d => d.ScheduleRescan(_project, Arg.Any<Func<CancellationToken, Task>>()))
            .Do(ci => capturedRescan = ci.Arg<Func<CancellationToken, Task>>());

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        capturedRescan.Should().NotBeNull();
        await capturedRescan!(CancellationToken.None);

        _languageServer.Received(1).SendNotification(
            "reqnroll/refreshCodeLens",
            Arg.Is<RefreshCodeLensParams>(p => p.ProjectName == _project.ProjectName && !p.IsFullReplacement));
    }

    [Fact]
    public async Task Incremental_change_does_not_push_refreshCodeLens()
    {
        _scopeManager.HasBaselineForProject(_project).Returns(true);

        await CreateSut().Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: false),
            CancellationToken.None);

        _languageServer.DidNotReceive().SendNotification(
            "reqnroll/refreshCodeLens", Arg.Any<RefreshCodeLensParams>());
    }

    [Fact]
    public async Task FullReplacement_does_not_push_refreshCodeLens_for_non_visual_studio()
    {
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(_project).Returns(Array.Empty<string>());

        // VS Code / Rider use the standard workspace/codeLens/refresh request instead.
        await CreateSut(new ClientIdeContext("vscode")).Handle(
            new BindingRegistryChangedNotification(_project, IsFullReplacement: true),
            CancellationToken.None);

        _languageServer.DidNotReceive().SendNotification(
            "reqnroll/refreshCodeLens", Arg.Any<RefreshCodeLensParams>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a project whose output assembly exists on disk with the given write time.</summary>
    private LspReqnrollProject MakeProjectWithBuiltAssembly(DateTime assemblyWriteUtc)
    {
        var assemblyPath = Path.Combine(_projectFolder, "bin", "Debug", "App.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.WriteAllText(assemblyPath, "fake-dll");
        File.SetLastWriteTimeUtc(assemblyPath, assemblyWriteUtc);
        return DiscoveryTestSupport.MakeProject(_ideScope, _projectFolder, outputAssemblyPath: assemblyPath);
    }

    /// <summary>Writes a .cs file under the project folder with a controlled last-write time.</summary>
    private string WriteCsFile(string name, string content, DateTime writeUtc)
    {
        var path = Path.Combine(_projectFolder, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, writeUtc);
        return path;
    }

    /// <summary>Marks the project as baselined and attributes the given .cs files to it in the index.</summary>
    private void IndexBindingFiles(LspReqnrollProject project, params string[] bindingFiles)
    {
        _scopeManager.HasBaselineForProject(project).Returns(true);
        _scopeManager.GetBindingFilePathsForProject(project).Returns(bindingFiles);
        // Keep the (separate) feature-file scan a no-op for these tests.
        _scopeManager.GetIndexedFeatureFiles(project).Returns(Array.Empty<string>());
        // ResolveOwners is the authoritative ownership check CollectCsFilesToReconcile uses for
        // open .cs buffers — stub it consistently with an indexed binding file's real ownership.
        foreach (var path in bindingFiles)
        {
            var uri = DocumentUri.FromFileSystemPath(path);
            _scopeManager.ResolveOwners(uri).Returns(new[] { project });
        }
    }

    private static bool PathEq(string actual, string expected)
        => string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);

    private static bool FilePathMatches(DocumentUri uri, string expected)
    {
        var actual = uri.GetFileSystemPath();
        return string.Equals(
            actual  is null ? null : Path.GetFullPath(actual),
            Path.GetFullPath(expected),
            StringComparison.OrdinalIgnoreCase);
    }
}
