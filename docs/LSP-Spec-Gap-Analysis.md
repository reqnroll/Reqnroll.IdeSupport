# LSP Specification Gap Analysis

> **Status: Deferred (2026-09-25).** None of the gaps below are being pursued for now. This
> analysis was originally captured in
> [issue #128](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/128) (opened 2026-07-11),
> which has been closed with the decision recorded here so the analysis and prioritization aren't
> lost. Maintainer verdict on the issue: *"This is a good list, but none of these would be required
> right now."*
>
> The analysis below is preserved as written (a snapshot as of 2026-07-11). See
> [Status updates since the analysis](#status-updates-since-the-analysis) for what has changed
> since, and re-verify against the code before picking any item up.

A comparison of the [LSP 3.17 specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification)
against the Reqnroll.IdeSupport LSP server implementation, identifying standard LSP features that
are not yet implemented but could add value for Gherkin / Reqnroll users.

## Legend

- ✅ **Implemented** — production handler exists
- ❌ **Missing** — no handler, no capability declaration
- ⚠️ **Partial** — some aspect covered, but not full spec
- 🔄 **Custom equiv.** — has a custom `reqnroll/*` replacement instead of the standard LSP method

## Status updates since the analysis

| Item | Update |
|---|---|
| All | **Deferred** — issue #128 closed 2026-09-25; nothing scheduled. |
| 5. `workspace/executeCommand` | ⚠️ **Partial.** The server now handles `workspace/executeCommand` for one command, `reqnroll.toggleComment` (`CommentToggleHandler`). The other operations listed below are still custom `reqnroll/*` requests only. |
| 6. `textDocument/documentLink` | Clickable tags (e.g. tags carrying issue/work-item identifiers, as the legacy VS extension supported) are tracked separately as [issue #755](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/755) — likely the main use case for `documentLink`. |
| 9. Pull diagnostics | **Abandoned**, not just deferred — `OmniSharp.Extensions.LanguageServer` 0.19.9's server-side JSON converters for the pull-diagnostics report types are `NotImplementedException` stubs. See [Open Questions Q19](LSP-IDE-Support-Open-Questions.md) and the archived [PullDiagnostics-Implementation-Plan.md](Archive/PullDiagnostics-Implementation-Plan.md). |
| Protocol ceiling | Any future work here is bounded by OmniSharp 0.19.9's LSP 3.17 ceiling — see the "Protocol version ceiling" note in [LSP-IDE-Support-Architecture.md](LSP-IDE-Support-Architecture.md). |

---

## What was already implemented (at time of analysis)

The server already covered the high-value Gherkin features well:

| Feature | Handler |
|---|---|
| `textDocument/didOpen` / `didChange` / `didClose` | `TextDocumentSyncHandler` |
| `textDocument/completion` | `GherkinCompletionHandler` |
| `textDocument/definition` | `FeatureDefinitionHandler` |
| `textDocument/references` | `StepReferencesHandler` |
| `textDocument/codeAction` | `FeatureCodeActionHandler` (Define Steps scaffold) |
| `textDocument/codeLens` | `StepCodeLensHandler` |
| `textDocument/formatting` | `GherkinFormattingHandler` |
| `textDocument/documentSymbol` | `FeatureDocumentSymbolHandler` |
| `textDocument/foldingRange` | `FeatureFoldingRangeHandler` |
| `textDocument/prepareRename` / `rename` | `StepRenameHandler` |
| `textDocument/inlayHint` | `FeatureInlayHintHandler` |
| `textDocument/semanticTokens/full` + `full/delta` | `SemanticTokensHandler` |
| `workspace/didChangeWatchedFiles` | `WatchedFilesHandler` |
| `workspace/didChangeWorkspaceFolders` | `WorkspaceFoldersHandler` |
| `textDocument/publishDiagnostics` (push) | `DiagnosticsPublishHandler` |
| `$/setTrace` / `$/logTrace` | `SetTraceNotificationHandler` |

Plus custom `reqnroll/*` extensions for: project lifecycle, findStepUsages, goToStepDefinitions,
goToHooks, findUnusedStepDefinitions, renameTargets, selectRenameTarget, refreshCodeLens,
documentSymbolHierarchical, documentActivated. See [LSP-Protocol-Extensions.md](LSP-Protocol-Extensions.md).

---

## Missing standard LSP features

### 1. 🔴 `textDocument/hover` — HIGH VALUE

**Spec:** LSP 3.0+ — shows contextual information when the user hovers over a symbol.

**What this would unlock:** The most obvious UX gap. Users should be able to hover over any
Gherkin step and see:

- The matched step definition's full regex or Cucumber expression
- Source file location (project, file, line)
- The step definition's method signature and containing class
- For undefined steps: "Step definition not found" + inline hint to create one
- For tags: tag-level documentation
- For Scenario Outline placeholders (`<param>`): the data table column they reference

**Implementation effort:** Low. The match cache already has `FeatureBindingMatchSet` with `Steps`
containing `MatchedStepDefinition` that holds `Implementation` (method name, source location,
expression). The binding registry also has the full `ProjectStepDefinitionBinding` with
`Regex`/expression patterns. It's essentially the same data path as `textDocument/definition` but
returning `Hover` content instead of `Location`.

**Note:** `DiagnosticsAggregator` already has an `UndefinedStepMessage` constant
(`"Step definition not found."`), suggesting hover was considered during design.

---

### 2. 🔴 `workspace/symbol` — HIGH VALUE

**Spec:** LSP 3.0+ — workspace-wide symbol search (Ctrl+T / Cmd+T).

**What this would unlock:** Users could press Ctrl+T (VS Code) or Ctrl+, (VS) and search across
all `.feature` files for:

- Feature names
- Scenario names
- Scenario Outline names
- Tag names (`@tagname`)
- Step text patterns

This is a genuine power-user feature for navigating large feature file collections — every named
element should be discoverable via workspace symbol search rather than manual file browsing.

**Implementation effort:** Medium. Requires building a workspace-level index of all `.feature`
files parsed by the server. The server already has `ILspWorkspaceScopeManager.GetAllScopes()` and
`IGherkinDocumentTaggerService` — the building blocks exist to enumerate all open/known feature
files and extract symbols from their parsed Gherkin documents.

---

### 3. 🟡 `window/workDoneProgress` — MEDIUM VALUE

**Spec:** LSP 3.15+ — server-initiated progress reporting via `$/progress` notifications.

**What this would unlock:** The server could report progress during long operations:

- Initial binding discovery on solution load: "Reqnroll: discovering bindings (42%)"
- FindUnusedStepDefinitions scanning across all projects
- Full workspace rescan after a large build

**Implementation effort:** Low. The progress infrastructure is standard LSP. The server already has
`OperationDurationRecorder` and `ILanguageServerFacade` — wiring progress into the existing
long-running operations would be a small addition.

---

### 4. 🟡 `textDocument/documentHighlight` — MEDIUM VALUE

**Spec:** LSP 3.0+ — highlights all occurrences of the symbol at the cursor position in the
current document.

**What this would unlock:** When the cursor is on a step (e.g. `Given I have entered 50`), all
other occurrences of the same step in the file would be highlighted. Especially useful for:

- Reading long feature files with repeated steps
- Scenario Outlines where the same step appears in multiple examples
- Verifying step text consistency across the file

**Implementation effort:** Low. The match cache already has all steps with their ranges. A handler
would find the step at the cursor position, then enumerate all steps with the same expression text
and return their ranges.

---

### 5. 🟡 `workspace/executeCommand` — MEDIUM VALUE

**Spec:** LSP 3.0+ — standard mechanism for executing commands on the server.

**What this would unlock:** Instead of relying solely on custom `reqnroll/*` request methods,
commands could be registered as `workspace/executeCommand` entries. This makes them discoverable
through VS Code's command palette and the standard LSP command infrastructure. Examples:

- `reqnroll.findUnusedStepDefinitions`
- `reqnroll.goToHooks`
- `reqnroll.renameStep`

**Implementation effort:** Low. The server already handles these operations — it's a matter of also
registering them as `executeCommand` commands.

---

### 6. 🟡 `textDocument/documentLink` — LOWER VALUE

**Spec:** LSP 3.0+ — renders clickable links in the document.

**What this would unlock:** Feature files could contain navigable links to:

- Feature files referenced in tags or backgrounds
- Navigation markers within a feature file
- Links to external documentation or issue trackers from tags (see issue #755)

**Implementation effort:** Low. The Gherkin parser already identifies all document elements.

---

### 7. 🟡 `textDocument/implementation` — LOWER VALUE (for .cs files)

**Spec:** LSP 3.0+ — navigate from an interface/abstract member to its implementations.

**What this would unlock:** From a `.cs` step definition method, navigate to all `.feature` file
steps that match it. However, this server is registered only for `.feature` document selectors (VS
routes `.cs` LSP to Roslyn), so this would need to be exposed as a custom `reqnroll/*` command or
require the VS client to intercept `.cs` requests.

**Implementation effort:** Medium. The match cache has the step → definition mapping, but there's
no reverse index (definition → all matching steps). Building that would be a new data structure.

---

### 8. 🔵 `textDocument/didSave` + `willSave` + `willSaveWaitUntil` — LOWER VALUE

**Spec:** LSP 3.0+ — document save event notifications.

**What this would unlock:** The server could react to save events:

- Format on save (trigger the existing formatting handler)
- Re-validate bindings on save
- Clear/reset diagnostic state

**Implementation effort:** Low. `TextDocumentSyncHandler` could be extended to implement
OmniSharp's `IDidSaveTextDocumentHandler`.

**Note:** The pipeline triggers binding re-discovery on `didChange` (incremental) and on
connector/build events, so save-triggered processing is not critical. It's a small gap in the
document sync lifecycle.

---

### 9. 🔵 `workspace/diagnostic` + `textDocument/diagnostic` (Pull Diagnostics, LSP 3.17) — FORWARD-LOOKING

> **Update:** abandoned — see [Status updates](#status-updates-since-the-analysis).

**Spec:** LSP 3.17+ — pull-based diagnostics model.

**What this would unlock:** The server uses push diagnostics (`textDocument/publishDiagnostics`).
LSP 3.17 introduced pull diagnostics as the preferred model. The pull model:

- Avoids racing the client's own diagnostic clears
- Lets the client request diagnostics on demand
- Supports result IDs for incremental updates

**Implementation effort:** Medium. Would require implementing `IDocumentDiagnosticHandler` and
`IWorkspaceDiagnosticHandler` alongside the existing push pipeline; the `DiagnosticsAggregator`
logic could be reused.

---

### 10. 🔵 `textDocument/selectionRange` — LOWER VALUE

**Spec:** LSP 3.15+ — smart selection expansion.

**What this would unlock:** Smart selection in feature files: select a step, then expand to the
entire scenario, then the entire feature. A nice-to-have ergonomic improvement.

**Implementation effort:** Medium. Requires walking the Gherkin AST hierarchy to build selection
ranges.

---

### 11. 🔵 `window/showDocument` — LOWER VALUE

**Spec:** LSP 3.16+ — server asks the client to open a document at a specific location.

**What this would unlock:** An alternative to returning `Location` from definition requests (which
already works fine) for cases where the server wants to proactively open a document — e.g. showing
the first unused step definition.

**Implementation effort:** Low.

---

## Prioritization (as analysed)

### Should implement (high value, low effort)

1. **`textDocument/hover`** — the single biggest UX gap; hover info is table-stakes for a modern
   LSP server.
2. **`textDocument/documentHighlight`** — simple, useful, low implementation cost.
3. **`workspace/symbol`** — Ctrl+T navigation across all feature files.

### Consider implementing (medium value)

4. **`window/workDoneProgress`** — progress feedback during long operations (startup, scanning).
5. **`workspace/executeCommand`** — standard command registration for IDE discoverability.
6. **`textDocument/didSave`** — completes the document sync lifecycle.

### Defer (forward-looking or lower value)

7. Pull diagnostics (`workspace/diagnostic` + `textDocument/diagnostic`) — since abandoned
8. `textDocument/selectionRange`
9. `textDocument/documentLink` — see issue #755
10. `textDocument/implementation`
11. `window/showDocument`
