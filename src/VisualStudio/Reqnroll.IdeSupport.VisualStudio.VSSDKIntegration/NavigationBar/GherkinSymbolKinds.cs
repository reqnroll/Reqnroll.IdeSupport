#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.NavigationBar;

/// <summary>
/// LSP <c>SymbolKind</c> values used by <c>GherkinDocumentSymbolService</c> (LSP.Core) /
/// <c>DocumentSymbolHandler</c> for Gherkin nodes. Kept in sync with the Document Outline
/// design's mapping: Feature→Module, Background→Constructor, Rule→Namespace,
/// Scenario/ScenarioOutline→Method, Step→Field, Examples→Array.
/// </summary>
internal static class GherkinSymbolKinds
{
    public const int Feature    = 2;  // Module
    public const int Rule       = 3;  // Namespace
    public const int Background = 9;  // Constructor
    public const int Scenario   = 6;  // Method (covers Scenario and ScenarioOutline)
    public const int Step       = 8;  // Field
    public const int Examples   = 18; // Array

    /// <summary>True for nodes that act as a Navigation Bar "container" (combo 0) rather than a leaf (combo 1).</summary>
    public static bool IsContainer(int kind) => kind is Feature or Rule or Background or Scenario;
}
