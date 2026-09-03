using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Workspace;

/// <summary>
/// Issue #568: <see cref="LspProjectScope.AddOrUpdateProject"/> already documents the race it
/// guards against — two concurrent calls for the same project both seeing <c>TryGetValue</c>
/// return <see langword="false"/>, constructing separate <see cref="LspReqnrollProject"/> instances,
/// with the losing one's background work never disposed — and fixed it with
/// <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,System.Func{TKey,TValue})"/>. These
/// tests exercise that guarantee under real concurrent callers rather than the sequential-only
/// coverage in <see cref="LspProjectScopeTests"/>.
/// </summary>
public class LspProjectScopeConcurrencyTests
{
    private readonly LspIdeScope _ideScope = new(Substitute.For<IIdeSupportLogger>());
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    private LspProjectScope CreateSut() => new(_root, _ideScope);

    private ReqnrollProjectLoadedParams ParamsFor(string projectFileName) => new()
    {
        WorkspaceFolder = _root,
        ProjectFile = Path.Combine(_root, projectFileName),
        ProjectFolder = _root,
        OutputAssemblyPath = Path.Combine(_root, "bin/Debug/Proj.dll"),
        TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0"
    };

    [Fact]
    public async Task Concurrent_AddOrUpdateProject_calls_for_the_same_project_never_construct_more_than_one_instance()
    {
        var duplicatedAt = -1;

        for (var round = 0; round < 300 && duplicatedAt < 0; round++)
        {
            var sut = CreateSut();
            var info = ParamsFor("Proj.csproj");

            using var gate = new Barrier(4);
            var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                gate.SignalAndWait();
                return sut.AddOrUpdateProject(info).Project;
            })).ToArray();

            var projects = await Task.WhenAll(tasks);

            if (projects.Distinct().Count() != 1 || sut.Projects.Count != 1)
                duplicatedAt = round;
        }

        duplicatedAt.Should().Be(-1,
            $"concurrent AddOrUpdateProject calls for the identical project must never construct more than one LspReqnrollProject, at round {duplicatedAt}");
    }

    [Fact]
    public async Task Concurrent_AddOrUpdateProject_calls_for_distinct_projects_never_lose_a_project()
    {
        var sut = CreateSut();
        const int threadCount = 16;

        using var gate = new Barrier(threadCount);
        var tasks = Enumerable.Range(0, threadCount).Select(i => Task.Run(() =>
        {
            gate.SignalAndWait();
            return sut.AddOrUpdateProject(ParamsFor($"Proj{i}.csproj")).Project;
        })).ToArray();

        var projects = await Task.WhenAll(tasks);

        projects.Distinct().Should().HaveCount(threadCount);
        sut.Projects.Should().HaveCount(threadCount);
    }
}
