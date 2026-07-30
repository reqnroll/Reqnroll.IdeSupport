using System.Text;
using System.Text.RegularExpressions;
using Reqnroll.IdeSupport.LSP.Core.Bindings;

namespace Reqnroll.IdeSupport.LSP.Core.Completions;

/// <summary>
/// Produces a human-readable sample string from a <see cref="ProjectStepDefinitionBinding"/>'s
/// regex expression, substituting parameter placeholders such as <c>[int]</c> for capturing
/// groups.  Falls back to the raw regex string when the expression contains regex operators
/// outside capturing groups.
/// </summary>
public sealed class StepDefinitionSampler
{
    private static readonly Regex ChoiceParamRegex =
        new(@"^\(\s*[a-zA-Z0-9 ]+(?:\s*\|\s*[a-zA-Z0-9 ]+)*\s*\)$", RegexOptions.Compiled);

    private readonly RegexStepDefinitionExpressionAnalyzer _analyzer = new();

    /// <summary>Builds a human-readable sample string for the given binding's regex expression, substituting parameter placeholders for capturing groups.</summary>
    public string GetStepDefinitionSample(ProjectStepDefinitionBinding binding)
    {
        // Method-name-style bindings (no explicit attribute expression) have no authored step
        // text to sample from — their regex is auto-generated from the method name/parameters and
        // isn't valid Gherkin step text. Callers should filter these out before offering them as
        // completions (see CompletionService); this is a defensive fallback so a future caller
        // can't accidentally surface the raw regex (issue #344).
        if (binding.SpecifiedExpression is null)
            return binding.DisplayExpression;

        var expression = binding.Expression;
        var analyzed   = _analyzer.Parse(expression);

        // Single part with no parameter groups — plain text, just unescape it
        if (analyzed.Parts.Length == 1)
            return GetUnescapedText(analyzed.Parts[0]);

        // Any text part outside capturing groups contains regex operators → fall back
        if (!analyzed.ContainsOnlySimpleText)
            return expression;

        var sb = new StringBuilder();
        for (int i = 0; i < analyzed.Parts.Length; i += 2)
        {
            sb.Append(GetUnescapedText(analyzed.Parts[i]));

            if (i < analyzed.Parts.Length - 1)
            {
                var paramText = GetUnescapedText(analyzed.Parts[i + 1]);
                if (IsChoiceParameter(paramText))
                    sb.Append(paramText);
                else
                {
                    sb.Append('[');
                    sb.Append(GetPlaceholderText(binding, i / 2));
                    sb.Append(']');
                }
            }
        }

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetUnescapedText(AnalyzedStepDefinitionExpressionPart part)
        => part is AnalyzedStepDefinitionExpressionSimpleTextPart simple
            ? simple.UnescapedText
            : part.ExpressionText;

    private static bool IsChoiceParameter(string paramText)
        => ChoiceParamRegex.IsMatch(paramText);

    private static string GetPlaceholderText(ProjectStepDefinitionBinding binding, int groupIndex)
    {
        var paramTypes = binding.Implementation?.ParameterTypes;
        if (paramTypes is null || groupIndex >= paramTypes.Length)
            return "???";

        var typeName = paramTypes[groupIndex];
        if (typeName == "System.Int32")  return "int";
        if (typeName == "System.String") return "string";
        var dotIndex = typeName.LastIndexOf('.');
        return dotIndex >= 0 ? typeName.Substring(dotIndex + 1) : typeName;
    }
}
