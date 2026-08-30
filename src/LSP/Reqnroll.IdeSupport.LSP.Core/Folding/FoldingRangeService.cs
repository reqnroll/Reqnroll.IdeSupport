using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;




namespace Reqnroll.IdeSupport.LSP.Core.Folding;

/// <summary>
/// Walks the IdeSupportTag tree to produce foldable region ranges (Code Folding).
/// Folding ranges are computed for:
///   - Feature blocks (body only, after the keyword line)
///   - Scenario / Scenario Outline / Background blocks
///   - Rule blocks
///   - Doc strings
///   - Data tables
///   - Examples blocks
/// </summary>
public class FoldingRangeService : IFoldingRangeService
{
    /// <summary>Walks the parsed Deveroom tags to compute foldable regions for features, scenarios, rules, doc strings, data tables, and examples blocks.</summary>
    public IReadOnlyList<GherkinFoldingRange> BuildFoldingRanges(
        IReadOnlyCollection<IdeSupportTag> tags)
    {
        var result = new List<GherkinFoldingRange>();

        var featureTag = tags.FirstOrDefault(t => t.Type == IdeSupportTagTypes.FeatureBlock);
        if (featureTag is null)
            return result;

        // Fold feature body (line range excluding the keyword line)
        AddFeatureBodyFold(featureTag, result);

        // Walk children for Scenario/Rule blocks
        foreach (var child in featureTag.ChildTags)
        {
            switch (child.Type)
            {
                case IdeSupportTagTypes.ScenarioDefinitionBlock:
                    AddScenarioFold(child, result);
                    break;
                case IdeSupportTagTypes.RuleBlock:
                    AddRuleFold(child, result);
                    break;
            }
        }

        return result;
    }

    // ── Feature body ───────────────────────────────────────────────────────

    private static void AddFeatureBodyFold(IdeSupportTag featureTag, List<GherkinFoldingRange> result)
    {
        var (_, featureEndLine) = GetLineRange(featureTag.Range);
        var featureStartLine = GetLineNumber(featureTag.Range);
        if (featureEndLine - featureStartLine >= 2)
        {
            // Fold from line after the Feature: keyword to the last line
            result.Add(new GherkinFoldingRange(
                featureStartLine + 1,
                featureEndLine));
        }
    }

    // ── Scenario / Background ──────────────────────────────────────────────

    private static void AddScenarioFold(IdeSupportTag scenarioTag, List<GherkinFoldingRange> result)
    {
        var (scenarioStartLine, scenarioEndLine) = GetLineRange(scenarioTag.Range);
        if (scenarioEndLine > scenarioStartLine)
        {
            result.Add(new GherkinFoldingRange(
                scenarioStartLine,
                scenarioEndLine));
        }

        // Doc strings and data tables inside step blocks
        foreach (var inner in scenarioTag.ChildTags)
        {
            switch (inner.Type)
            {
                case IdeSupportTagTypes.StepBlock:
                    AddInlineContentFolds(inner, result);
                    break;
                case IdeSupportTagTypes.ExamplesBlock:
                    AddExamplesFold(inner, result);
                    break;
            }
        }
    }

    /// <summary>Walks a StepBlock for DocString and DataTable children.</summary>
    private static void AddInlineContentFolds(IdeSupportTag stepTag, List<GherkinFoldingRange> result)
    {
        foreach (var inner in stepTag.ChildTags)
        {
            switch (inner.Type)
            {
                case IdeSupportTagTypes.DocString:
                case IdeSupportTagTypes.DataTable:
                    AddContentFold(inner, result);
                    break;
            }
        }
    }

    // ── Rule ───────────────────────────────────────────────────────────────

    private static void AddRuleFold(IdeSupportTag ruleTag, List<GherkinFoldingRange> result)
    {
        var (ruleStartLine, ruleEndLine) = GetLineRange(ruleTag.Range);
        if (ruleEndLine > ruleStartLine)
        {
            result.Add(new GherkinFoldingRange(
                ruleStartLine,
                ruleEndLine));
        }

        // Scenarios inside rules
        foreach (var inner in ruleTag.ChildTags)
        {
            if (inner.Type == IdeSupportTagTypes.ScenarioDefinitionBlock)
                AddScenarioFold(inner, result);
        }
    }

    // ── Examples ───────────────────────────────────────────────────────────

    private static void AddExamplesFold(IdeSupportTag examplesTag, List<GherkinFoldingRange> result)
    {
        var (examplesStartLine, examplesEndLine) = GetLineRange(examplesTag.Range);
        if (examplesEndLine > examplesStartLine)
        {
            result.Add(new GherkinFoldingRange(
                examplesStartLine,
                examplesEndLine));
        }

        // Data table inside examples
        foreach (var inner in examplesTag.ChildTags)
        {
            if (inner.Type == IdeSupportTagTypes.DataTable)
                AddContentFold(inner, result);
        }
    }

    // ── DocString / DataTable (inline content folds) ───────────────────────

    private static void AddContentFold(IdeSupportTag tag, List<GherkinFoldingRange> result)
    {
        var (startLine, endLine) = GetLineRange(tag.Range);
        if (endLine > startLine)
        {
            result.Add(new GherkinFoldingRange(
                startLine,
                endLine));
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static int GetLineNumber(GherkinRange range)
        => range.StartLinePosition.Line;

    private static (int StartLine, int EndLine) GetLineRange(GherkinRange range)
        => (range.StartLinePosition.Line, range.EndLinePosition.Line);
}
