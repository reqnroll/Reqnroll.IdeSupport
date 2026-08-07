using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.TestTargets;

/// <summary>
/// Resolves the generated C# test method(s) that a <c>.feature</c> scenario or Scenario Outline
/// (or one of its <c>Examples:</c> rows) corresponds to, by reading the project's generated
/// <c>&lt;feature&gt;.feature.cs</c> code-behind rather than predicting Reqnroll's naming rules blind
/// — see docs/Test-Runner-Integration-Design.md §3.
/// </summary>
public interface IScenarioTestTargetResolver
{
    /// <summary>
    /// Resolves the test target(s) for the scenario/Outline/example-row at <paramref name="scenarioRange"/>.
    /// </summary>
    /// <param name="featureUri">The <c>.feature</c> file's URI — used to locate its generated <c>.feature.cs</c> companion.</param>
    /// <param name="tags">The <c>.feature</c> file's already-parsed Gherkin tag tree (e.g. <c>buffer.Tags</c>).</param>
    /// <param name="scenarioRange">
    /// The range to resolve at. A range within a scenario/Outline's own header or steps resolves to
    /// every target for that scenario (e.g. every Outline row); a range within one specific
    /// <c>Examples:</c> row resolves to just that row's target.
    /// </param>
    /// <param name="projectPackageIds">
    /// The owning project's referenced NuGet package IDs, used to determine which row-attribute type
    /// (e.g. <c>Xunit.InlineDataAttribute</c>) identifies a parameterized-row instance for that
    /// project's test framework. An empty collection is treated as "framework unknown" — Tier 1
    /// method/class resolution still works, but row-tests parameterization is not detected.
    /// </param>
    /// <param name="projectFolder">
    /// The owning project's directory (the folder containing its <c>.csproj</c>), used to fall back
    /// to an <c>obj/</c>-relocated code-behind file (Reqnroll 3.3.0+'s
    /// <c>GenerateFeatureFileCodeBehindInProjectDirectory=false</c> option) when the co-located
    /// <c>&lt;feature&gt;.feature.cs</c> doesn't exist. May be <see langword="null"/> or empty if the
    /// owning project couldn't be resolved — the co-located convention is still tried in that case.
    /// </param>
    /// <returns>Zero or more resolved targets. Never <see langword="null"/>.</returns>
    IReadOnlyList<ScenarioTestTarget> Resolve(
        Uri featureUri,
        IReadOnlyCollection<DeveroomTag> tags,
        GherkinRange scenarioRange,
        IReadOnlyCollection<string> projectPackageIds,
        string? projectFolder = null);
}
