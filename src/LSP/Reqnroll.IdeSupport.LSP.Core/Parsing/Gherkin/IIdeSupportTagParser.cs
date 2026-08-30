using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;





namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>
/// Walks a parsed feature document and produces the flattened <c>IdeSupportTag</c> tree consumed
/// for semantic tokens, diagnostics, and step binding matches.
/// </summary>
public interface IIdeSupportTagParser
{
    /// <summary>
    /// Parse <paramref name="fileSnapshot"/> and return Deveroom tags annotated with
    /// binding matches from <paramref name="bindingRegistry"/>.
    /// Pass <see cref="ProjectBindingRegistry.Invalid"/> when no registry is available yet;
    /// step-matching tags will simply be omitted.
    /// </summary>
    IReadOnlyCollection<IdeSupportTag> Parse(
        IGherkinTextSnapshot fileSnapshot,
        ProjectBindingRegistry bindingRegistry);
}
