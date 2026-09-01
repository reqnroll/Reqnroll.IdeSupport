#nullable enable

using System.IO;
using System.Linq;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

public class BatchScenariosTests
{
    [Fact]
    public void Discovery_scenarios_are_skipped_when_no_corpus_assembly_is_supplied()
    {
        var skipped = BatchScenarios.UnavailableDiscoveryScenarios(corpusAssemblyPath: null);

        skipped.Select(s => s.Target).Should().BeEquivalentTo(
            new[]
            {
                PerfTargets.RoslynReDiscovery, PerfTargets.ReflectionDiscovery,
                PerfTargets.StepRename, PerfTargets.FindUnusedStepDefinitions,
                PerfTargets.CSharpRapidEditBurst,
            });
        skipped.Should().OnlyContain(s => s.Reason.Contains("built corpus"));
    }

    [Fact]
    public void Discovery_scenarios_are_skipped_when_the_assembly_path_does_not_exist()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-corpus-assembly.dll");
        BatchScenarios.UnavailableDiscoveryScenarios(missing).Should().HaveCount(5);
    }

    [Fact]
    public void Discovery_scenarios_are_not_skipped_when_a_real_assembly_is_supplied()
    {
        // Any existing file stands in for "a built corpus assembly is present".
        var existing = typeof(BatchScenariosTests).Assembly.Location;
        BatchScenarios.UnavailableDiscoveryScenarios(existing).Should().BeEmpty();
    }
}
