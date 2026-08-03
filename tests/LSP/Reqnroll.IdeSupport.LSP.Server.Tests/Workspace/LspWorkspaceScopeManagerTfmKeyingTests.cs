using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Workspace;

/// <summary>
/// Documents <see cref="LspWorkspaceScopeManager"/>'s current (Phase 1) behaviour for a
/// multi-targeted project — one <c>.csproj</c> that produces two <see cref="LspReqnrollProject"/>
/// registrations sharing the same <c>ProjectFile</c> but with different target framework
/// monikers. <c>FindProjectByKey</c>'s own comment says TFM keying is "a planned follow-up" and
/// that Phase 1 matches by <c>ProjectFile</c> only; these tests pin down what that means in
/// practice rather than prescribe what it should do.
/// </summary>
public class LspWorkspaceScopeManagerTfmKeyingTests : IAsyncLifetime
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly LspIdeScope _ideScope;
    private readonly LspWorkspaceScopeManager _sut;

    private readonly string _root1 = Path.Combine(Path.GetTempPath(), "TfmKeying_" + Guid.NewGuid().ToString("N"));
    private readonly string _root2 = Path.Combine(Path.GetTempPath(), "TfmKeying_" + Guid.NewGuid().ToString("N"));

    public LspWorkspaceScopeManagerTfmKeyingTests()
    {
        _ideScope = new LspIdeScope(_logger);
        _sut = new LspWorkspaceScopeManager(_ideScope, _logger, _mediator);
        Directory.CreateDirectory(_root1);
        Directory.CreateDirectory(_root2);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _sut.Dispose();
        try { if (Directory.Exists(_root1)) Directory.Delete(_root1, recursive: true); } catch { }
        try { if (Directory.Exists(_root2)) Directory.Delete(_root2, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private ReqnrollProjectLoadedParams ProjectParams(string workspaceFolder, string projectFile, string tfm) => new()
    {
        WorkspaceFolder        = workspaceFolder,
        ProjectFile            = projectFile,
        ProjectFolder          = workspaceFolder,
        OutputAssemblyPath     = Path.Combine(workspaceFolder, "bin", "My.dll"),
        TargetFrameworkMoniker = tfm
    };

    private ReqnrollProjectFilesParams BaselineParams(
        string projectFile, string tfm, params (string path, ProjectFileRole role)[] entries) => new()
    {
        ProjectFile            = projectFile,
        TargetFrameworkMoniker = tfm,
        Kind                   = ProjectFilesKind.Baseline,
        Files                  = entries
            .Select(e => new ProjectFileEntry { Path = e.path, Role = e.role, Added = true })
            .ToArray()
    };

    [Fact]
    public async Task Two_projects_sharing_a_ProjectFile_with_different_Tfm_collapse_to_a_single_resolved_owner()
    {
        // Simulates a multi-targeted project (e.g. net8.0;net481) whose two builds each send
        // their own reqnroll/projectLoaded notification for the same .csproj, from two distinct
        // workspace-folder scopes.
        var sharedProjectFile = Path.Combine(_root1, "Multi.csproj");

        await _sut.HandleProjectLoadedAsync(
            ProjectParams(_root1, sharedProjectFile, ".NETCoreApp,Version=v8.0"), CancellationToken.None);
        await _sut.HandleProjectLoadedAsync(
            ProjectParams(_root2, sharedProjectFile, ".NETFramework,Version=v4.8.1"), CancellationToken.None);

        var shared = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Shared.feature");

        // A baseline naming the net481 TFM still resolves against whichever of the two
        // ProjectFile-matching registrations FindProjectByKey's FirstOrDefault happens to hit —
        // the Tfm on ProjectKey is carried through but never consulted (Phase 1).
        await _sut.HandleProjectFilesAsync(
            BaselineParams(sharedProjectFile, ".NETFramework,Version=v4.8.1", (shared, ProjectFileRole.Feature)),
            CancellationToken.None);

        var owners = _sut.GetProjectsForUri(DocumentUri.FromFileSystemPath(shared));

        // Today's behaviour: exactly one owner is ever resolved for a given ProjectFile, even
        // though two distinct (ProjectFile, Tfm) registrations exist — the two TFM builds are
        // indistinguishable to FindProjectByKey, so they collapse onto a single match rather than
        // each owning their own membership slice.
        owners.Should().ContainSingle(
            "FindProjectByKey matches by ProjectFile only (Phase 1); the two TFM registrations are not disambiguated");
        owners.Single().ProjectFullName.Should().Be(Path.GetFullPath(sharedProjectFile));
    }

    [Fact]
    public async Task Baseline_for_either_Tfm_of_a_shared_ProjectFile_resolves_to_the_same_project_instance()
    {
        var sharedProjectFile = Path.Combine(_root1, "Multi.csproj");

        await _sut.HandleProjectLoadedAsync(
            ProjectParams(_root1, sharedProjectFile, ".NETCoreApp,Version=v8.0"), CancellationToken.None);
        await _sut.HandleProjectLoadedAsync(
            ProjectParams(_root2, sharedProjectFile, ".NETFramework,Version=v4.8.1"), CancellationToken.None);

        var fileForNet8  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Net8.feature");
        var fileForNet481 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Net481.feature");

        await _sut.HandleProjectFilesAsync(
            BaselineParams(sharedProjectFile, ".NETCoreApp,Version=v8.0", (fileForNet8, ProjectFileRole.Feature)),
            CancellationToken.None);
        await _sut.HandleProjectFilesAsync(
            BaselineParams(sharedProjectFile, ".NETFramework,Version=v4.8.1", (fileForNet481, ProjectFileRole.Feature)),
            CancellationToken.None);

        var ownerForNet8   = _sut.GetProjectsForUri(DocumentUri.FromFileSystemPath(fileForNet8)).Single();
        var ownerForNet481 = _sut.GetProjectsForUri(DocumentUri.FromFileSystemPath(fileForNet481)).Single();

        // Whichever LspReqnrollProject instance FindProjectByKey resolved for the first baseline
        // is reused for the second, regardless of the differing Tfm named in each baseline — both
        // .feature files end up attributed to the same project object.
        ownerForNet8.Should().BeSameAs(ownerForNet481,
            "TFM is not part of the lookup key today, so both baselines resolve to whichever single project matched the shared ProjectFile");
    }
}
