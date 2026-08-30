#nullable enable

using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// One Scenario/Scenario Outline definition discovered in a feature document, with its tag
/// context retained (as an <see cref="IGherkinDocumentContext"/>) so hook-scope matching
/// (issue #373's hook-match-count CodeLens) can be evaluated against it directly via
/// <see cref="Bindings.ProjectHookBinding.Match"/>, reusing the exact same scope-matching logic
/// "Go to Hooks"/#269's hook-match CodeLens already exercise -- no separate matching code needed.
/// </summary>
/// <remarks>
/// Deliberately excludes <c>Background</c> blocks (which share <see cref="IdeSupportTagTypes.ScenarioDefinitionBlock"/>
/// with real scenarios but aren't independently executed/countable) and counts a Scenario Outline
/// once regardless of its Examples row count (per #373's decided semantics -- a static count of
/// scenario *definitions*, not expanded runtime executions).
/// </remarks>
public sealed class FeatureScenarioInfo
{
    /// <summary>Initializes a new instance of the <see cref="FeatureScenarioInfo"/> class.</summary>
    public FeatureScenarioInfo(string featureDocumentId, IdeSupportTag scenarioTag)
    {
        FeatureDocumentId = featureDocumentId ?? throw new ArgumentNullException(nameof(featureDocumentId));
        ScenarioTag       = scenarioTag ?? throw new ArgumentNullException(nameof(scenarioTag));
    }

    /// <summary>The document ID (URI string) of the feature file that contains this scenario.</summary>
    public string FeatureDocumentId { get; }

    /// <summary>
    /// The scenario's <c>ScenarioDefinitionBlock</c> tag -- an <see cref="IGherkinDocumentContext"/>
    /// whose <see cref="GherkinDocumentContextExtensions.GetTagNames"/> already walks up to include
    /// inherited Feature-level tags, so passing it directly to <c>ProjectHookBinding.Match</c>
    /// correctly evaluates hooks scoped to either level with no special-casing.
    /// </summary>
    public IdeSupportTag ScenarioTag { get; }

    /// <summary>The scenario/scenario outline's title, or <see langword="null"/> if untitled.</summary>
    public string? Name => (ScenarioTag.Data as Gherkin.Ast.IHasDescription)?.Name is { Length: > 0 } name ? name : null;

    /// <summary>True when this is a <c>Scenario Outline</c> rather than a plain <c>Scenario</c>.</summary>
    public bool IsOutline => ((IGherkinDocumentContext)ScenarioTag).IsScenarioOutline();

    /// <summary>The scenario definition's text span within <see cref="FeatureDocumentId"/>.</summary>
    public Documents.GherkinRange Range => ScenarioTag.Range;
}
