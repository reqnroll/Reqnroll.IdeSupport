using Gherkin.Ast;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;

/// <summary>
/// The structural fingerprint of a benchmark corpus (Performance Verification, T2). Captures
/// the corpus's <em>size and shape</em> — counts and the bound/unbound/ambiguous match mix — which
/// is what matters for benchmark validity, rather than the exact step wording.
/// </summary>
/// <remarks>
/// Re-derived deterministically from the committed corpus via the same parse + match machinery the
/// server uses, so there is no PRNG in the verification path. The corpus drift test asserts a
/// freshly-computed fingerprint equals the one pinned in <c>corpus.manifest.json</c>.
/// </remarks>
public sealed record CorpusFingerprint(
    int FeatureFileCount,
    int ScenarioCount,
    int ScenarioOutlineCount,
    int StepCount,
    int StepDefinitionPatternCount,
    int BoundStepCount,
    int UnboundStepCount,
    int AmbiguousStepCount)
{
    /// <summary>
    /// Computes the fingerprint of the corpus rooted at <paramref name="corpusRoot"/> (expects
    /// <c>Bindings/*.cs</c> and <c>Features/**/*.feature</c> beneath it).
    /// </summary>
    public static async Task<CorpusFingerprint> ComputeAsync(string corpusRoot)
    {
        var registry = await BuildRegistryAsync(Path.Combine(corpusRoot, "Bindings")).ConfigureAwait(false);

        var tagParser = new IdeSupportTagParser(
            new IdeSupportNullLogger(), NullTelemetryService.Instance, new DefaultConfigurationProvider());

        var featureFiles = Directory
            .EnumerateFiles(Path.Combine(corpusRoot, "Features"), "*.feature", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        int scenarios = 0, outlines = 0, steps = 0, bound = 0, unbound = 0, ambiguous = 0;

        foreach (var path in featureFiles)
        {
            var text = File.ReadAllText(path);
            var uri = DocumentUri.FromFileSystemPath(path);

            // bound/unbound/ambiguous + step count: the faithful server path (tag parse → match set).
            var buffer = new DocumentBuffer(uri, 1, text);
            var snapshot = buffer.ToGherkinTextSnapshot();
            var tags = tagParser.Parse(snapshot, registry);
            var matchSet = FeatureBindingMatchSet.FromTags(uri.ToString(), 1, registry.Version, tags);

            steps += matchSet.Steps.Count;
            bound += matchSet.Defined.Count();
            unbound += matchSet.Undefined.Count();
            ambiguous += matchSet.Ambiguous.Count();

            // scenario/outline counts: from the AST (ScenarioOutline derives from Scenario, so
            // it must be tested first).
            CountScenarios(text, ref scenarios, ref outlines);
        }

        return new CorpusFingerprint(
            FeatureFileCount: featureFiles.Length,
            ScenarioCount: scenarios,
            ScenarioOutlineCount: outlines,
            StepCount: steps,
            StepDefinitionPatternCount: registry.StepDefinitions.Length,
            BoundStepCount: bound,
            UnboundStepCount: unbound,
            AmbiguousStepCount: ambiguous);
    }

    private static void CountScenarios(string featureText, ref int scenarios, ref int outlines)
    {
        var parser = new IdeSupportGherkinParser(
            ReqnrollGherkinDialectProvider.Get("en"), NullTelemetryService.Instance);
        parser.ParseAndCollectErrors(featureText, new IdeSupportNullLogger(), out var doc, out _);
        if (doc?.Feature is null) return;

        foreach (var container in EnumerateStepsContainers(doc.Feature))
        {
            if (container is ScenarioOutline) outlines++;
            else if (container is Scenario) scenarios++;
        }
    }

    private static IEnumerable<StepsContainer> EnumerateStepsContainers(Feature feature)
    {
        foreach (var child in feature.Children)
        {
            if (child is StepsContainer sc) yield return sc;
            else if (child is Rule rule)
                foreach (var inner in rule.Children.OfType<StepsContainer>())
                    yield return inner;
        }
    }

    private static async Task<ProjectBindingRegistry> BuildRegistryAsync(string bindingsDir)
    {
        var parser = new StepDefinitionFileParser();
        var stepDefs = new List<ProjectStepDefinitionBinding>();

        if (Directory.Exists(bindingsDir))
        {
            foreach (var csPath in Directory.EnumerateFiles(bindingsDir, "*.cs", SearchOption.AllDirectories)
                                            .OrderBy(p => p, StringComparer.Ordinal))
            {
                var file = FileDetails.FromPath(csPath).WithCSharpContent(File.ReadAllText(csPath));
                stepDefs.AddRange(await parser.Parse(file).ConfigureAwait(false));
            }
        }

        return new ProjectBindingRegistry(stepDefs, Array.Empty<ProjectHookBinding>(), projectHash: 1);
    }

    /// <summary>Minimal provider returning a default configuration (English dialect).</summary>
    private sealed class DefaultConfigurationProvider : IIdeSupportConfigurationProvider
    {
        private readonly IdeSupportConfiguration _configuration = new();
        public event EventHandler? ConfigurationChanged { add { } remove { } }
        public IdeSupportConfiguration GetConfiguration() => _configuration;
    }
}
