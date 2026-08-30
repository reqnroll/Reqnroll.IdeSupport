using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.DocumentOutline;

/// <summary>Builds the hierarchical document outline (Document Symbol) tree for a feature file from its parsed tags.</summary>
public interface IGherkinDocumentSymbolService
{
    /// <summary>Builds the outline symbol tree (feature/rule/scenario/steps) from a feature document's flattened tags.</summary>
    IReadOnlyList<GherkinDocumentSymbol> BuildSymbols(IReadOnlyCollection<IdeSupportTag> tags);
}
