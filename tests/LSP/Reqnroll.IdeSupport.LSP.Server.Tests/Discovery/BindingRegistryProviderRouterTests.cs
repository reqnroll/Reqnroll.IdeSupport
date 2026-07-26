using MediatR;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
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
