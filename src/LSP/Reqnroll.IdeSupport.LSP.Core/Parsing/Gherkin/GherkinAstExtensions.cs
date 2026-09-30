using Gherkin.Ast;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>Convenience extensions for navigating and classifying Gherkin AST nodes.</summary>
public static class GherkinAstExtensions
{
    /// <summary>Returns the direct child nodes that are step containers (Background/Scenario/ScenarioOutline).</summary>
    public static IEnumerable<StepsContainer> StepsContainers(this IHasChildren container)
        => container.Children.OfType<StepsContainer>();

    /// <summary>Returns every step container in a feature or rule, descending into nested Rule blocks.</summary>
    public static IEnumerable<StepsContainer> FlattenStepsContainers(this IHasChildren featureOrRule)
    {
        foreach (var featureChild in featureOrRule.Children)
            if (featureChild is StepsContainer stepsContainer)
                yield return stepsContainer;
            else if (featureChild is IHasChildren containerNode)
                foreach (var ruleStepsContainer in containerNode.StepsContainers())
                    yield return ruleStepsContainer;
    }

    /// <summary>Returns the direct child nodes that are scenarios (including scenario outlines).</summary>
    public static IEnumerable<Scenario> ScenarioDefinitions(this IHasChildren container)
        => container.Children.OfType<Scenario>();

    /// <summary>Returns every scenario (including scenario outlines) in a feature or rule, descending into nested Rule blocks.</summary>
    public static IEnumerable<Scenario> FlattenScenarioDefinitions(this IHasChildren featureOrRule)
        => featureOrRule.FlattenStepsContainers().OfType<Scenario>();

    /// <summary>Returns the Rule blocks declared directly in a feature.</summary>
    public static IEnumerable<Rule> Rules(this Feature feature)
        => feature.Children.OfType<Rule>();

    /// <summary>Returns the feature's Background block, or null if it has none.</summary>
    public static Background Background(this Feature feature)
        => feature.Children.OfType<Background>().FirstOrDefault();

    /// <summary>Maps a step keyword to the Given/When/Then block it starts, or null for And/But (which continue the previous block).</summary>
    public static ScenarioBlock? ToScenarioBlock(this StepKeyword stepKeyword)
    {
        switch (stepKeyword)
        {
            case StepKeyword.Given:
                return ScenarioBlock.Given;
            case StepKeyword.When:
                return ScenarioBlock.When;
            case StepKeyword.Then:
                return ScenarioBlock.Then;
        }

        return null;
    }

    /// <summary>Returns all keywords in the dialect that introduce a top-level block (Feature, Background, Scenario, Scenario Outline, Examples).</summary>
    public static string[] GetBlockKeywords(this GherkinDialect gherkinDialect) =>
        gherkinDialect.FeatureKeywords
            .Concat(gherkinDialect.BackgroundKeywords)
            .Concat(gherkinDialect.ScenarioKeywords)
            .Concat(gherkinDialect.ScenarioOutlineKeywords)
            .Concat(gherkinDialect.ExamplesKeywords)
            .ToArray();

    /// <summary>
    /// Returns the rows of <paramref name="hasRows"/>, or an empty sequence when the node is an
    /// <see cref="Examples"/> block whose table is missing (deleting an Examples table leaves
    /// the <c>Examples:</c> keyword line behind, so the parsed node has a null header/body and
    /// Gherkin's <see cref="IHasRows.Rows"/> throws <see cref="ArgumentNullException"/>).
    /// </summary>
    public static IEnumerable<TableRow> GetRowsOrEmpty(this IHasRows hasRows)
    {
        if (hasRows == null)
            throw new ArgumentNullException(nameof(hasRows));
        return hasRows switch
        {
            Examples { TableHeader: { } header, TableBody: { } body } => new[] { header }.Concat(body),
            Examples { TableHeader: { } header } => [header],
            Examples { TableHeader: null } => [],
            _ => hasRows.Rows,
        };
    }
}
