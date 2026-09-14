using MediatR;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

public class BindingRegistryProviderRouterTests : IDisposable
{
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IMediator                 _mediator     = Substitute.For<IMediator>();
    private readonly IBindingMatchService      _matchService = Substitute.For<IBindingMatchService>();
    private readonly IIdeSupportLogger            _logger       = Substitute.For<IIdeSupportLogger>();
    private readonly IFileSystemForIDE          _fileSystem   = new FileSystemForIDE();
    private readonly LspIdeScope               _ideScope;
    private readonly string                    _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly LspReqnrollProject        _project;

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    public BindingRegistryProviderRouterTests()
    {
        _ideScope = new LspIdeScope(_logger);
        // OutputAssemblyPath points at a non-existent file so the initial discovery triggered
        // on ProjectDiscovered short-circuits (no process is spawned).
        _project = DiscoveryTestSupport.MakeProject(
            _ideScope, _folder, outputAssemblyPath: Path.Combine(_folder, "missing.dll"));
    }

    public void Dispose() => _project.Dispose();

    private BindingRegistryProviderRouter CreateSut() =>
        new(_scopeManager, _mediator, _matchService, _logger, _fileSystem);

    private void RaiseProjectDiscovered(LspReqnrollProject project)
        => _scopeManager.ProjectDiscovered += Raise.Event<Action<LspReqnrollProject>>(project);

    private void RaiseProjectRemoved(LspReqnrollProject project)
        => _scopeManager.ProjectRemoved += Raise.Event<Action<LspReqnrollProject>>(project);

    // ── GetRegistryForUri ──────────────────────────────────────────────────────

    [Fact]
    public void GetRegistryForUri_returns_invalid_when_no_project_matches()
    {
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>())
            .Returns((LspReqnrollProject?)null);

        using var sut = CreateSut();

        sut.GetRegistryForUri(FeatureUri).Should().BeSameAs(ProjectBindingRegistry.Invalid);
    }

    [Fact]
    public void GetRegistryForUri_returns_invalid_when_project_has_no_provider_registered()
    {
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>())
            .Returns(_project);

        using var sut = CreateSut(); // no ProjectDiscovered raised → no provider in Properties

        sut.GetRegistryForUri(FeatureUri).Should().BeSameAs(ProjectBindingRegistry.Invalid);
    }

    [Fact]
    public void GetRegistryForUri_returns_the_providers_current_registry_for_a_discovered_project()
    {
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>())
            .Returns(_project);

        using var sut = CreateSut();
        RaiseProjectDiscovered(_project);

        // A provider was created and stored on the project; before any successful discovery
        // its Current is Invalid (the URI resolves to that provider, not to the global default).
        _project.Properties.Should().ContainKey(typeof(ConnectorBindingRegistryProvider));
        sut.GetRegistryForUri(FeatureUri).Should().BeSameAs(ProjectBindingRegistry.Invalid);
    }

    // ── Project lifecycle ────────────────────────────────────────────────────────

    [Fact]
    public void OnProjectDiscovered_stores_a_provider_in_the_project_property_bag()
    {
        using var sut = CreateSut();

        RaiseProjectDiscovered(_project);

        _project.Properties.TryGetValue(typeof(ConnectorBindingRegistryProvider), out var obj)
            .Should().BeTrue();
        obj.Should().BeOfType<ConnectorBindingRegistryProvider>();
    }

    [Fact]
    public void OnProjectRemoved_after_discovery_does_not_throw()
    {
        using var sut = CreateSut();
        RaiseProjectDiscovered(_project);

        var act = () => RaiseProjectRemoved(_project);

        act.Should().NotThrow();
    }

    [Fact]
    public void OnProjectRemoved_invalidates_project_match_sets()
    {
        using var sut = CreateSut();
        RaiseProjectDiscovered(_project);

        RaiseProjectRemoved(_project);

        _matchService.Received(1).InvalidateAllForProject(
            Arg.Is<ProjectOwner>(o => o.ProjectFile == _project.ProjectFullName));
    }

    [Fact]
    public void OnProjectRemoved_for_unknown_project_is_ignored()
    {
        using var sut = CreateSut();

        var act = () => RaiseProjectRemoved(_project); // never discovered

        act.Should().NotThrow();
    }

    // ── ResolveUsageSearchScope (issue #548 / #552) ───────────────────────────────
    //
    // Exercises the real production expansion logic (not a mock) by patching real
    // ConnectorBindingRegistryProvider instances via ApplyRoslynFileUpdateAsync -- a genuine
    // Roslyn parse, same in-process seam CSharpBindingDiscoveryService uses on didOpen/didChange
    // -- rather than spawning the out-of-process connector.

    private static CSharpStepDefinitionFile CSharpFile(string path, string content) =>
        FileDetails.FromPath(path).WithCSharpContent(content);

    private const string OneGivenBinding = @"
namespace S
{
    [Reqnroll.Binding]
    public class Steps
    {
        [Reqnroll.Given(""a step"")]
        public void Method() { }
    }
}";

    private static LspReqnrollProject MakeExtraProject(LspIdeScope ideScope, string folder) =>
        DiscoveryTestSupport.MakeProject(ideScope, folder, outputAssemblyPath: Path.Combine(folder, "missing.dll"));

    private static ConnectorBindingRegistryProvider ProviderFor(LspReqnrollProject project) =>
        (ConnectorBindingRegistryProvider)project.Properties[typeof(ConnectorBindingRegistryProvider)]!;

    [Fact]
    public void ResolveUsageSearchScope_returns_null_when_the_file_has_no_direct_owner()
    {
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(Array.Empty<LspReqnrollProject>());

        using var sut = CreateSut();

        sut.ResolveUsageSearchScope(DocumentUri.FromFileSystemPath(Path.Combine(_folder, "Steps.cs")))
            .Should().BeNull();
    }

    [Fact]
    public async Task ResolveUsageSearchScope_expands_to_a_project_whose_registry_independently_reports_the_same_binding()
    {
        // The .cs file physically lives in _project (a class library, say); referencingProject
        // has no ownership relationship to it at all, but references the library and so
        // discovers the exact same binding (same physical SourceFile, same BindingId) via its own
        // connector run -- the shape that motivated issue #548.
        var csPath = Path.Combine(_folder, "ExtraSteps.cs");
        var referencingFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var referencingProject = MakeExtraProject(_ideScope, referencingFolder);

        using var sut = CreateSut();
        RaiseProjectDiscovered(_project);
        RaiseProjectDiscovered(referencingProject);

        var file = CSharpFile(csPath, OneGivenBinding);
        await ProviderFor(_project).ApplyRoslynFileUpdateAsync(file, notify: false);
        await ProviderFor(referencingProject).ApplyRoslynFileUpdateAsync(file, notify: false);

        var csUri = DocumentUri.FromFileSystemPath(csPath);
        _scopeManager.ResolveOwners(csUri).Returns(new[] { _project });
        _scopeManager.ResolvePrimaryOwner(csUri).Returns(_project);

        var scope = sut.ResolveUsageSearchScope(csUri);

        scope.Should().NotBeNull();
        scope!.Should().Contain(new ProjectOwner(_project.ProjectFullName, _project.TargetFrameworkMoniker));
        scope.Should().Contain(new ProjectOwner(referencingProject.ProjectFullName, referencingProject.TargetFrameworkMoniker));

        referencingProject.Dispose();
    }

    [Fact]
    public async Task ResolveUsageSearchScope_does_not_expand_to_a_project_reporting_an_unrelated_binding()
    {
        var csPath = Path.Combine(_folder, "ExtraSteps.cs");
        var unrelatedFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var unrelatedProject = MakeExtraProject(_ideScope, unrelatedFolder);

        using var sut = CreateSut();
        RaiseProjectDiscovered(_project);
        RaiseProjectDiscovered(unrelatedProject);

        await ProviderFor(_project).ApplyRoslynFileUpdateAsync(CSharpFile(csPath, OneGivenBinding), notify: false);
        await ProviderFor(unrelatedProject).ApplyRoslynFileUpdateAsync(
            CSharpFile(Path.Combine(unrelatedFolder, "Other.cs"), @"
namespace S
{
    [Reqnroll.Binding]
    public class Other
    {
        [Reqnroll.Given(""an unrelated step"")]
        public void Method() { }
    }
}"), notify: false);

        var csUri = DocumentUri.FromFileSystemPath(csPath);
        _scopeManager.ResolveOwners(csUri).Returns(new[] { _project });
        _scopeManager.ResolvePrimaryOwner(csUri).Returns(_project);

        var scope = sut.ResolveUsageSearchScope(csUri);

        scope.Should().NotBeNull();
        scope!.Should().Contain(new ProjectOwner(_project.ProjectFullName, _project.TargetFrameworkMoniker));
        scope.Should().NotContain(new ProjectOwner(unrelatedProject.ProjectFullName, unrelatedProject.TargetFrameworkMoniker));

        unrelatedProject.Dispose();
    }

    /// <summary>
    /// Regression guard for issue #552: a project with its own independent physical copy of the
    /// same source (e.g. a parallel multi-targeted sibling project, no reference relationship at
    /// all) normalizes to the identical <see cref="BindingId"/> but must NOT widen the search --
    /// only a project reporting the binding at the EXACT SAME physical SourceFile (the signature
    /// of genuine transitive-reference discovery) may be added.
    /// </summary>
    [Fact]
    public async Task ResolveUsageSearchScope_does_not_expand_to_a_project_with_its_own_copy_of_the_same_source()
    {
        var csPath = Path.Combine(_folder, "ExtraSteps.cs");
        var siblingFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var siblingProject = MakeExtraProject(_ideScope, siblingFolder);

        using var sut = CreateSut();
        RaiseProjectDiscovered(_project);
        RaiseProjectDiscovered(siblingProject);

        await ProviderFor(_project).ApplyRoslynFileUpdateAsync(CSharpFile(csPath, OneGivenBinding), notify: false);
        // Same content (-> same BindingId) but its own distinct physical copy of the file.
        await ProviderFor(siblingProject).ApplyRoslynFileUpdateAsync(
            CSharpFile(Path.Combine(siblingFolder, "ExtraSteps.cs"), OneGivenBinding), notify: false);

        var csUri = DocumentUri.FromFileSystemPath(csPath);
        _scopeManager.ResolveOwners(csUri).Returns(new[] { _project });
        _scopeManager.ResolvePrimaryOwner(csUri).Returns(_project);

        var scope = sut.ResolveUsageSearchScope(csUri);

        scope.Should().NotBeNull();
        scope!.Should().Contain(new ProjectOwner(_project.ProjectFullName, _project.TargetFrameworkMoniker));
        scope.Should().NotContain(new ProjectOwner(siblingProject.ProjectFullName, siblingProject.TargetFrameworkMoniker));

        siblingProject.Dispose();
    }

    // ── Dispose ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_unsubscribes_from_scope_manager_events()
    {
        var sut = CreateSut();
        RaiseProjectDiscovered(_project);

        sut.Dispose();

        // After disposal the router must no longer react to events.
        var act = () => RaiseProjectDiscovered(_project);
        act.Should().NotThrow();
    }
}
