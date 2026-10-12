#nullable enable
using Cucumber.TagExpressions;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.TagExpressions;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// One scope as Reqnroll's runtime registers it: a single <c>[Scope]</c> attribute, a single hook
/// tag, or a connector <c>StepScope</c>. Tag, feature and scenario title are all required within
/// one scope (AND). <see cref="Error"/> carries a problem reported by the discovery source itself.
/// </summary>
/// <param name="Tag">The tag expression text, or <see langword="null"/> for no tag restriction.</param>
/// <param name="Feature">The required feature title, or <see langword="null"/>.</param>
/// <param name="Scenario">The required scenario title, or <see langword="null"/>.</param>
/// <param name="Error">An error reported for this scope by its discovery source, or <see langword="null"/>.</param>
public sealed record ScopeSpec(string? Tag, string? Feature, string? Scenario, string? Error = null)
{
    /// <summary>Gets whether this scope restricts nothing and carries no error.</summary>
    public bool IsUnrestricted =>
        Error == null && string.IsNullOrWhiteSpace(Tag) && Feature == null && Scenario == null;
}

/// <summary>
/// The single place that turns the scopes of one binding attribute into the form every consumer
/// sees. Both discovery paths feed it: the connector path (the runtime registry, one entry per
/// scope) and the Roslyn path (the same union built from <c>[Scope]</c> attributes and hook tags),
/// so the same source yields the same <see cref="BindingScope"/> whichever path discovered it.
/// </summary>
/// <remarks>
/// <para>
/// Reqnroll registers one binding per scope over the union of class-level scopes, method-level
/// scopes and (for hooks) each hook tag (<c>BindingSourceProcessor.ProcessMethod</c> and
/// <c>ProcessHookAttribute</c>), so the scopes are alternatives (OR) and a binding is in scope when
/// any one of them matches. Within one scope, tag, feature and scenario must all match (AND).
/// </para>
/// <para>
/// <see cref="Normalize"/> keeps that meaning exactly while reducing the list to a canonical form,
/// using only rewrites that cannot change which scenarios match: an unrestricted scope makes the
/// whole binding unrestricted; scopes with the same feature and scenario title merge, OR-ing their
/// tags (<c>(t1 AND F AND S) OR (t2 AND F AND S) = (t1 OR t2) AND F AND S</c>), and a scope with
/// that title but no tag absorbs the tagged ones (<c>F OR (t AND F) = F</c>); duplicates collapse.
/// What remains is sorted, so the result does not depend on attribute enumeration order.
/// </para>
/// </remarks>
public static class ScopeNormalizer
{
    /// <summary>
    /// Reduces <paramref name="scopes"/> (alternatives, OR) to canonical form. Returns
    /// <see langword="null"/> when the binding is unrestricted: no scopes at all, or any scope that
    /// restricts nothing (a <see langword="null"/> element counts as such). A scope that carries an
    /// <see cref="ScopeSpec.Error"/> is never merged or absorbed, so its error stays visible.
    /// </summary>
    public static IReadOnlyList<ScopeSpec>? Normalize(IEnumerable<ScopeSpec?> scopes)
    {
        var all = scopes.ToList();
        if (all.Count == 0 || all.Any(scope => scope == null || scope.IsUnrestricted))
            return null;

        var clean = new List<ScopeSpec>();
        var withErrors = new List<ScopeSpec>();
        foreach (var scope in all.Select(scope => Canonical(scope!)).Distinct())
            (scope.Error == null ? clean : withErrors).Add(scope);

        var merged = clean
            .GroupBy(scope => (scope.Feature, scope.Scenario))
            .OrderBy(group => group.Key.Feature ?? "", StringComparer.Ordinal)
            .ThenBy(group => group.Key.Scenario ?? "", StringComparer.Ordinal)
            .Select(Merge);

        return merged.Concat(withErrors).ToArray();
    }

    /// <summary>
    /// Builds the <see cref="BindingScope"/> for normalized <paramref name="scopes"/>: <see langword="null"/>
    /// when unrestricted, an ordinary single scope for one entry, and a scope with
    /// <see cref="BindingScope.Alternatives"/> for several. Invalid tag expressions become the scope's
    /// <see cref="BindingScope.Error"/>, as does an error the discovery source reported.
    /// </summary>
    public static BindingScope? ToBindingScope(IReadOnlyList<ScopeSpec>? scopes, IReqnrollTagExpressionParser tagParser)
    {
        if (scopes == null || scopes.Count == 0)
            return null;
        if (scopes.Count == 1)
            return Build(scopes[0], tagParser);

        var alternatives = scopes.Select(scope => Build(scope, tagParser) ?? new BindingScope()).ToArray();
        var errors = alternatives.Select(alternative => alternative.Error).Where(error => !string.IsNullOrEmpty(error)).ToArray();
        return new BindingScope
        {
            Alternatives = alternatives,
            Error = errors.Length == 0 ? null : string.Join("\n", errors)
        };
    }

    /// <summary>Normalizes <paramref name="scopes"/> and builds the resulting <see cref="BindingScope"/> in one step.</summary>
    public static BindingScope? Build(IEnumerable<ScopeSpec?> scopes, IReqnrollTagExpressionParser tagParser) =>
        ToBindingScope(Normalize(scopes), tagParser);

    private static ScopeSpec Merge(IGrouping<(string? Feature, string? Scenario), ScopeSpec> group)
    {
        var (feature, scenario) = group.Key;
        if (group.Any(scope => scope.Tag == null))
            return new ScopeSpec(null, feature, scenario);

        var tags = group.Select(scope => scope.Tag!).Distinct().OrderBy(tag => tag, StringComparer.Ordinal).ToArray();
        var tag = tags.Length == 1 ? tags[0] : string.Join(" or ", tags.Select(t => $"({t})"));
        return new ScopeSpec(tag, feature, scenario);
    }

    // Reqnroll drops the leading '@' of a single tag name and accepts it with or without one, while
    // the tag expression parser requires '@' on every name of a multi-term expression, so a bare
    // name is given its '@' before it can be combined with another.
    private static ScopeSpec Canonical(ScopeSpec scope)
    {
        var tag = scope.Tag?.Trim();
        if (string.IsNullOrEmpty(tag))
            return scope with { Tag = null };

        var text = tag!;
        var isSingleName = !text.Any(c => char.IsWhiteSpace(c) || c == '(' || c == ')');
        if (isSingleName && !text.StartsWith("@", StringComparison.Ordinal) && !IsOperator(text))
            tag = "@" + text;

        return scope with { Tag = tag };
    }

    private static bool IsOperator(string text) =>
        text is "and" or "or" or "not";

    private static BindingScope? Build(ScopeSpec scope, IReqnrollTagExpressionParser tagParser)
    {
        if (scope.IsUnrestricted)
            return null;

        ITagExpression? tagExpression = null;
        var error = scope.Error;
        if (!string.IsNullOrWhiteSpace(scope.Tag))
        {
            var parsed = tagParser.Parse(scope.Tag!);
            if (parsed is InvalidTagExpression invalid)
            {
                var tagError = $"Invalid tag expression '{scope.Tag}': {invalid.Message}";
                error = error == null ? tagError : $"{error}\n{tagError}";
            }
            else
            {
                tagExpression = parsed;
            }
        }

        return new BindingScope
        {
            Tag = tagExpression,
            FeatureTitle = scope.Feature,
            ScenarioTitle = scope.Scenario,
            Error = error
        };
    }
}
