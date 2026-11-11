# Document Outline and Code Folding (F9, F10)

**Status:** Implemented. Describes the system as it is; verify against the source files named in each section.
**Audience:** Maintainers working on `documentSymbol`, folding ranges, or their per-IDE rendering.

## 1. Purpose and scope

- **Outline (F9).** The IDE's Outline or Structure view shows Feature → Background / Rule → Scenario / Scenario Outline →
  Step; clicking a node navigates. In Visual Studio it feeds the navigation-bar dropdowns above the editor.
- **Folding (F10).** Scenarios, Backgrounds, Rules, doc strings and data tables collapse in the gutter and update as the
  document is edited.

Both are computed from the cached tag tree, the same one semantic tokens read.

## 2. Overview

```mermaid
flowchart LR
    TAG["Document buffer<br/>IdeSupportTag[]"] --> DSS["DocumentSymbolService<br/>(LSP.Core)"]
    TAG --> FRS["FoldingRangeService<br/>(LSP.Core)"]
    DSS --> DSH["DocumentSymbolHandler<br/>documentSymbol"]
    FRS --> FRH["FoldingRangeHandler<br/>foldingRange"]
    DSH --> VSC["VS Code: native Outline"]
    DSH --> VSN["Visual Studio:<br/>reqnroll/documentSymbolHierarchical<br/>→ navigation-bar dropdowns"]
    FRH --> VSF["VS Code, Visual Studio: native folding"]
    TREE["Rider: Gherkin PSI tree"] --> RDF["ReqnrollFeatureFoldingBuilder,<br/>ReqnrollFeatureStructureViewFactory"]
```

## 3. Flows

```mermaid
sequenceDiagram
    participant IDE
    participant H as DocumentSymbolHandler / FoldingRangeHandler
    participant B as Document buffer
    participant S as DocumentSymbolService / FoldingRangeService

    IDE->>H: textDocument/documentSymbol (or foldingRange)
    H->>B: IdeSupportTag tree by URI
    B-->>H: tags
    H->>S: build hierarchy (or ranges)
    S-->>H: protocol-agnostic model
    H->>H: convert to OmniSharp DocumentSymbol[] / FoldingRange[]
    H-->>IDE: response
    IDE-->>IDE: render outline panel / gutter fold markers
```

## 4. Glossary

| Term | Meaning here |
|---|---|
| **Document symbol** | One node of the outline; hierarchical `DocumentSymbol` form. |
| **Navigation bar** | Visual Studio's dropdown bar above the editor (`IVsDropdownBarClient`). |
| **Folding range** | A start/end line pair the IDE may collapse. |
| **Foldable regions** | Scenario, Background, Rule, doc string and table nodes. |
| **Structure view** | Rider's name for its outline tool window. |

## 5. Key classes

| Class | Location | Responsibility |
|---|---|---|
| `DocumentSymbolService`, `GherkinDocumentSymbol`, `IDocumentSymbolService` | `src/LSP/…LSP.Core/DocumentOutline` | Walks the tag tree to a protocol-agnostic hierarchy. |
| `DocumentSymbolHandler` | `…LSP.Server/Features/DocumentOutline` | Converts to OmniSharp `DocumentSymbol[]`. |
| `FoldingRangeService`, `GherkinFoldingRange`, `IFoldingRangeService` | `…LSP.Core/Folding` | Computes ranges. |
| `FoldingRangeHandler` | `…LSP.Server/Features/Folding` | Statically registered handler. |
| `GherkinNavigationBarSymbolService`, `GherkinDropdownBarClient` | `src/VisualStudio/…Extension/NavigationBar`, `…VSSDKIntegration/NavigationBar` | Visual Studio navigation bar. |
| `ReqnrollFeatureFoldingBuilder`, `ReqnrollFeatureStructureViewFactory` | `src/Rider/…/psi` | Rider folding and Structure view, computed from the PSI tree. |

## 6. Behaviour and contracts

**Symbol kinds:** Feature→Module, Background→Constructor, Rule→Namespace, Scenario / Scenario Outline→Method,
Step→Field, Examples→Array. `DocumentSymbol.Children` is `init`-only, so children are wrapped in
`Container<DocumentSymbol>` in the object initializer. The `Range` of a container symbol is its full body (not its header
line) so clients can resolve things "inside" it; the `SelectionRange` is the header.

**Visual Studio outline.** Visual Studio registers the `documentSymbol` capability but never sends the request: its
Document Outline window (Ctrl+Alt+T) uses COM / `IVsHierarchy`. It does consume symbol information for the navigation bar
and Go to Member, but those need content-type plumbing for `.feature`. The client therefore uses a custom
`reqnroll/documentSymbolHierarchical` request and drives the dropdown bar itself. The Document Outline window is not
populated (open question O2 in the [register](../LSP-IDE-Support-Open-Questions.md#1-open-questions)). The navigation-bar
listener attaches by `[ContentType("Gherkin")]`, so it depends on the activation work in
[../VS-LSP-Activation.md](../VS-LSP-Activation.md), and is gated off in diff views.

**Rider.** Both come from the Gherkin PSI tree (the tree is built on the Cucumber Gherkin parser), not from the server:
`ReqnrollFeatureFoldingBuilder` is a `FoldingBuilder` and `ReqnrollFeatureStructureViewFactory` a
`PsiStructureViewFactory`, both registered for the Gherkin language. They are available as soon as the file opens and on
every supported Rider version, including those whose generic LSP client has no outline. A Feature, Rule, Background,
Scenario, Examples block, doc string or table folds from the end of its first line (a Feature or Rule from the end of its
keyword line, not a tag line above it) to the end of its content. The platform keeps each region's expand/collapse state
across edits. The Structure view is registered for Gherkin, which takes precedence over the platform's own generic outline
(registered for any language), so it replaces the `documentSymbol`-driven view on newer Rider. The server's
`documentSymbol` and `foldingRange` handlers are still used by VS Code and Visual Studio.

**VS Code and Visual Studio** render folding purely through the generic LSP client.

## 7. Pitfalls and design decisions

1. **Keep the Rider tree-based folding in step with the server's ranges.** Both follow the same rules (the blocks listed
   above); a block that folds in one IDE should fold in the others.
2. **The Rider outline and folds must not need the server.** They are computed from the tree so they work before the
   server has started.
3. **Do not expect Visual Studio to request `documentSymbol`.** Use the custom request for the dropdown bar.
4. **Keep the symbol model protocol-agnostic in Core.** Conversion to OmniSharp types belongs in the handler.

## 8. Known limitations

- Visual Studio's Document Outline window is not populated; the navigation-bar dropdowns are.
- Rider's Structure view shows Feature, Background, Rule, Scenario, Step and Examples nodes without icons.

## 9. Testing

| Layer | Where |
|---|---|
| Symbol and folding services | `tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/DocumentOutline`, `…/Folding` |
| Handlers | `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/DocumentOutline`, `…/Folding` |
| End to end | `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs/Features/Editor/DocumentOutline.feature`, `CodeFolding.feature` |
