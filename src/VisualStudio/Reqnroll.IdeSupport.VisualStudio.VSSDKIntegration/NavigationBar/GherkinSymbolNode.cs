#nullable enable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.NavigationBar;

/// <summary>0-based line/character position, matching LSP <c>Position</c>.</summary>
public readonly record struct GherkinSymbolPosition(int Line, int Character);

/// <summary>Matches LSP <c>Range</c> — a <see cref="Start"/>/<see cref="End"/> position pair.</summary>
public readonly record struct GherkinSymbolRange(GherkinSymbolPosition Start, GherkinSymbolPosition End);

/// <summary>
/// Client-side, protocol-agnostic mirror of an LSP <c>DocumentSymbol</c> node, used to populate
/// the Navigation Bar drop-downs from the LSP server's document-symbol data (see
/// <c>GherkinNavigationBarSymbolService</c> for the design rationale). Deliberately independent of any
/// OmniSharp/Newtonsoft type so this VSSDK-hosted (net481, classic COM) project doesn't need to
/// reference the LSP client libraries — <c>GherkinNavigationBarSymbolService</c> in the Extension
/// project does the JSON parsing and hands over already-mapped instances of this type.
/// </summary>
public sealed record GherkinSymbolNode(
    string                            Name,
    int                               Kind,
    GherkinSymbolRange                Range,
    GherkinSymbolRange                SelectionRange,
    IReadOnlyList<GherkinSymbolNode>  Children,
    /// <summary>
    /// Mirrors LSP <c>DocumentSymbol.detail</c> — carries the finer-grained Gherkin construct name
    /// (e.g. <c>"Scenario Outline"</c> vs <c>"Scenario"</c>) that <see cref="Kind"/> alone can't
    /// express, since both collapse to the same LSP <c>SymbolKind.Method</c> value. <see langword="null"/>
    /// when the server didn't set one (e.g. Feature/Rule/Step nodes).
    /// </summary>
    string?                           Detail = null);
