#nullable enable

using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

/// <summary>
/// Harness self-test (implementation plan §2): drives the real in-process server against a tiny
/// corpus subset with low iteration counts and asserts the benchmark produces a populated result.
/// Guards against the harness silently measuring nothing (e.g. a handler returning null for the
/// corpus URIs, or positions that never hit a step).
/// </summary>
public class BenchmarkHarnessSmokeTests
{
    [Fact]
    public async Task Harness_drives_the_real_server_and_produces_samples()
    {
        var corpusRoot = CorpusLocator.FindCorpusRoot();

        await using var harness = new BenchmarkLspHarness();
        await harness.StartAsync(corpusRoot);

        var features = await InteractiveScenarios.OpenFeaturesAsync(harness, corpusRoot, count: 2);
        features.Should().NotBeEmpty();

        var scenarios = new InteractiveScenarios(harness, features, warmup: 1, measured: 3);

        var semanticTokens = await scenarios.SemanticTokensAsync();
        semanticTokens.SampleCount.Should().Be(3);
        semanticTokens.P95Ms.Should().BeGreaterThanOrEqualTo(0);

        var keyword = await scenarios.KeywordCompletionAsync();
        keyword.SampleCount.Should().Be(3);
    }

    [Fact]
    public async Task GoToStepDefinition_scenario_measures_a_real_binding_lookup()
    {
        // Issue #757: reqnroll/goToStepDefinition is field-instrumented, so it has synthetic coverage
        // too (the #495 lesson) — and that coverage must hit a bound step, not time an empty result.
        var corpusRoot = CorpusLocator.FindCorpusRoot();

        await using var harness = new BenchmarkLspHarness();
        await harness.StartAsync(corpusRoot);

        // Prime the registry the way BenchmarkRunner does for its bound-state scenarios — here via
        // Roslyn source discovery (no built corpus assembly needed), as FindUsagesScalingProbeTests does.
        harness.SendCorpusProjectLoaded(corpusRoot, Path.Combine(corpusRoot, "does-not-exist.dll"));
        var csPath = Path.Combine(corpusRoot, "Bindings", "CorpusSteps.cs");
        harness.OpenCSharp(DocumentUri.FromFileSystemPath(csPath), 1, File.ReadAllText(csPath));

        var features = await InteractiveScenarios.OpenFeaturesAsync(harness, corpusRoot, count: 2);
        var (line, character) = features[0].StepPosition;
        (await WaitForBindingsAsync(harness, features[0].Uri, line, character)).Should()
            .BeTrue("the benchmark's step position must be a bound step once discovery settles");

        var scenarios = new InteractiveScenarios(harness, features, warmup: 1, measured: 3);
        var summary = await scenarios.GoToStepDefinitionAsync();

        summary.SampleCount.Should().Be(3);
    }

    private static async Task<bool> WaitForBindingsAsync(
        BenchmarkLspHarness harness, DocumentUri uri, int line, int character, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var response = await harness.RequestGoToStepDefinitionAsync(uri, line, character);
            if (response is { Items.Count: > 0 }) return true;
            await Task.Delay(50);
        }
        return false;
    }
}
