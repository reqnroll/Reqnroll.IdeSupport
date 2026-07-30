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
        int? attributeSourceLine = null)
    : base(implementation, scope, error)
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
    /// Populated during syntax-based discovery; <see langword="null"/> for connector-discovered bindings.
    /// When set, <c>CoversQuery</c> uses this for exact AST-based matching instead of the heuristic line window.
    /// </summary>
    public int? AttributeSourceLine { get; }

    /// <summary>The expression to display for this binding: <see cref="SpecifiedExpression"/> if known, otherwise derived from <see cref="Regex"/>.</summary>
    public string Expression => SpecifiedExpression ?? GetSpecifiedExpressionFromRegex();

    /// <summary>
    /// A user-facing expression for surfaces that display this binding (diagnostics, completions,
    /// rename UI, unused-step-definitions lists): <see cref="SpecifiedExpression"/> when the
    /// binding was authored with an explicit attribute string, otherwise a short indicator instead
    /// of <see cref="Expression"/>'s raw auto-generated regex. Method-name-style bindings (e.g.
    /// <c>[Given] public void The_First_Number_Is_P0(int p)</c>) derive a regex from the method
    /// name/parameters that is correct for matching but often unreadable — dumping it verbatim
    /// into UI is the bug reported in issue #344.
    /// </summary>
    public string DisplayExpression => SpecifiedExpression ?? "(method-name pattern)";

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
        if (!IsValid || !(step is DeveroomGherkinStep deveroomGherkinStep))
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

    /// <summary>Returns a short description including the step type, expression, and implementation.</summary>
    public override string ToString() => $"[{StepDefinitionType}({DisplayExpression})]: {Implementation}";

    /// <summary>Returns a copy of this binding with its expression (and derived regex) replaced.</summary>
    public ProjectStepDefinitionBinding WithSpecifiedExpression(string expression)
    {
        var regex = GetRegexFromSpecifiedExpression(expression);
        return new ProjectStepDefinitionBinding(StepDefinitionType, regex, Scope, Implementation, expression, Error, AttributeSourceLine);
    }
}
