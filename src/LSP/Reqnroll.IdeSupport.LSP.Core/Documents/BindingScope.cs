using Cucumber.TagExpressions;

namespace Reqnroll.IdeSupport.LSP.Core.Documents;

/// <summary>BindingScope</summary>
public class BindingScope
{
    /// <summary>Gets or sets the tag.</summary>
    public ITagExpression? Tag { get; set; }
    /// <summary>Gets or sets the feature title.</summary>
    public string? FeatureTitle { get; set; }
    /// <summary>Gets or sets the scenario title.</summary>
    public string? ScenarioTitle { get; set; }
    /// <summary>Gets or sets the error.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets alternative scopes, any one of which puts the binding in scope (issue #953):
    /// Reqnroll treats several <c>[Scope]</c> attributes on one binding as alternatives (OR),
    /// which a single tag/feature/scenario triple cannot express when the alternatives restrict
    /// different properties (e.g. <c>[Scope(Tag = "@a")]</c> + <c>[Scope(Feature = "F")]</c>).
    /// When set, <see cref="Tag"/>, <see cref="FeatureTitle"/> and <see cref="ScenarioTitle"/>
    /// are <see langword="null"/>, and <see cref="Error"/> combines the alternatives' errors.
    /// <see langword="null"/> for the ordinary single-scope case.
    /// </summary>
    public IReadOnlyList<BindingScope>? Alternatives { get; set; }

    /// <summary>Gets whether the scope has no <see cref="Error"/> set.</summary>
    public bool IsValid => Error == null;

    /// <summary>
    /// Formats the tag expression together with any feature/scenario title and error, comma-separated.
    /// Alternatives (see <see cref="Alternatives"/>) are each formatted that way, parenthesized and
    /// joined with <c>" or "</c>; their own errors are included there rather than repeated.
    /// </summary>
    public override string ToString()
    {
        if (Alternatives != null)
            return string.Join(" or ", Alternatives.Select(alternative => $"({alternative})"));

        var result = Tag?.ToString() ?? "";
        if (FeatureTitle != null)
        {
            result = result.Length > 0 ? result + ", " : result;
            result = $"{result}Feature='{FeatureTitle}'";
        }
        if (ScenarioTitle != null)
        {
            result = result.Length > 0 ? result + ", " : result;
            result = $"{result}Scenario='{ScenarioTitle}'";
        }
        if (Error != null)
        {
            result = result.Length > 0 ? result + ", " : result;
            result = $"{result}Error='{Error}'";
        }
        return result;
    }
}
