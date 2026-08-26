#nullable enable

using System.Linq;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

/// <summary>
/// Self-test for <see cref="WorkspaceReloadContentionScenario"/> (issue #488): drives the real
/// in-process server through a few "many tabs restored" reload storms and asserts it produces a
/// populated baseline/under-load comparison. This assembly already disables xUnit's cross-class
/// parallelization (see <c>AssemblyInfo.cs</c>, added for <c>ConcurrencyProbeTests</c>) -- this
/// scenario is exactly the same "real wall-clock latency under induced contention" shape, so it
/// needs the same isolation from unrelated concurrent test noise to produce meaningful numbers.
/// </summary>
public class WorkspaceReloadContentionScenarioTests
{
    [Fact]
    public async Task Scenario_drives_the_real_server_and_produces_a_baseline_and_under_load_comparison()
    {
        var corpusRoot = CorpusLocator.FindCorpusRoot();

        await using var harness = new BenchmarkLspHarness();
        await harness.StartAsync(corpusRoot);

        var features = await InteractiveScenarios.OpenFeaturesAsync(harness, corpusRoot, count: 5);
        features.Should().HaveCountGreaterThanOrEqualTo(2);

        var restoredFiles = features.Take(features.Count - 1).ToList();
        var probe = features[^1];
        var options = new WorkspaceReloadContentionOptions(Repetitions: 2, SettleDelayMs: 50);

        var result = await new WorkspaceReloadContentionScenario(harness, restoredFiles, probe, options)
            .RunAsync();

        result.Baseline.SampleCount.Should().Be(2);
        result.UnderLoad.SampleCount.Should().Be(2);
        result.Baseline.P95Ms.Should().BeGreaterThanOrEqualTo(0);
        result.UnderLoad.P95Ms.Should().BeGreaterThanOrEqualTo(0);
        result.CeilingRatio.Should().Be(options.CeilingRatio);
    }
}
