using Gherkin.Ast;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;




namespace Reqnroll.IdeSupport.LSP.Core.DocumentOutline;

/// <summary>GherkinDocumentSymbolService</summary>
public class GherkinDocumentSymbolService : IGherkinDocumentSymbolService
{
    /// <summary>Builds the document-outline symbol tree (feature, rules, scenarios, steps, examples) from the parsed Deveroom tags.</summary>
    public IReadOnlyList<GherkinDocumentSymbol> BuildSymbols(IReadOnlyCollection<DeveroomTag> tags)
    {
        var featureTag = tags.FirstOrDefault(t => t.Type == DeveroomTagTypes.FeatureBlock);
        if (featureTag is null)
            return Array.Empty<GherkinDocumentSymbol>();

        return new[] { BuildFeatureSymbol(featureTag) };
    }

    // ── Symbol builders ───────────────────────────────────────────────────────

    private static GherkinDocumentSymbol BuildFeatureSymbol(DeveroomTag featureTag)
    {
        var feature = (Feature)featureTag.Data;
        return new GherkinDocumentSymbol(
            Name: feature.Name ?? feature.Keyword.Trim(),
            Detail: null,
            Kind: GherkinSymbolKind.Feature,
            Range: featureTag.Range,
            SelectionRange: FirstLineRange(featureTag.Range),
            Children: BuildFeatureChildren(featureTag));
    }

    private static IReadOnlyList<GherkinDocumentSymbol> BuildFeatureChildren(DeveroomTag parent)
    {
        var result = new List<GherkinDocumentSymbol>();
        foreach (var child in parent.ChildTags)
        {
            switch (child.Type)
            {
                case DeveroomTagTypes.ScenarioDefinitionBlock:
                    result.Add(BuildScenarioSymbol(child));
                    break;
                case DeveroomTagTypes.RuleBlock:
                    result.Add(BuildRuleSymbol(child));
                    break;
            }
        }
        return result;
    }

    private static GherkinDocumentSymbol BuildRuleSymbol(DeveroomTag ruleTag)
    {
        var rule = (Rule)ruleTag.Data;
        return new GherkinDocumentSymbol(
            Name: rule.Name ?? rule.Keyword.Trim(),
            Detail: null,
            Kind: GherkinSymbolKind.Rule,
            Range: ruleTag.Range,
            SelectionRange: FirstLineRange(ruleTag.Range),
            Children: BuildFeatureChildren(ruleTag));
    }

    private static GherkinDocumentSymbol BuildScenarioSymbol(DeveroomTag scenarioTag)
    {
        var stepsContainer = (StepsContainer)scenarioTag.Data;
        var (name, kind) = stepsContainer switch
        {
            Background bg         => (NameOrKeyword(bg.Name, bg.Keyword), GherkinSymbolKind.Background),
            ScenarioOutline so    => (NameOrKeyword(so.Name, so.Keyword), GherkinSymbolKind.ScenarioOutline),
            Scenario sc           => (NameOrKeyword(sc.Name, sc.Keyword), GherkinSymbolKind.Scenario),
            _                     => (stepsContainer.GetType().Name, GherkinSymbolKind.Scenario),
        };

        var children = new List<GherkinDocumentSymbol>();
        foreach (var child in scenarioTag.ChildTags)
        {
            switch (child.Type)
            {
                case DeveroomTagTypes.StepBlock:
                    children.Add(BuildStepSymbol(child));
                    break;
                case DeveroomTagTypes.ExamplesBlock:
                    children.Add(BuildExamplesSymbol(child));
                    break;
            }
        }

        return new GherkinDocumentSymbol(
            Name: name,
            Detail: null,
            Kind: kind,
            Range: scenarioTag.Range,
            SelectionRange: FirstLineRange(scenarioTag.Range),
            Children: children);
    }

    private static GherkinDocumentSymbol BuildStepSymbol(DeveroomTag stepTag)
    {
        var step = (Step)stepTag.Data;
        return new GherkinDocumentSymbol(
            Name: step.Keyword + step.Text,
            Detail: null,
            Kind: GherkinSymbolKind.Step,
            Range: stepTag.Range,
            SelectionRange: stepTag.Range,
            Children: Array.Empty<GherkinDocumentSymbol>());
    }

    private static GherkinDocumentSymbol BuildExamplesSymbol(DeveroomTag examplesTag)
    {
        var examples = (Examples)examplesTag.Data;
        return new GherkinDocumentSymbol(
            Name: NameOrKeyword(examples.Name, examples.Keyword),
            Detail: null,
            Kind: GherkinSymbolKind.Examples,
            Range: examplesTag.Range,
            SelectionRange: FirstLineRange(examplesTag.Range),
            Children: Array.Empty<GherkinDocumentSymbol>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NameOrKeyword(string? name, string keyword)
        => !string.IsNullOrWhiteSpace(name) ? name! : keyword.Trim();

    /// <summary>Returns a range covering only the first (header) line of <paramref name="range"/>.</summary>
    private static GherkinRange FirstLineRange(GherkinRange range)
    {
        var (startLine, _) = range.StartLinePosition;
        var line = range.Snapshot.GetLineFromLineNumber(startLine);
        // line.End is the offset of the newline character (exclusive of it in the stub snapshot)
        var length = Math.Min(line.End - range.Start, range.Length);
        return new GherkinRange(range.Snapshot, range.Start, Math.Max(0, length));
    }
}
