using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TextSync;

public class WatchedFilesHandlerTests : IDisposable
{
    private readonly ILspWorkspaceScopeManager      _scopeManager           = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IMediator                      _mediator               = Substitute.For<IMediator>();
    private readonly IIdeSupportLogger                _logger                 = Substitute.For<IIdeSupportLogger>();
    private readonly IEditorConfigOptionsProvider   _editorConfigProvider   = Substitute.For<IEditorConfigOptionsProvider>();
    private readonly ICSharpBindingDiscoveryService _csharpDiscoveryService = Substitute.For<ICSharpBindingDiscoveryService>();
    private readonly LspIdeScope _ideScope;
    private readonly string _projectFolder;

    public WatchedFilesHandlerTests()
    {
        _ideScope = new LspIdeScope(_logger);
        _projectFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_projectFolder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectFolder))
            Directory.Delete(_projectFolder, recursive: true);
    }

    private WatchedFilesHandler CreateSut()
        => new(_scopeManager, _mediator, _logger, _editorConfigProvider, _csharpDiscoveryService);

    private LspReqnrollProject MakeProject()
    {
        var info = new ReqnrollProjectLoadedParams
        {
            WorkspaceFolder        = _projectFolder,
            ProjectFile            = Path.Combine(_projectFolder, "MyApp.Tests.csproj"),
            ProjectFolder          = _projectFolder,
            OutputAssemblyPath     = Path.Combine(_projectFolder, "bin", "Debug", "net8.0", "MyApp.Tests.dll"),
            TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0"
        };
        return new LspReqnrollProject(info, _ideScope);
    }

    private LspProjectScope MakeScope(LspReqnrollProject project)
    {
        var scope = new LspProjectScope(_projectFolder, _ideScope);
        scope.AddOrUpdateProject(new ReqnrollProjectLoadedParams
        {
            WorkspaceFolder        = _projectFolder,
            ProjectFile            = project.ProjectFullName,
            ProjectFolder          = project.ProjectFolder,
            OutputAssemblyPath     = project.OutputAssemblyPath,
            TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0"
        });
        return scope;
    }

    private static DidChangeWatchedFilesParams MakeParams(string filePath, FileChangeType changeType)
    {
        var uri = DocumentUri.FromFileSystemPath(filePath);
        return new DidChangeWatchedFilesParams
        {
            Changes = new Container<FileEvent>(new FileEvent { Uri = uri, Type = changeType })
        };
    }

    // ── No matching project ───────────────────────────────────────────────────

    [Fact]
    public async Task Handle_skips_and_does_not_publish_when_no_project_matches_config_change()
    {
        _scopeManager.GetProjectForUri(Arg.Any<DocumentUri>()).Returns((LspReqnrollProject?)null);

        var sut = CreateSut();
        var configPath = Path.Combine(_projectFolder, "reqnroll.json");
        var result = await sut.Handle(MakeParams(configPath, FileChangeType.Changed), CancellationToken.None);

        result.Should().Be(Unit.Value);
        await _mediator.DidNotReceiveWithAnyArgs()
            .Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>());
    }

    // ── Config change with matching project ───────────────────────────────────

    [Fact]
    public async Task Handle_publishes_config_changed_notification_when_project_matches()
    {
        var project = MakeProject();
        _scopeManager.GetProjectForUri(Arg.Any<DocumentUri>()).Returns(project);

        var sut = CreateSut();
        var configPath = Path.Combine(_projectFolder, "reqnroll.json");
        await sut.Handle(MakeParams(configPath, FileChangeType.Changed), CancellationToken.None);

        await _mediator.Received(1).Publish(
            Arg.Any<INotification>(), Arg.Any<CancellationToken>());
    }

    // ── .editorconfig change ──────────────────────────────────────────────────

    [Fact]
    public async Task Handle_invalidates_editorconfig_cache_on_change()
    {
        _scopeManager.GetScopeForUri(Arg.Any<DocumentUri>()).Returns((LspProjectScope?)null);

        var sut = CreateSut();
        var ecPath = Path.Combine(_projectFolder, ".editorconfig");
        await sut.Handle(MakeParams(ecPath, FileChangeType.Changed), CancellationToken.None);

        _editorConfigProvider.Received(1).InvalidateCache(
            Arg.Is<string>(p => p.EndsWith(".editorconfig")));
    }

    [Fact]
    public async Task Handle_publishes_config_changed_for_each_project_in_scope_on_editorconfig_change()
    {
        var project = MakeProject();
        var scope   = MakeScope(project);
        _scopeManager.GetScopeForUri(Arg.Any<DocumentUri>()).Returns(scope);

        var sut = CreateSut();
        var ecPath = Path.Combine(_projectFolder, ".editorconfig");
        await sut.Handle(MakeParams(ecPath, FileChangeType.Changed), CancellationToken.None);

        await _mediator.Received(1).Publish(
            Arg.Any<INotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_skips_publish_but_still_invalidates_cache_when_no_scope_matches_editorconfig()
    {
        _scopeManager.GetScopeForUri(Arg.Any<DocumentUri>()).Returns((LspProjectScope?)null);

        var sut = CreateSut();
        var ecPath = Path.Combine(_projectFolder, ".editorconfig");
        await sut.Handle(MakeParams(ecPath, FileChangeType.Changed), CancellationToken.None);

        _editorConfigProvider.Received(1).InvalidateCache(Arg.Any<string>());
        await _mediator.DidNotReceiveWithAnyArgs()
            .Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>());
    }

    // ── Output assembly change routes via GetProjectByOutputPath ──────────────

    [Fact]
    public async Task Handle_routes_output_assembly_change_via_output_path_lookup()
    {
        var project = MakeProject();
        _scopeManager.GetProjectByOutputPath(Arg.Any<string>()).Returns(project);

        var sut = CreateSut();
        var dllPath = project.OutputAssemblyPath;
        await sut.Handle(MakeParams(dllPath, FileChangeType.Changed), CancellationToken.None);

        // The output-assembly branch must look up by output path, not by URI.
        _scopeManager.Received().GetProjectByOutputPath(
            Arg.Is<string>(p => p.EndsWith("MyApp.Tests.dll")));
        _scopeManager.DidNotReceive().GetProjectForUri(Arg.Any<DocumentUri>());
    }

    [Fact]
    public async Task Handle_returns_Unit_on_empty_changes()
    {
        var sut = CreateSut();
        var request = new DidChangeWatchedFilesParams { Changes = new Container<FileEvent>() };
        var result = await sut.Handle(request, CancellationToken.None);
        result.Should().Be(Unit.Value);
    }

    // ── GetRegistrationOptions ────────────────────────────────────────────────

    [Fact]
    public void GetRegistrationOptions_watches_reqnroll_json()
    {
        var sut = CreateSut();
        var options = sut.GetRegistrationOptions(null!, null!);
        options.Watchers.Should().ContainSingle(w =>
            w.GlobPattern.ToString()!.Contains("reqnroll.json"));
    }

    [Fact]
    public void GetRegistrationOptions_watches_editorconfig()
    {
        var sut = CreateSut();
        var options = sut.GetRegistrationOptions(null!, null!);
        options.Watchers.Should().Contain(w =>
            w.GlobPattern.ToString()!.Contains(".editorconfig"));
    }

    [Fact]
    public void GetRegistrationOptions_watches_build_output_assemblies()
    {
        var sut = CreateSut();
        var options = sut.GetRegistrationOptions(null!, null!);
        options.Watchers.Should().Contain(w =>
            w.GlobPattern.ToString()!.Contains("bin") &&
            w.GlobPattern.ToString()!.Contains(".dll"));
    }

    // ── C# source deletion ────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_calls_RemoveFileAsync_when_cs_source_is_deleted()
    {
        var csPath = Path.Combine(_projectFolder, "Steps.cs");
        var sut = CreateSut();
        await sut.Handle(MakeParams(csPath, FileChangeType.Deleted), CancellationToken.None);

        await _csharpDiscoveryService.Received(1)
            .RemoveFileAsync(
                Arg.Is<DocumentUri>(u => u.GetFileSystemPath()!.EndsWith("Steps.cs")),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ignores_cs_source_create_and_change_events()
    {
        var csPath = Path.Combine(_projectFolder, "Steps.cs");
        var sut = CreateSut();

        await sut.Handle(MakeParams(csPath, FileChangeType.Created), CancellationToken.None);
        await sut.Handle(MakeParams(csPath, FileChangeType.Changed), CancellationToken.None);

        await _csharpDiscoveryService.DidNotReceiveWithAnyArgs()
            .RemoveFileAsync(Arg.Any<DocumentUri>(), Arg.Any<CancellationToken>());
    }
}
