using Gherkin.Ast;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>
/// A Gherkin AST step extended with Reqnroll-specific metadata: the resolved keyword enum and
/// the Given/When/Then block it belongs to.
/// </summary>
public class IdeSupportGherkinStep : Step
{
    /// <summary>Creates a step node from its parsed parts plus Reqnroll-specific metadata.</summary>
    public IdeSupportGherkinStep(Location location, string keyword, StepKeywordType keywordType, string text, StepArgument argument,
        StepKeyword stepKeyword, ScenarioBlock scenarioBlock) : base(location, keyword, keywordType, text, argument)
    {
        StepKeyword = stepKeyword;
        ScenarioBlock = scenarioBlock;
    }

    /// <summary>The Given/When/Then block this step belongs to.</summary>
    public ScenarioBlock ScenarioBlock { get; }
    /// <summary>The resolved keyword enum for this step's keyword text.</summary>
    public StepKeyword StepKeyword { get; }
}
