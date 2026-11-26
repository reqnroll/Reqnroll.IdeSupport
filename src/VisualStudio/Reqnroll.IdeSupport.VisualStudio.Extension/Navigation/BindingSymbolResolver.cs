#nullable enable

using Microsoft.CodeAnalysis;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

/// <summary>
/// Resolves a <see cref="BindingMethodIdentity"/> to the method symbol it names in a compilation:
/// the documentation-comment ID finds the candidates, and only one declared in the exact assembly
/// (name, version, culture and public key) is accepted.
/// </summary>
/// <remarks>
/// Ambiguity or absence yields <see langword="null"/>, never a guess: navigating to the wrong
/// method is worse than reporting that the binding could not be found. A version mismatch (the
/// project now references a different build of the binding assembly than the one discovery
/// reflected over) is such an absence, and is resolved by rebuilding.
/// </remarks>
internal static class BindingSymbolResolver
{
    /// <summary>The single method symbol <paramref name="identity"/> names in <paramref name="compilation"/>, or <see langword="null"/>.</summary>
    public static IMethodSymbol? Resolve(Compilation compilation, BindingMethodIdentity identity)
    {
        if (!identity.DocumentationCommentId.StartsWith("M:", StringComparison.Ordinal) ||
            !AssemblyIdentity.TryParseDisplayName(identity.AssemblyIdentity, out var assemblyIdentity))
            return null;

        var matches = DocumentationCommentId
            .GetSymbolsForDeclarationId(identity.DocumentationCommentId, compilation)
            .OfType<IMethodSymbol>()
            .Where(symbol => assemblyIdentity.Equals(symbol.ContainingAssembly?.Identity))
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }
}
