#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus;

/// <summary>
/// Deterministic, index-driven generator for the benchmark corpus (Performance Verification, T2). Emits a
/// <c>Bindings/</c> C# step-definition file, a tree of <c>Features/*.feature</c> files and a
/// <c>reqnroll.json</c>. It is the corpus <b>re-pin tool</b>: the generated files are committed,
/// and the pin is the committed tree + its <see cref="CorpusFingerprint"/>, not regeneration.
/// </summary>
/// <remarks>
/// Unlike <c>SampleProjectGenerator</c> this uses <b>no randomness</b> — every value derives from a
/// loop index — so regeneration is reproducible and the bound/unbound/ambiguous mix is controlled
/// by construction. (See the implementation plan, A3.2.1, for why a seeded shared-PRNG generator is
/// not relied on for the pin.)
/// </remarks>
public sealed class CorpusGenerator
{
    public int FeatureFileCount { get; init; } = 50;
    public int UniquePatternCount { get; init; } = 60;
    public int ScenariosPerFeature { get; init; } = 4;

    public void Generate(string corpusRoot)
    {
        var featuresDir = Path.Combine(corpusRoot, "Features");
        var bindingsDir = Path.Combine(corpusRoot, "Bindings");

        // Start clean so removed/renamed files don't linger in the committed tree.
        if (Directory.Exists(featuresDir)) Directory.Delete(featuresDir, recursive: true);
        if (Directory.Exists(bindingsDir)) Directory.Delete(bindingsDir, recursive: true);
        Directory.CreateDirectory(featuresDir);
        Directory.CreateDirectory(bindingsDir);

        File.WriteAllText(Path.Combine(bindingsDir, "CorpusSteps.cs"), BuildBindings());
        File.WriteAllText(Path.Combine(corpusRoot, "reqnroll.json"), BuildReqnrollJson());

        for (var f = 0; f < FeatureFileCount; f++)
            File.WriteAllText(
                Path.Combine(featuresDir, $"Feature{f:D3}.feature"),
                BuildFeature(f));
    }

    // ── Bindings ────────────────────────────────────────────────────────────────
    // A fixed core plus N unique patterns. The two "the result is …" patterns deliberately
    // overlap on numeric values to produce ambiguous matches by construction.
    private string BuildBindings()
    {
        var sb = new StringBuilder();
        sb.AppendLine("using Reqnroll;");
        sb.AppendLine();
        sb.AppendLine("namespace Corpus.Bindings;");
        sb.AppendLine();
        sb.AppendLine("[Binding]");
        sb.AppendLine("public class CorpusSteps");
        sb.AppendLine("{");
        sb.AppendLine("    [Given(@\"precondition (\\d+) is met\")]");
        sb.AppendLine("    public void GivenPreconditionIsMet(int n) { }");
        sb.AppendLine();
        sb.AppendLine("    [When(@\"action (\\d+) is performed\")]");
        sb.AppendLine("    public void WhenActionIsPerformed(int n) { }");
        sb.AppendLine();
        sb.AppendLine("    // Overlapping patterns: a numeric result matches both → ambiguous.");
        sb.AppendLine("    [Then(@\"the result is (.+)\")]");
        sb.AppendLine("    public void ThenTheResultIsText(string s) { }");
        sb.AppendLine();
        sb.AppendLine("    [Then(@\"the result is (\\d+)\")]");
        sb.AppendLine("    public void ThenTheResultIsNumber(int n) { }");
        sb.AppendLine();

        for (var i = 0; i < UniquePatternCount; i++)
        {
            sb.AppendLine($"    [When(@\"feature {i} is enabled\")]");
            sb.AppendLine($"    public void WhenFeature{i}IsEnabled() {{ }}");
            sb.AppendLine();
        }

        // Hook bindings: not fingerprinted (CorpusFingerprint only tracks step-definition shape),
        // so these can be added/edited freely without moving the pinned fingerprint. Gives the
        // hook-facing benchmarks (Go to Hooks, hook CodeLens both directions, Go to Matching
        // Scenarios) something real to match instead of always hitting the empty fast path — an
        // unscoped hook plus a tag-scoped one (matched by every feature's first scenario, see
        // BuildFeature's "@hookscope" tag) covers both the "all scenarios" and counted-match shapes.
        sb.AppendLine("    [BeforeScenario]");
        sb.AppendLine("    public void GlobalBeforeScenario() { }");
        sb.AppendLine();
        sb.AppendLine("    [AfterScenario]");
        sb.AppendLine("    public void GlobalAfterScenario() { }");
        sb.AppendLine();
        sb.AppendLine("    [BeforeScenario(\"hookscope\")]");
        sb.AppendLine("    public void ScopedBeforeScenario() { }");
        sb.AppendLine();
        sb.AppendLine("    [BeforeStep]");
        sb.AppendLine("    public void GlobalBeforeStep() { }");
        sb.AppendLine();
        sb.AppendLine("    [AfterStep]");
        sb.AppendLine("    public void GlobalAfterStep() { }");
        sb.AppendLine();

        sb.AppendLine("}");
        return sb.ToString();
    }

    // ── Features ──────────────────────────────────────────────────────────────────
    private string BuildFeature(int f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Feature: Corpus feature {f:D3}");
        sb.AppendLine($"  Synthetic benchmark feature {f:D3} for performance verification.");
        sb.AppendLine();

        for (var s = 0; s < ScenariosPerFeature; s++)
        {
            // Rotate step kinds across scenarios so each file carries a stable mix of
            // bound, unbound and ambiguous steps.
            var patternId = (f * ScenariosPerFeature + s) % UniquePatternCount;
            // Tags don't move the fingerprint (scenario/step counts are unaffected), so the first
            // scenario of every feature carries @hookscope to give the tag-scoped hook binding
            // (see BuildBindings) real matches for the hook-facing benchmarks to exercise.
            if (s == 0) sb.AppendLine("  @hookscope");
            sb.AppendLine($"  Scenario: Scenario {f:D3}-{s}");
            sb.AppendLine($"    Given precondition {s} is met");                 // bound
            sb.AppendLine($"    When feature {patternId} is enabled");           // bound (unique)
            sb.AppendLine($"    When action {s} is performed");                  // bound
            sb.AppendLine($"    When undefined step {f}-{s} occurs");            // unbound
            sb.AppendLine($"    Then the result is {s}");                        // ambiguous (numeric)
            sb.AppendLine($"    Then the result is computed cleanly");           // bound (text only)
            sb.AppendLine();
        }

        // One scenario outline per file (exercises the outline path + counts).
        sb.AppendLine($"  Scenario Outline: Outline {f:D3}");
        sb.AppendLine("    Given precondition <n> is met");
        sb.AppendLine("    When action <n> is performed");
        sb.AppendLine("    Then the result is <outcome>");
        sb.AppendLine();
        sb.AppendLine("    Examples:");
        sb.AppendLine("      | n | outcome |");
        sb.AppendLine("      | 1 | success |");
        sb.AppendLine("      | 2 | failure |");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string BuildReqnrollJson() =>
        "{\n" +
        "  \"$schema\": \"https://schemas.reqnroll.net/reqnroll-config-latest.json\",\n" +
        "  \"language\": {\n" +
        "    \"feature\": \"en\"\n" +
        "  }\n" +
        "}\n";
}
