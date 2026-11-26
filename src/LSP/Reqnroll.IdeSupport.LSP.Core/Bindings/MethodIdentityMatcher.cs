using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// Picks, among the method declarations of a source file that share a binding's method name, the
/// one its <see cref="MethodIdentity"/> describes: same namespace-qualified declaring type, same
/// parameter count, same parameter types.
/// </summary>
/// <remarks>
/// The source is parsed without a compilation, so parameter types are compared by their
/// spelling-independent simple names (<see cref="BindingMethodIdentity.NormalizeParameterType"/>),
/// the same normalization the connector and Roslyn discovery already share. Each narrowing step
/// that would leave no candidate is skipped rather than applied: the source may have changed since
/// the build that produced the identity, and the caller's name-only match is then the best
/// remaining answer, exactly as before identities existed.
/// </remarks>
internal static class MethodIdentityMatcher
{
    /// <summary>
    /// Narrows <paramref name="candidates"/> (declarations already matched by method name) to those
    /// <paramref name="identity"/> describes, returning them unchanged when there is no usable identity.
    /// </summary>
    public static IReadOnlyList<MethodDeclarationSyntax> Narrow(
        IReadOnlyList<MethodDeclarationSyntax> candidates, MethodIdentity? identity)
    {
        if (identity == null || candidates.Count == 0 ||
            !TryParse(identity.DocumentationCommentId, out var qualifiedName, out var parameterTypes))
            return candidates;

        var byType = Keep(candidates,
            m => string.Equals(RemoveWhitespace(StepDefinitionFileParser.FullMethodName(m)), qualifiedName, StringComparison.Ordinal));
        var byArity = Keep(byType, m => m.ParameterList.Parameters.Count == parameterTypes.Count);
        var byParameterTypes = byArity
            .Where(m => m.ParameterList.Parameters
                .Select(p => p.Type == null ? string.Empty : BindingMethodIdentity.NormalizeParameterType(p.Type.ToString()))
                .SequenceEqual(parameterTypes, StringComparer.Ordinal))
            .ToList();

        return byParameterTypes.Count == 1 ? byParameterTypes : byArity;
    }

    private static IReadOnlyList<MethodDeclarationSyntax> Keep(
        IReadOnlyList<MethodDeclarationSyntax> candidates, Func<MethodDeclarationSyntax, bool> predicate)
    {
        var kept = candidates.Where(predicate).ToList();
        return kept.Count == 0 ? candidates : kept;
    }

    /// <summary>
    /// Splits a method's documentation-comment ID into its namespace-qualified name
    /// (<c>Ns.Type.Method</c>) and its parameter types, normalized for comparison with source text.
    /// </summary>
    internal static bool TryParse(string? documentationCommentId, out string qualifiedName,
        out IReadOnlyList<string> parameterTypes)
    {
        qualifiedName = string.Empty;
        parameterTypes = Array.Empty<string>();
        if (documentationCommentId == null || !documentationCommentId.StartsWith("M:", StringComparison.Ordinal))
            return false;

        var body = documentationCommentId.Substring(2);
        var open = body.IndexOf('(');
        if (open < 0)
        {
            qualifiedName = body;
            return qualifiedName.Length > 0;
        }

        if (!body.EndsWith(")", StringComparison.Ordinal))
            return false;

        qualifiedName = body.Substring(0, open);
        var parameters = new List<string>();
        foreach (var parameter in SplitTopLevel(body.Substring(open + 1, body.Length - open - 2)))
        {
            var position = 0;
            var rendered = new StringBuilder();
            if (!TryRenderType(parameter, ref position, rendered) || position != parameter.Length)
                return false;
            parameters.Add(BindingMethodIdentity.NormalizeParameterType(rendered.ToString()));
        }

        parameterTypes = parameters;
        return qualifiedName.Length > 0;
    }

    // Splits on commas that are not inside a generic argument list or an array rank specifier.
    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{': case '[': depth++; break;
                case '}': case ']': depth--; break;
                case ',' when depth == 0:
                    yield return text.Substring(start, i - start);
                    start = i + 1;
                    break;
            }
        }
        if (text.Length > 0)
            yield return text.Substring(start);
    }

    // Renders one documentation-comment type as C# source would spell it, so that both sides go
    // through the same normalization: List{X} -> List<X>, System.Nullable{X} -> X?, X[0:,0:] -> X[,].
    // By-reference markers are dropped (bindings cannot have ref/out parameters).
    private static bool TryRenderType(string text, ref int position, StringBuilder output)
    {
        var nameStart = position;
        while (position < text.Length && "{}[],@*".IndexOf(text[position]) < 0)
            position++;
        var name = text.Substring(nameStart, position - nameStart);
        if (name.Length == 0)
            return false;

        if (position < text.Length && text[position] == '{')
        {
            position++;
            var arguments = new List<string>();
            while (true)
            {
                var argument = new StringBuilder();
                if (!TryRenderType(text, ref position, argument))
                    return false;
                arguments.Add(argument.ToString());
                if (position >= text.Length)
                    return false;
                if (text[position] == ',') { position++; continue; }
                if (text[position] == '}') { position++; break; }
                return false;
            }

            if (name == "System.Nullable" && arguments.Count == 1)
                output.Append(arguments[0]).Append('?');
            else
                output.Append(name).Append('<').Append(string.Join(",", arguments)).Append('>');
        }
        else
        {
            output.Append(name);
        }

        while (position < text.Length)
        {
            switch (text[position])
            {
                case '[':
                    var close = text.IndexOf(']', position);
                    if (close < 0) return false;
                    var rank = text.Substring(position + 1, close - position - 1).Split(',').Length;
                    output.Append('[').Append(new string(',', rank - 1)).Append(']');
                    position = close + 1;
                    continue;
                case '@':
                    position++;
                    continue;
                case '*':
                    output.Append('*');
                    position++;
                    continue;
            }
            break;
        }

        return true;
    }

    private static string RemoveWhitespace(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
