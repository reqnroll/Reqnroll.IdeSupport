using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.LanguageServer;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>Contributes the <c>Gherkin</c> document type for <c>.feature</c> files, backed by the LSP server.</summary>
internal static class GherkinDocumentType
{
    /// <summary>Document type configuration mapping the <c>.feature</c> extension to the LSP-backed Gherkin document type.</summary>
    [VisualStudioContribution]
    internal static DocumentTypeConfiguration GherkinDocument => new("Gherkin")
    {
        FileExtensions = new[] { ".feature" },
        BaseDocumentType = LanguageServerProvider.LanguageServerBaseDocumentType,
    };
}
