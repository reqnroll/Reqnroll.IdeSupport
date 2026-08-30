#nullable enable

using System.Text.RegularExpressions;
using Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Xunit;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Scaffolding;

public class CandidateStepDefinitionFileRankerTests
{
    private const string FeatureText =
        "Feature: F\nScenario: S\n    Given a step\n    When I press add\n    Then done\n";

    private static readonly LspTextSnapshot Snapshot =
        new("/workspace/test.feature", 1, FeatureText);

    [Fact]
    public void Returns_empty_when_no_steps_are_defined()
    {
        var matchSet = BuildMatchSet(
            UndefinedMatch("a step"));

        var result = CandidateStepDefinitionFileRanker.RankCandidateFiles(matchSet);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Ranks_the_file_with_the_most_matched_steps_first()
    {
        var matchSet = BuildMatchSet(
            DefinedMatch("a step",          "/workspace/CalculatorSteps.cs"),
            DefinedMatch("I press add",     "/workspace/CalculatorSteps.cs"),
            DefinedMatch("done",            "/workspace/CommonSteps.cs"));

        var result = CandidateStepDefinitionFileRanker.RankCandidateFiles(matchSet);

        result.Should().Equal("/workspace/CalculatorSteps.cs", "/workspace/CommonSteps.cs");
    }

    [Fact]
    public void Ignores_undefined_steps_when_ranking()
    {
        var matchSet = BuildMatchSet(
            DefinedMatch("a step", "/workspace/CalculatorSteps.cs"),
            UndefinedMatch("I press add"));

        var result = CandidateStepDefinitionFileRanker.RankCandidateFiles(matchSet);

        result.Should().Equal("/workspace/CalculatorSteps.cs");
    }

    [Fact]
    public void Deduplicates_multiple_defined_steps_in_the_same_file_into_one_entry()
    {
        var matchSet = BuildMatchSet(
            DefinedMatch("a step",      "/workspace/CalculatorSteps.cs"),
            DefinedMatch("I press add", "/workspace/CalculatorSteps.cs"));

        var result = CandidateStepDefinitionFileRanker.RankCandidateFiles(matchSet);

        result.Should().ContainSingle().Which.Should().Be("/workspace/CalculatorSteps.cs");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FeatureBindingMatchSet BuildMatchSet(params StepBindingMatch[] steps) =>
        new("/workspace/test.feature", ProjectOwner.Unknown,
            documentVersion: 1, registryVersion: 1, steps: steps);

    private static StepBindingMatch DefinedMatch(string text, string sourceFile)
    {
        var binding = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given,
            new Regex($"^{Regex.Escape(text)}$"),
            null,
            new ProjectBindingImplementation("Handle", null, new SourceLocation(sourceFile, 1, 1)));

        var item   = MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch);
        var result = MatchResult.CreateMultiMatch(new[] { item });
        var range  = GherkinRange.FromPoint(Snapshot, 0, text.Length);

        return new StepBindingMatch("/workspace/test.feature", range, result, "Given", "S", null);
    }

    private static StepBindingMatch UndefinedMatch(string text)
    {
        var gherkinStep = new IdeSupportGherkinStep(
            new global::Gherkin.Ast.Location(0, 0), "Given ", StepKeywordType.Context, text, null!,
            StepKeyword.Given, ScenarioBlock.Given);

        var item   = MatchResultItem.CreateUndefined(gherkinStep, text);
        var result = MatchResult.CreateMultiMatch(new[] { item });
        var range  = GherkinRange.FromPoint(Snapshot, 0, text.Length);

        return new StepBindingMatch("/workspace/test.feature", range, result, "Given", "S", null);
    }
}
