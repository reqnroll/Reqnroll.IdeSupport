#nullable disable

using Gherkin.Ast;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// Describes a step that did not match any step definition, carrying the original AST step
/// plus an optional custom step text override (used e.g. for scenario outline placeholders).
/// </summary>
public class UndefinedStepDescriptor
{
    /// <summary>Creates a descriptor for a step that had no matching step definition.</summary>
    public UndefinedStepDescriptor(Step undefinedStep, string customStepText)
    {
        UndefinedStep = undefinedStep;
        CustomStepText = customStepText;
    }

    /// <summary>The original Gherkin AST step that failed to match.</summary>
    public Step UndefinedStep { get; }

    /// <summary>An optional override for the step text (e.g. with scenario outline placeholders substituted).</summary>
    public string CustomStepText { get; }

    /// <summary>The effective step text: <see cref="CustomStepText"/> if set, otherwise the original step's text.</summary>
    public string StepText => CustomStepText ?? UndefinedStep.Text;
    /// <summary>The Given/When/Then block the step belongs to.</summary>
    public ScenarioBlock ScenarioBlock => ((IdeSupportGherkinStep) UndefinedStep).ScenarioBlock;
    /// <summary>True when the step has a data table argument.</summary>
    public bool HasDataTable => UndefinedStep.Argument is DataTable;
    /// <summary>True when the step has a doc string argument.</summary>
    public bool HasDocString => UndefinedStep.Argument is DocString;
}

/// <summary>
/// The outcome of matching a single Gherkin step against the project's step definitions:
/// either a defined match, an ambiguous match, or an undefined step, along with any errors.
/// </summary>
public class MatchResultItem
{
    private static readonly string[] EmptyErrors = Array.Empty<string>();

    private MatchResultItem(MatchResultType type, ProjectStepDefinitionBinding matchedStepDefinition,
        ParameterMatch parameterMatch, string[] errors, UndefinedStepDescriptor undefinedStep)
    {
        Type = type;
        MatchedStepDefinition = matchedStepDefinition;
        ParameterMatch = parameterMatch ?? throw new ArgumentNullException(nameof(parameterMatch));
        UndefinedStep = undefinedStep;
        Errors = errors ?? EmptyErrors;
    }

    /// <summary>Whether this item represents a defined, ambiguous, or undefined step match.</summary>
    public MatchResultType Type { get; }

    // step definition match
    /// <summary>The step definition this step matched, when <see cref="Type"/> is Defined or Ambiguous.</summary>
    public ProjectStepDefinitionBinding MatchedStepDefinition { get; }
    /// <summary>The parameter/argument match details for <see cref="MatchedStepDefinition"/>.</summary>
    public ParameterMatch ParameterMatch { get; }

    // undefined step
    /// <summary>Details of the step when <see cref="Type"/> is Undefined; otherwise null.</summary>
    public UndefinedStepDescriptor UndefinedStep { get; }

    /// <summary>Any error messages produced while matching this step.</summary>
    public string[] Errors { get; }
    /// <summary>True when <see cref="Errors"/> is non-empty.</summary>
    public bool HasErrors => Errors.Any();

    /// <summary>Returns a short description of the match outcome for diagnostics/logging.</summary>
    public override string ToString()
    {
        switch (Type)
        {
            case MatchResultType.Undefined:
                return "Undefined";
            case MatchResultType.Defined:
                return $"Defined: {MatchedStepDefinition}";
            case MatchResultType.Ambiguous:
                return $"Ambiguous: {MatchedStepDefinition}";
        }

        return "";
    }

    /// <summary>Creates a copy of this item reclassified as <see cref="MatchResultType.Ambiguous"/>.</summary>
    public MatchResultItem CloneToAmbiguousItem() => new(MatchResultType.Ambiguous,
        MatchedStepDefinition, ParameterMatch, Errors, null);

    /// <summary>Creates a <see cref="MatchResultType.Defined"/> item for a successfully matched step definition.</summary>
    public static MatchResultItem CreateMatch(ProjectStepDefinitionBinding matchedStepDefinition,
        ParameterMatch parameterMatch)
    {
        if (matchedStepDefinition == null) throw new ArgumentNullException(nameof(matchedStepDefinition));
        if (parameterMatch == null) throw new ArgumentNullException(nameof(parameterMatch));

        string[] errors = null;
        if (parameterMatch.HasError)
            errors = new[] {parameterMatch.Error};

        return new MatchResultItem(MatchResultType.Defined,
            matchedStepDefinition, parameterMatch, errors, null);
    }

    /// <summary>
    /// Creates an <see cref="MatchResultType.Undefined"/> item for a step with no matching step
    /// definition.
    /// </summary>
    /// <param name="nearMissErrors">
    /// Issue #514's "cheap first step": when an <em>invalid</em> binding's regex/scope would
    /// otherwise have matched this step (see <see cref="Bindings.ProjectStepDefinitionBinding.WouldMatchIgnoringValidity"/>),
    /// its <see cref="Bindings.ProjectBinding.Error"/> is passed here instead of leaving the step
    /// simply "not found" with no indication a near-miss binding exists. Surfaces through
    /// <see cref="MatchResult.GetErrorMessage"/> the same way an ambiguous-match message does.
    /// </param>
    public static MatchResultItem CreateUndefined(Step step, string customStepText,
        string[] nearMissErrors = null) =>
        new(MatchResultType.Undefined,
            null, ParameterMatch.NotMatch, nearMissErrors, new UndefinedStepDescriptor(step, customStepText));
}
