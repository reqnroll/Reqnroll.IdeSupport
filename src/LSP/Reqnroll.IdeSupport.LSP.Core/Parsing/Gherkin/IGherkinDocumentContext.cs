#nullable disable
namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>
/// A node in the ancestor chain of a parsed Gherkin document element (e.g. Feature, Rule,
/// Scenario, Step), letting callers walk up to enclosing nodes and their tags.
/// </summary>
public interface IGherkinDocumentContext
{
    /// <summary>The enclosing context, or null at the document root.</summary>
    IGherkinDocumentContext Parent { get; }
    /// <summary>The Gherkin AST node this context wraps.</summary>
    object Node { get; }
}
