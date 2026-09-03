using MediatR;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

/// <summary>
/// Issue #568: <see cref="BindingRegistryProviderRouter"/> owns a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> of per-project providers, populated and torn
/// down from <see cref="ILspWorkspaceScopeManager.ProjectDiscovered"/>/<c>ProjectRemoved"</c>
/// events that can fire from more than one thread as projects load. These tests exercise
/// concurrent callers rather than the sequential-only coverage in
/// <see cref="BindingRegistryProviderRouterTests"/>.
/// </summary>
public class BindingRegistryProviderRouterConcurrencyTests : IDisposable
{
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IBindingMatchService _matchService = Substitute.For<IBindingMatchService>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IFileSystemForIDE _fileSystem = new FileSystemForIDE();
    private readonly LspIdeScope _ideScope;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly List<LspReqnrollProject> _projects = new();

    public BindingRegistryProviderRouterConcurrencyTests()
    {
        _ideScope = new LspIdeScope(_logger);
    }

    public void Dispose()
    {
        foreach (var project in _projects)
            project.Dispose();
    }

    private BindingRegistryProviderRouter CreateSut() =>
        new(_scopeManager, _mediator, _matchService, _logger, _fileSystem);

    // OutputAssemblyPath points at a non-existent file so the initial discovery triggered on
    // ProjectDiscovered short-circuits (no process is spawned).
    private LspReqnrollProject MakeProject(int i)
    {
        var project = DiscoveryTestSupport.MakeProject(
            _ideScope, _folder, outputAssemblyPath: Path.Combine(_folder, $"missing{i}.dll"));
        _projects.Add(project);
        return project;
    }

    private void RaiseProjectDiscovered(LspReqnrollProject project)
        => _scopeManager.ProjectDiscovered += Raise.Event<Action<LspReqnrollProject>>(project);

    private void RaiseProjectRemoved(LspReqnrollProject project)
        => _scopeManager.ProjectRemoved += Raise.Event<Action<LspReqnrollProject>>(project);

    [Fact]
    public async Task Concurrent_ProjectDiscovered_events_for_distinct_projects_never_lose_a_provider()
    {
        using var sut = CreateSut();
        const int projectCount = 16;
        var projects = Enumerable.Range(0, projectCount).Select(MakeProject).ToArray();

        using var gate = new Barrier(projectCount);
        var tasks = projects.Select(p => Task.Run(() =>
        {
            gate.SignalAndWait();
            RaiseProjectDiscovered(p);
        })).ToArray();

        await Task.WhenAll(tasks);

        foreach (var project in projects)
        {
            project.Properties.TryGetValue(typeof(ConnectorBindingRegistryProvider), out var obj)
                .Should().BeTrue();
            obj.Should().BeOfType<ConnectorBindingRegistryProvider>();
        }
    }

    [Fact]
    public async Task Concurrent_ProjectDiscovered_and_ProjectRemoved_for_the_same_project_do_not_throw()
    {
        var project = MakeProject(0);

        for (var round = 0; round < 100; round++)
        {
            using var sut = CreateSut();

            using var gate = new Barrier(2);
            var t1 = Task.Run(() => { gate.SignalAndWait(); RaiseProjectDiscovered(project); });
            var t2 = Task.Run(() => { gate.SignalAndWait(); RaiseProjectRemoved(project); });

            var act = async () => await Task.WhenAll(t1, t2);
            await act.Should().NotThrowAsync(
                $"a ProjectDiscovered/ProjectRemoved race for the same project must not throw, at round {round}");
        }
    }

    [Fact]
    public async Task Dispose_racing_ProjectDiscovered_events_does_not_throw()
    {
        const int projectCount = 8;
        var projects = Enumerable.Range(0, projectCount).Select(MakeProject).ToArray();
        var sut = CreateSut();

        using var gate = new Barrier(projectCount + 1);
        var tasks = projects.Select(p => Task.Run(() =>
        {
            gate.SignalAndWait();
            RaiseProjectDiscovered(p);
        })).ToArray();
        var disposeTask = Task.Run(() =>
        {
            gate.SignalAndWait();
            sut.Dispose();
        });

        var act = async () => await Task.WhenAll(tasks.Append(disposeTask));
        await act.Should().NotThrowAsync();
    }
}
