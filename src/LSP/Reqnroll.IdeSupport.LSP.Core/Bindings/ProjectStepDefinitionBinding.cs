#nullable disable

using Gherkin.Ast;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>A discovered Reqnroll step definition binding: its Given/When/Then type, matching regex, and scope.</summary>
public class ProjectStepDefinitionBinding : ProjectBinding
{
    /// <summary>Creates a step definition binding.</summary>
    public ProjectStepDefinitionBinding(ScenarioBlock stepDefinitionType, Regex regex, BindingScope scope,
        ProjectBindingImplementation implementation, string specifiedExpression = null, string error = null,
        int? attributeSourceLine = null, SourceLocation errorLocation = null)
    : base(implementation, scope, error, errorLocation)
    {
        StepDefinitionType = stepDefinitionType;
        Regex = regex;
        SpecifiedExpression = specifiedExpression;
        AttributeSourceLine = attributeSourceLine;
    }

    /// <summary>True when the binding also has a compiled matching <see cref="Regex"/>.</summary>
    public override bool IsValid => Regex != null && base.IsValid;
    /// <summary>The Given/When/Then block this step definition applies to.</summary>
    public ScenarioBlock StepDefinitionType { get; }
    /// <summary>The step definition's original expression text as authored, when known (as opposed to one derived from <see cref="Regex"/>).</summary>
    public string SpecifiedExpression { get; }
    /// <summary>The compiled regex used to match step text against this binding.</summary>
    public Regex Regex { get; }
    /// <summary>
    /// The 1-based source line of the binding attribute (e.g. the <c>[Given("...")]</c> line).
    /// Populated during syntax-based discovery, and backfilled via a Roslyn re-parse of the
    /// source file for connector-discovered bindings too (<c>ConnectorDiscoveryService</c>);
    /// <see langword="null"/> only when that backfill itself fails (e.g. source file unreadable).
    /// When set, <c>CoversQuery</c> uses this for exact AST-based matching instead of the heuristic line window.
    /// </summary>
    public int? AttributeSourceLine { get; }

    /// <summary>The expression to display for this binding: <see cref="SpecifiedExpression"/> if known, otherwise derived from <see cref="Regex"/>.</summary>
    public string Expression => SpecifiedExpression ?? GetSpecifiedExpressionFromRegex();

    /// <summary>
    /// True when this binding has no explicit attribute expression — a method-name-style binding
    /// (e.g. <c>[Given] public void The_First_Number_Is_P0(int p)</c>) that derives a matching
    /// regex from the method name/parameters instead. That regex is correct for matching but often
    /// unreadable — dumping it verbatim into UI is the bug reported in issue #344.
    /// </summary>
    public bool IsMethodNameStyle => SpecifiedExpression is null;

    /// <summary>
    /// A user-facing expression for surfaces that display this binding (diagnostics, completions,
    /// rename UI, unused-step-definitions lists): <see cref="SpecifiedExpression"/> when known,
    /// otherwise <see langword="null"/> — callers should omit the expression from their display
    /// entirely for a <see cref="IsMethodNameStyle"/> binding (the method name in
    /// <see cref="ProjectBinding.Implementation"/> already identifies it) rather than show a
    /// placeholder or <see cref="Expression"/>'s raw auto-generated regex (issue #344).
    /// </summary>
    public string DisplayExpression => SpecifiedExpression;

    private string GetSpecifiedExpressionFromRegex()
    {
        var result = Regex?.ToString();
        if (result == null)
            return null;

        // remove only ONE ^/$ from around the regex
        if (result.StartsWith("^"))
            result = result.Substring(1);
        if (result.EndsWith("$"))
            result = result.Substring(0, result.Length - 1);
        return result;
    }

    private static Regex GetRegexFromSpecifiedExpression(string expression) =>
        new($"^{expression}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Attempts to match this binding against a step: checks the scenario block, regex, and
    /// scope, then resolves the parameter match. Returns null if any of those fail.
    /// </summary>
    public MatchResultItem Match(Step step, IGherkinDocumentContext context, string stepText = null)
    {
        if (!IsValid || !(step is IdeSupportGherkinStep deveroomGherkinStep))
            return null;
        if (deveroomGherkinStep.ScenarioBlock != StepDefinitionType)
            return null;
        stepText = stepText ?? step.Text;
        var match = Regex.Match(stepText);
        if (!match.Success)
            return null;

        if (!MatchScope(context))
            return null;

        var parameterMatch = MatchParameter(step, match);
        return MatchResultItem.CreateMatch(this, parameterMatch);
    }

    /// <summary>
    /// Like <see cref="Match"/> but ignores <see cref="ProjectBinding.IsValid"/> — used only to
    /// find a near-miss for diagnostics (issue #514's "cheap first step"): an invalid binding
    /// (one with <see cref="ProjectBinding.Error"/> set) whose regex and scope would otherwise
    /// have matched a step that is reported undefined. Never used for real step-execution
    /// matching, which must keep excluding invalid bindings via <see cref="Match"/>.
    /// </summary>
    public bool WouldMatchIgnoringValidity(Step step, IGherkinDocumentContext context, string stepText = null)
    {
        if (Regex is null || !(step is IdeSupportGherkinStep deveroomGherkinStep))
            return false;
        if (deveroomGherkinStep.ScenarioBlock != StepDefinitionType)
            return false;
        stepText = stepText ?? step.Text;
        return Regex.Match(stepText).Success && MatchScope(context);
    }

    private ParameterMatch MatchParameter(Step step, Match match)
    {
        var parameterCount = Implementation.ParameterTypes?.Length ?? 0;
        var matchedStepParameters = match.Groups.OfType<Group>().Skip(1)
            .Where(g => g.Success)
            .Select(g => new MatchedStepTextParameter(g.Index, g.Length)).ToArray();
        var expectedParameterCount = matchedStepParameters.Length + (step.Argument == null ? 0 : 1);
        if (parameterCount != expectedParameterCount) //handle parameter error
            return new ParameterMatch(matchedStepParameters, step.Argument, Implementation.ParameterTypes,
                $"The method '{Implementation.Method}' has invalid parameter count, {expectedParameterCount} parameter(s) expected");
        return new ParameterMatch(matchedStepParameters, step.Argument, Implementation.ParameterTypes);
    }

    /// <summary>Returns a short description including the step type, expression (if authored), and implementation.</summary>
    public override string ToString() =>
        DisplayExpression is null
            ? $"[{StepDefinitionType}]: {Implementation}"
            : $"[{StepDefinitionType}({DisplayExpression})]: {Implementation}";

    /// <summary>Returns a copy of this binding with its expression (and derived regex) replaced.</summary>
    public ProjectStepDefinitionBinding WithSpecifiedExpression(string expression)
    {
        var regex = GetRegexFromSpecifiedExpression(expression);
        return new ProjectStepDefinitionBinding(StepDefinitionType, regex, Scope, Implementation, expression, Error, AttributeSourceLine, ErrorLocation);
    }
}
