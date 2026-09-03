using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

/// <summary>
/// Issue #568: <see cref="ConnectorBindingRegistryProvider"/> serialises its read-modify-write on
/// <c>_current</c> behind a <see cref="SemaphoreSlim"/> (<c>_currentLock</c>) that is separate from
/// the <c>object</c> lock guarding the in-flight discovery run's cancellation token — "two
/// independent sync primitives coordinating one lifecycle", per the audit that raised this issue.
/// These tests exercise concurrent <see cref="ConnectorBindingRegistryProvider.ApplyRoslynFileUpdateAsync"/>
/// callers rather than the sequential-only coverage in <see cref="ConnectorBindingRegistryProviderTests"/>.
/// </summary>
public class ConnectorBindingRegistryProviderConcurrencyTests : IDisposable
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IConnectorDiscoveryService _discovery = Substitute.For<IConnectorDiscoveryService>();
    private readonly LspIdeScope _ideScope;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly LspReqnrollProject _project;

    public ConnectorBindingRegistryProviderConcurrencyTests()
    {
        _ideScope = new LspIdeScope(_logger);
        _project = DiscoveryTestSupport.MakeProject(_ideScope, _folder);
    }

    public void Dispose() => _project.Dispose();

    private ConnectorBindingRegistryProvider CreateSut() => new(_project, _discovery, _logger);

    private CSharpStepDefinitionFile FileDetailsFor(string fileName, string content) =>
        FileDetails.FromPath(Path.Combine(_folder, fileName)).WithCSharpContent(content);

    private static string StepsSource(string expression, string method = "M") => $@"
namespace S
{{
    [Reqnroll.Binding]
    public class Steps
    {{
        [Reqnroll.Given(""{expression}"")]
        public void {method}(int n) {{ }}
    }}
}}";

    [Fact]
    public async Task Concurrent_ApplyRoslynFileUpdate_calls_for_different_files_never_lose_either_files_bindings()
    {
        const int fileCount = 8;
        var sut = CreateSut();

        // Distinct method names: ReplaceBindings treats a same-named method as the same logical
        // binding across files (so a real rename/move doesn't duplicate it) -- with a shared
        // method name here, every patch would legitimately supersede every other file's entry,
        // masking the concurrency behaviour this test exists to check.
        var files = Enumerable.Range(0, fileCount)
            .Select(i => FileDetailsFor($"Steps{i}.cs", StepsSource($"step {i}", method: $"M{i}")))
            .ToArray();

        using var gate = new Barrier(fileCount);
        var tasks = files.Select(f => Task.Run(async () =>
        {
            gate.SignalAndWait();
            await sut.ApplyRoslynFileUpdateAsync(f);
        })).ToArray();

        await Task.WhenAll(tasks);

        sut.Current.StepDefinitions.Select(s => s.Expression).Should().BeEquivalentTo(
            Enumerable.Range(0, fileCount).Select(i => $"step {i}"),
            "the serialised read-modify-write on _current must not let one file's concurrent patch overwrite another's");
    }

    [Fact]
    public async Task Concurrent_ApplyRoslynFileUpdate_calls_for_the_same_file_leave_current_matching_exactly_one_write()
    {
        var inconsistentAt = -1;

        for (var round = 0; round < 200 && inconsistentAt < 0; round++)
        {
            var sut = CreateSut();
            var fileA = FileDetailsFor("Steps.cs", StepsSource($"variant a {round}"));
            var fileB = FileDetailsFor("Steps.cs", StepsSource($"variant b {round}"));

            using var gate = new Barrier(2);
            var t1 = Task.Run(() => sut.ApplyRoslynFileUpdateAsync(fileA));
            var t2 = Task.Run(() => sut.ApplyRoslynFileUpdateAsync(fileB));
            await Task.WhenAll(t1, t2);

            var expressions = sut.Current.StepDefinitions.Select(s => s.Expression).ToArray();
            var matchesA = expressions.SequenceEqual(new[] { $"variant a {round}" });
            var matchesB = expressions.SequenceEqual(new[] { $"variant b {round}" });

            if (!matchesA && !matchesB)
                inconsistentAt = round;
        }

        inconsistentAt.Should().Be(-1,
            $"two concurrent patches to the same file must leave _current matching exactly one of the two writes, not a torn mix, at round {inconsistentAt}");
    }

    [Fact]
    public async Task Concurrent_TriggerRefresh_and_Dispose_do_not_throw()
    {
        // Regression guard: TriggerRefresh() used to read newCts.Token *after* publishing newCts
        // to _cts and releasing _cts_lock. A Dispose() racing that same window disposes the CTS
        // this method just published, so the later newCts.Token read threw ObjectDisposedException
        // -- reproduced on round 83 of 2000 pre-fix, matching the "SemaphoreSlim + separate lock"
        // hazard this issue's audit called out for this class.
        Exception? caught = null;
        var failedAtRound = -1;

        for (var round = 0; round < 2000 && caught == null; round++)
        {
            var sut = CreateSut();
            using var gate = new Barrier(2);
            var t1 = Task.Run(() => { gate.SignalAndWait(); sut.TriggerRefresh(); });
            var t2 = Task.Run(() => { gate.SignalAndWait(); sut.Dispose(); });
            try
            {
                await Task.WhenAll(t1, t2);
            }
            catch (Exception ex)
            {
                caught = ex;
                failedAtRound = round;
            }
        }

        caught.Should().BeNull($"round {failedAtRound}: {caught}");
    }
}
