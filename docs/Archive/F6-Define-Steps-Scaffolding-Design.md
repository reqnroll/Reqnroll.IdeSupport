# F6 — Define Steps (Scaffolding): Design & Build Plan

**Phase 2 · Status: Implemented.** v1 (new-file-only scaffolding) shipped as designed below.
v2 — per-feature candidate-file ranking and append-to-existing-file support, resolving the v1
deferred items OQ-4/OQ-6 — shipped 2026-08-01
([#417](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/417)). See §11 for the as-built
delta from this original design.

---

## 1. Overview

F6 surfaces a code action that generates C# step-definition method stubs for any step in a
`.feature` file that has no matching binding. The generated stubs respect the project's
`reqnroll.json` configuration (skeleton style, namespace style, async variants) and are placed in
a new or existing `.cs` file in the project.

This document:
- Captures the behaviour the existing VisualStudio extension defines (via specs and unit tests)
- Evaluates how `textDocument/codeAction` is supported across VS, VS Code, and Rider for
  non-C# document types
- Proposes a primary surface (code action) and two supplementary surfaces
- Recommends dynamic vs. custom LSP registration
- Lays out a concrete build plan with component breakdown and test strategy

---

## 2. Reference: The VisualStudio Extension's Define Steps Command

The existing `Reqnroll.VisualStudio` project specifies `DefineStepsCommand` through BDD specs
([`DefineStepsCommand.feature`](../tests/VisualStudio/Reqnroll.VisualStudio.Specs/Features/Editor/Commands/DefineStepsCommand.feature))
and unit tests
([`DefineStepsCommandTests.cs`](../tests/VisualStudio/Reqnroll.VisualStudio.Tests/Editor/Commands/DefineStepsCommandTests.cs)).
The command class itself has **not yet been ported** to the new LSP codebase. The tests and specs
define the target behaviour.

### 2.1 Trigger and Entry Point

The VS extension command fires from the Edit menu / right-click context menu on any feature file
editor. It is not position-sensitive — it scans the entire current feature file for undefined
steps.

### 2.2 Dialog Flow

```
User invokes "Define Steps"
    │
    ├─ All steps defined? → ShowProblem("All steps have been defined in this file already.")
    │
    └─ Undefined steps exist
           │
           └─ Open CreateStepDefinitionsDialog
                  │
                  ├─ Lists deduplicated step skeletons (type + expression)
                  ├─ User selects subset (checkboxes)
                  │
                  ├─ "Copy to clipboard" → copies selected skeleton snippets
                  │
                  └─ "Create" → picks target file
                         │
                         ├─ New file named <FeatureName>StepDefinitions.cs
                         ├─ Prefers StepDefinitions/ subfolder if it exists
                         └─ Generates complete class file, opens it in editor
```

### 2.3 Step Skeleton Generation

The core algorithm (from `DefineStepsCommand.GenerateStepDefinitionClass` and `SnippetService`):

1. **Collect undefined steps** from the match cache for the current feature file.
2. **Deduplicate** by skeleton: two steps that produce the same pattern (e.g. two
   `Given the operand {int} has been entered` instances) emit one skeleton.
3. **Infer parameters**: Cucumber Expression parameters (`{int}`, `{string}`, etc.) become
   typed C# parameters. Regex style maps everything to `string`.
4. **Generate method name** in PascalCase from keyword + step text, dropping numeric literals
   that were already captured as parameters.
5. **Apply skeleton style** from `reqnroll.json → trace.stepDefinitionSkeletonStyle`:
   - `CucumberExpression` (default): `[Given("the operand {int} has been entered")]`
   - `RegexAttribute`: `[Given(@"the operand (.*) has been entered")]`
   - `AsyncRegexAttribute`: same as RegexAttribute but `public async Task MethodAsync()`
6. **Escape special characters** per style:
   - Both: `(` → `\(`, `{` → `\{`, `\` → `\\`
   - Regex only: `.` → `\.`, `)` → `\)`, `|` → `\|`
7. **Wrap in a class file** respecting `NamespaceDeclarationStyle` (block-scoped or file-scoped).

**Approved output examples** (from the VS extension test baselines):

*Block-scoped namespace (Reqnroll):*
```csharp
using System;
using Reqnroll;

namespace MyNamespace.MyProject
{
    [Binding]
    public class Feature1StepDefinitions
    {
        [When(@"I press add")]
        public void WhenIPressAdd()
        {
            throw new PendingStepException();
        }
    }
}
```

*File-scoped namespace (Reqnroll):*
```csharp
using System;
using Reqnroll;

namespace MyNamespace.MyProject;

[Binding]
public class Feature1StepDefinitions
{
    [When(@"I press add")]
    public void WhenIPressAdd()
    {
        throw new PendingStepException();
    }
}
```

### 2.4 Target File Naming

- File name: `<FeatureTitle>StepDefinitions.cs` (e.g. feature titled "Addition" → `AdditionStepDefinitions.cs`)
- Placed in the same directory as the feature file, or in a `StepDefinitions/` sibling directory if one exists.
- Namespace is derived from the project's default namespace + any folder path relative to the project root.

---

## 3. Surface Analysis: How to Expose This Feature

The VS extension uses a **menu command with a selection dialog**. The LSP ecosystem offers
different surfaces; each has different affordances and IDE support.

### 3.1 Option A — Code Action (`textDocument/codeAction`) [**Recommended Primary**]

The lightbulb / quick-fix model. When the cursor is on (or a range spans) one or more undefined
steps, the server returns `CodeAction` items. The IDE applies the embedded `WorkspaceEdit` when
the user selects one.

**Advantages:**
- Standard LSP — no IDE-specific glue
- Integrates naturally with each IDE's quick-fix UX (Ctrl+. in VS/VS Code, Alt+Enter in Rider)
- The diagnostic marker on undefined steps already draws user attention to the same location

**Constraints vs. the VS dialog model:**
- No user selection of which stubs to include — the action generates all stubs for the scope
- No "copy to clipboard" variant
- No interactive class/file picker

**Proposed action set:**

| Action title | Scope | WorkspaceEdit effect |
|---|---|---|
| `Define step: <step text>` | Single step at cursor | One stub in target file |
| `Define all missing steps in file` | Entire feature file | All undefined step stubs |
| `Define missing steps in scenario` | Enclosing scenario | Stubs for that scenario only |

This gives the user pick granularity through the standard lightbulb submenu without needing a
modal dialog.

### 3.2 Option B — Custom Request (`reqnroll/defineSteps`) + IDE-Specific UI [**Supplementary for VS**]

Following the pattern of F5 (`reqnroll/goToStepDefinitions`) and F17 (`reqnroll/goToHooks`), the
server registers a custom `reqnroll/defineSteps` request. The VS extension invokes it from a
right-click context menu or Edit menu command.

The server returns a `DefineStepsResponse` carrying the list of skeleton descriptors. The VS
extension shows the `CreateStepDefinitionsDialog`, lets the user select skeletons and a target
action (create file / copy to clipboard), then constructs and applies the `WorkspaceEdit`.

**Advantages:**
- Preserves the VS extension's full dialog UX including multi-select and clipboard option
- VS extension already has test infrastructure for this dialog
- Does not require VS's LSP client to support code action lightbulbs on feature files

**Disadvantages:**
- No benefit for VS Code or Rider (they would only have Option A)
- More code: a custom request, response model, and VS extension UI component

### 3.3 Option C — Command Palette (`workspace/executeCommand`)

The server registers `reqnroll.defineSteps` as an executable command. VS Code's extension can
surface this in the command palette; users invoke it from any open feature file.

This is a lighter-weight alternative to Option B for VS Code. In VS, the VS extension can call
`workspace/executeCommand` to trigger the same server-side logic.

**Recommended use:** Register `reqnroll.defineSteps` as a server command so VS Code's extension
can surface it in the command palette as a fallback for users who prefer keyboard-driven workflows
over the lightbulb.

### 3.4 Recommendation

Implement Option A (code action) as the **primary surface** for all three IDEs. Add Option B
(custom request + VS dialog) as a **Phase 2b enhancement** for Visual Studio once the code action
path is validated. Register Option C (command palette entry) as a **thin wrapper** around the same
server-side logic — it costs little and improves discoverability in VS Code.

---

## 4. IDE Compatibility: `textDocument/codeAction` for `.feature` Files

### 4.1 VS Code

**Support: Full (Grade A)**

VS Code's built-in LSP client sends `textDocument/codeAction` for any document type managed by a
language server. Dynamic capability registration with a `DocumentSelector` (pattern
`**/*.feature`) works correctly. The lightbulb appears automatically when code actions are
available at the cursor position. `workspace/applyEdit` (including file-creation edits) is fully
supported. No concerns.

### 4.2 Rider

**Support: Full (Grade A)**

Rider's built-in LSP client maps LSP code actions to Rider "intentions" (Alt+Enter quick-fix
menu). Dynamic registration via `ICodeActionHandler` with a `DocumentSelector` filter is
respected. `workspace/applyEdit` with file-creation is supported. No concerns.

### 4.3 Visual Studio

**Support: Partial (Grade B — needs verification)**

VS 2022's built-in LSP client (`Microsoft.VisualStudio.LanguageServer.Client`) has evolved
significantly. As of VS 2022 17.6+:

- The LSP client **does** send `textDocument/codeAction` for document types managed by the
  registered LSP server, including `.feature` files.
- VS respects dynamic capability registration for `codeAction`; it sends the request only for
  document types matching the `DocumentSelector`.
- The VS lightbulb (Quick Actions, Ctrl+.) fires for LSP code actions on non-C# files as long
  as the server advertises the capability during initialization or via dynamic registration.
- `workspace/applyEdit` is supported, but **creating a new file** via `workspace/applyEdit` is
  known to have issues in some VS 2022 versions. VS may apply edits only to already-open buffers.

**Known risks for VS:**
1. The lightbulb may not appear on `.feature` files in VS installations older than 17.6 — the
   feature file is a custom content type and VS's quick-action infrastructure may not activate.
2. `workspace/applyEdit` with `DocumentChanges` that include a `CreateFile` operation may be
   ignored or fail silently in VS; VS's `applyEdit` implementation does not always support the
   resource-operation entries in `WorkspaceEdit`.
3. VS's code action ordering/filtering may suppress server-provided actions in some cases.

**Mitigation:** For the VS extension, fall back to Option B (custom `reqnroll/defineSteps`
request + dialog) if code action lightbulb testing reveals gaps. The VS extension can register a
right-click "Define Steps…" menu item that fires the custom request directly, bypassing the
lightbulb mechanism entirely. This is consistent with F5/F17 which already use custom requests in
VS.

### 4.4 Registration Recommendation

**Use dynamic registration via `ICodeActionHandler` with `DocumentSelector = **/*.feature`.**

Rationale:
- The C# language server ambiguity that required manual `options.OnRequest<>()` registration for
  semantic tokens (`textDocument/semanticTokens/full`), code lens (`textDocument/codeLens`), and
  references (`textDocument/references`) **does not apply** to feature files. Those handlers were
  manually registered because the C# language server would also claim `.cs` file requests under
  dynamic registration. No other language server claims `.feature` files.
- `GherkinCompletionHandler` already uses `AddHandler<GherkinCompletionHandler>()` (dynamic
  registration) for `**/*.feature` and works correctly in all three IDEs. Code actions can follow
  the exact same pattern.
- Dynamic registration gives clients the correct capability advertisement in the `initialize`
  handshake response, which enables the client-side lightbulb/quick-action activation.

If VS testing reveals that dynamic registration does not activate the lightbulb, the fallback is
to advertise `codeActionProvider: true` statically in `OnInitialized` and use a manual
`options.OnRequest<>()` registration — same pattern as code lens.

---

## 5. Architecture

### 5.1 Component Map

```
LSP.Core
└── Editor/
    └── Scaffolding/
        ├── IStepScaffoldService          ← interface
        ├── StepScaffoldService           ← implements skeleton generation
        ├── StepSkeletonDescriptor        ← value type: keyword, pattern, parameters, method name
        ├── StepSkeletonRenderer          ← renders one descriptor → C# snippet text
        └── StepDefinitionFileBuilder     ← assembles full class file from snippets

LSP.Server
└── Handlers/ProtocolHandlers/
    └── FeatureCodeActionHandler          ← ICodeActionHandler, DocumentSelector *.feature
```

The VS extension (if Option B is pursued) adds:
```
VisualStudio.Extension
└── DefineSteps/
    ├── DefineStepsCommand                ← VS command, right-click menu entry
    └── CreateStepDefinitionsDialog       ← dialog: multi-select + create/clipboard
```

### 5.2 Data Flow (Code Action Path)

```
User cursor on undefined step in .feature file
    │
    ▼
IDE → textDocument/codeAction (uri, range, context.diagnostics)
    │
    ▼
FeatureCodeActionHandler.Handle()
    ├── IDocumentBufferService.TryGet(uri) → buffer
    ├── IBindingMatchService.TryGet(matchKey) → FeatureBindingMatchSet
    ├── Filter: matchSet.Undefined where step span overlaps request range
    ├── IStepScaffoldService.BuildDescriptors(undefinedSteps, config)
    │       → IReadOnlyList<StepSkeletonDescriptor>
    ├── StepSkeletonRenderer.Render(descriptors, style, indent, newline)
    │       → snippet strings (deduplicated)
    ├── StepDefinitionFileBuilder.Build(snippets, className, namespace, config)
    │       → complete .cs file content string
    ├── Determine target file path (see §5.3)
    └── Return CodeAction[] with embedded WorkspaceEdit
            │
            ├── "Define step: <text>" (one per undefined step in range)
            └── "Define all missing steps in file" (when ≥ 2 undefined)

User selects action
    │
    ▼
IDE applies WorkspaceEdit (creates/modifies .cs file)
    │
    ▼
IDE → textDocument/didChange (.cs file)
    │
    ▼
CsSyncHandler → CSharpBindingDiscoveryService → BindingRegistry updated
```

### 5.3 Target File Determination

The server must choose the target file path without user interaction:

1. Derive a base name from the feature file's title: `<Title>StepDefinitions` (PascalCase, spaces
   stripped).
2. Check for a `StepDefinitions/` sibling directory relative to the feature file's directory.
   - If it exists: `<featureDir>/StepDefinitions/<BaseName>.cs`
   - Otherwise: `<featureDir>/<BaseName>.cs`
3. If the target file already exists, the `WorkspaceEdit` **appends** the new methods to the
   existing class body rather than creating a new file.

Appending to an existing file is more complex because the server must:
- Parse the existing file to find the class body end (closing `}` or last method)
- Insert the new methods before the closing brace

For the initial implementation, **only new-file creation** is supported. Appending to existing
files is deferred as a follow-on (see Open Questions §7).

> **As-built (v2, 2026-08-01):** superseded — see §11. The target is now chosen per-feature (which
> existing file already has the most bindings matched to *this* feature's steps) rather than
> project-wide, and appending to an existing file is implemented.

### 5.4 Namespace Derivation

The namespace for the generated class is derived from:
1. Project default namespace (from `reqnrollProjectLoaded` metadata — already available in
   `LspWorkspaceScope`)
2. Relative folder path from project root to the target file directory (each segment
   PascalCased and appended with `.`)

If project metadata is unavailable, fall back to the feature file's folder name.

---

## 6. Build Plan

### Step 1 — Core: `StepSkeletonDescriptor` and `StepSkeletonRenderer`

**Location:** `LSP.Core/Editor/Scaffolding/`

Define `StepSkeletonDescriptor` as an immutable record carrying:
- `ScenarioBlock` (Given/When/Then)
- `ExpressionText` (the pattern, already escaped per style)
- `ParameterList` (list of `(string type, string name)`)
- `MethodName` (PascalCase)
- `IsAsync` (from config)

Implement `StepSkeletonRenderer.Render(descriptor, style, indent, newline)` which produces:
```csharp
[When(@"I press add")]
public void WhenIPressAdd()
{
    throw new PendingStepException();
}
```

Port the escaping logic from the VisualStudio spec test expectations:

| Character | CucumberExpression | Regex |
|---|---|---|
| `(` | `\(` | `\(` |
| `)` | no change | `\)` |
| `{` | `\{` | `\{` |
| `\` | `\\` | `\\` |
| `.` | no change | `\.` |
| `\|` | no change | `\|` |

Port parameter inference:
- `{int}` → `int p0` (or named: `int count` if word before `{int}` is `count`)
- `{string}` → `string p0`
- `{float}`, `{double}` → `double p0`
- `{word}` → `string p0`
- `{bigdecimal}` → `decimal p0`
- `(.*)` in Regex → `string p0`

**Tests:** Unit tests in `LSP.Core.Tests` verifying rendering for each skeleton style and each
special character escaping scenario (porting the spec scenarios from
`DefineStepsCommand.feature`).

---

### Step 2 — Core: `StepScaffoldService`

**Location:** `LSP.Core/Editor/Scaffolding/StepScaffoldService.cs`

```csharp
public interface IStepScaffoldService
{
    IReadOnlyList<StepSkeletonDescriptor> BuildDescriptors(
        IEnumerable<StepBindingMatch> undefinedSteps,
        DeveroomConfiguration config);
}
```

Responsibilities:
- Read `config.Trace.StepDefinitionSkeletonStyle` to select rendering mode
- Call `StepSkeletonRenderer` per step
- **Deduplicate** by normalised pattern (two steps with same expression → one descriptor)
- Return ordered list (Given before When before Then, then by appearance order)

**Tests:** Unit tests for deduplication, ordering, and config-driven style selection.

---

### Step 3 — Core: `StepDefinitionFileBuilder`

**Location:** `LSP.Core/Editor/Scaffolding/StepDefinitionFileBuilder.cs`

Static method:
```csharp
public static string BuildNewFile(
    IReadOnlyList<string>             snippets,
    string                            className,
    string                            @namespace,
    ReqnrollProjectTraits             projectTraits,
    CSharpCodeGenerationConfiguration csharpConfig,
    string                            indent,
    string                            newLine)
```

This is a direct port of `DefineStepsCommand.GenerateStepDefinitionClass` from the VS extension
tests. The approval test baselines already define the expected output format.

**Tests:** Approval tests matching the four existing baselines (Reqnroll/SpecFlow ×
BlockScoped/FileScoped).

---

### Step 4 — Server: `FeatureCodeActionHandler`

**Location:** `LSP.Server/Handlers/ProtocolHandlers/FeatureCodeActionHandler.cs`

Implements `ICodeActionHandler` (OmniSharp). `GetRegistrationOptions` returns:
```csharp
new CodeActionRegistrationOptions
{
    DocumentSelector = new TextDocumentSelector(
        new TextDocumentFilter { Pattern = "**/*.feature" }),
    CodeActionKinds = new[] { CodeActionKind.QuickFix },
    ResolveProvider = false
}
```

`Handle(CodeActionParams request, CancellationToken ct)`:
1. Filter to `.feature` files (guard; return empty for anything else).
2. Look up `FeatureBindingMatchSet` via `IBindingMatchService`.
3. Find undefined steps overlapping `request.Range`.
4. If none: return empty list.
5. Build descriptors via `IStepScaffoldService`.
6. Render and assemble file content via `StepDefinitionFileBuilder`.
7. Determine target file path (§5.3).
8. Return `CodeAction[]`:
   - One `CodeAction` per individual undefined step in the range (`CodeActionKind.QuickFix`,
     title `"Define step: <step text>"`).
   - One `CodeAction` for all undefined steps in the file when more than one exists
     (`"Define all missing steps in file"`).
   - One `CodeAction` for all undefined steps in the enclosing scenario when the cursor is inside
     a scenario with multiple undefined steps (`"Define missing steps in scenario: <name>"`).

Each `CodeAction` carries an embedded `WorkspaceEdit` with a single
`TextDocumentEdit` (new file: create + insert; existing file: deferred).

**Registration in `Program.cs`:**
```csharp
// DI registration:
.AddSingleton<IStepScaffoldService, StepScaffoldService>()
.AddSingleton<FeatureCodeActionHandler>()

// Handler registration (dynamic, same pattern as GherkinCompletionHandler):
options.AddHandler<FeatureCodeActionHandler>();
```

No `OnRequest<>` manual registration is needed because `.feature` files are unambiguous.

**VS fallback:** If VS testing reveals the lightbulb does not fire, add a static capability
declaration in `OnInitialized`:
```csharp
response.Capabilities.CodeActionProvider = new CodeActionRegistrationOptions.StaticOptions
{
    CodeActionKinds = new[] { CodeActionKind.QuickFix }
};
```
and swap to manual `options.OnRequest<CodeActionParams, CommandOrCodeActionContainer?>()`.

---

### Step 5 — Server DI & Config Wiring

- Add `CSharpCodeGenerationConfiguration` (namespace style) to `DeveroomConfiguration` (may
  already exist via the project system; confirm).
- Expose `ProjectDefaultNamespace` through `ILspWorkspaceScopeManager` or the project-loaded
  notification parameters so `FeatureCodeActionHandler` can derive the namespace.
- Register `IStepScaffoldService` in `Program.ConfigureServer`.

---

### Step 6 (Optional, Phase 2b) — VS Extension: Custom Request + Dialog

If code-action lightbulb testing on VS proves insufficient:

**Server side:** Register a custom `reqnroll/defineSteps` request returning a
`DefineStepsResponse` with a list of `StepSkeletonDescriptor` JSON objects. The server computes
the full file skeleton but does NOT apply a `WorkspaceEdit` — it returns the data and lets the VS
client drive the dialog.

**VS extension side:**
- `DefineStepsCommand` (VS command, context menu) — sends `reqnroll/defineSteps`, receives
  descriptors.
- `CreateStepDefinitionsDialog` — shows the VS dialog with checkboxes, "Create" and "Copy to
  clipboard" buttons.
- On "Create": VS extension constructs the `WorkspaceEdit` and calls
  `workspace/applyEdit` (or directly creates the file via VS DTE).
- On "Copy to clipboard": VS extension copies the concatenated snippet strings.

This preserves the full VS dialog UX and re-uses the existing test infrastructure from the VS
extension specs.

---

### Step 7 — Tests

| Test layer | What to test |
|---|---|
| `LSP.Core.Tests` — `StepSkeletonRendererTests` | Each skeleton style, each special-char escape, async variant |
| `LSP.Core.Tests` — `StepScaffoldServiceTests` | Deduplication, ordering, config-driven style |
| `LSP.Core.Tests` — `StepDefinitionFileBuilderTests` | Approval tests for block/file-scoped × Reqnroll/SpecFlow |
| `LSP.Server.Tests` — `FeatureCodeActionHandlerTests` | Returns empty for non-feature URIs; returns correct action count; action titles; WorkspaceEdit creates correct file path; deduplication |
| `LSP.Protocol.Specs` (integration) | `DefineSteps.feature` — end-to-end: undefined step → code action → workspace/applyEdit → file content verified |

Port scenario phrasing from the existing
[`DefineStepsCommand.feature`](../tests/VisualStudio/Reqnroll.VisualStudio.Specs/Features/Editor/Commands/DefineStepsCommand.feature)
into the integration spec. The VisualStudio-specific scenarios (dialog multi-select, clipboard,
file placement in `StepDefinitions/`) become Phase 2b scenarios gated on Option B.

---

## 7. Open Questions

| # | Question | Impact | Resolution |
|---|---|---|---|
| OQ-1 | Does VS 2022's lightbulb mechanism fire for `.feature` files? Needs a manual smoke test in VS with a pre-release build of the extension. If not, Option B becomes mandatory for VS. | High | **Confirmed live 2026-08-01**: yes, dynamic registration + Ctrl+. both work in VS once the server is fully initialized. An earlier "no lightbulb at all" report on the same day turned out to be the known post-fresh-build LSP-not-fully-wired-up state — restarting the experimental instance resolved it, not a code issue. Option B not needed. |
| OQ-2 | Does VS `workspace/applyEdit` support `CreateFile` resource operations? If not, the server cannot create new `.cs` files via the standard code-action path for VS; the VS extension would need to create the file via DTE. | High (VS only) | **Confirmed working** — verified live via the VS inspector log 2026-08-01 (`CreateFile` document change applied, new file opened via the `vscode.open` command). |
| OQ-3 | Should "Define step" also fire for **ambiguous** steps (multiple matches)? The VS extension only targets undefined steps. Leaving ambiguous steps out of scope for now. | Low | Still out of scope; unchanged. |
| OQ-4 | Appending stubs to an **existing** step definitions file: requires parsing the target `.cs` file to find the insertion point. Defer to a follow-on feature; Phase 1 only creates new files. | Medium | **Resolved, implemented** in v2 ([#417](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/417)) — see §11. |
| OQ-5 | How is `ProjectDefaultNamespace` surfaced to the LSP server? The `reqnroll/projectLoaded` notification carries project metadata — confirm it includes default namespace, or add it. | Medium | Resolved in v1 — `LspReqnrollProject.DefaultNamespace` is populated and read directly in `CodeActionHandler.Handle`. |
| OQ-6 | The VS extension's "Selected step definition skeletons saved to `StepDefinitions/` folder" scenario requires directory-awareness. In the LSP path, the server must inspect the workspace to detect a `StepDefinitions/` folder. Confirm this is feasible with the current `ILspWorkspaceScopeManager` API. | Low | **Resolved, implemented** in v2 — superseded by per-feature candidate ranking (§11), which is a stronger signal than folder detection alone; the folder heuristic is kept only as the last-resort fallback. |

**New, v2-only open item:**

| # | Question | Impact |
|---|---|---|
| OQ-7 | VS's built-in LSP client re-sorts same-priority `CodeAction`s alphabetically by title (see §11.3), so it cannot express our append-candidate ranking across *multiple* append targets — only "append is preferred over new-file" survives, not the relative order among several append candidates. Fixing this would require a custom serializer to emit VS's `_vs_priority` extension field (OmniSharp's `CodeAction` model has no extension-data hook for it). Deliberately not pursued: VS Code and Rider already order correctly, and this is cosmetic (all offered actions still work), not a correctness issue. | Low (VS only, cosmetic) |

---

## 8. File Inventory

### New files

| Path | Purpose |
|---|---|
| `src/LSP/Reqnroll.IdeSupport.LSP.Core/Editor/Scaffolding/StepSkeletonDescriptor.cs` | Value record |
| `src/LSP/Reqnroll.IdeSupport.LSP.Core/Editor/Scaffolding/StepSkeletonRenderer.cs` | Renders one descriptor → snippet |
| `src/LSP/Reqnroll.IdeSupport.LSP.Core/Editor/Scaffolding/IStepScaffoldService.cs` | Interface |
| `src/LSP/Reqnroll.IdeSupport.LSP.Core/Editor/Scaffolding/StepScaffoldService.cs` | Collects + deduplicates descriptors |
| `src/LSP/Reqnroll.IdeSupport.LSP.Core/Editor/Scaffolding/StepDefinitionFileBuilder.cs` | Assembles full `.cs` class file |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Handlers/ProtocolHandlers/FeatureCodeActionHandler.cs` | LSP `ICodeActionHandler` |

### Modified files

| Path | Change |
|---|---|
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Program.cs` | Register `IStepScaffoldService`, `FeatureCodeActionHandler`; `AddHandler<FeatureCodeActionHandler>()` |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/ReqnrollProjectLoadedParams.cs` | Add `DefaultNamespace` field (if missing) |

---

## 9. Relationship to Existing Components

| Component | Role in F6 |
|---|---|
| `IBindingMatchService` | Source of `FeatureBindingMatchSet.Undefined` steps — same cache used by F3 (diagnostics) and F5 (go-to-definition) |
| `DiagnosticsPublishHandler` | Already pushes `Warning` diagnostics for undefined steps; F6 code actions attach to the same diagnostic range via `context.Diagnostics` filtering |
| `IDocumentBufferService` | Provides the feature file buffer for URI → match-set resolution |
| `ILspWorkspaceScopeManager` | Provides project ownership (for namespace derivation) and step-definitions folder detection |
| `GherkinCompletionHandler` | Registration pattern to follow (dynamic, `AddHandler<>`, `**/*.feature` selector) |
| `StepCodeLensHandler` | Registration pattern to avoid (manual `OnRequest<>` — not needed for `.feature` files) |

---

## 10. Success Criteria

- [x] "Define step: X" code action appears in VS Code lightbulb for any undefined step
- [x] "Define all missing steps in file" appears when ≥ 2 undefined steps exist
- [x] Selecting an action creates `<FeatureName>StepDefinitions.cs` with correct content
- [x] Generated class respects `stepDefinitionSkeletonStyle` from `reqnroll.json`
- [x] Generated class respects namespace declaration style (block vs. file-scoped)
- [x] Duplicate step skeletons are collapsed to one stub
- [x] Special characters are correctly escaped in the generated expression
- [x] After file creation, the binding registry refreshes and the step is no longer highlighted as undefined
- [x] No code action is returned for feature files where all steps are defined
- [x] VS Code, Rider: all of the above pass via generic LSP code action path
- [x] Visual Studio: lightbulb verified working live 2026-08-01 (Ctrl+. and passive lightbulb both fire); Option B not needed

**v2 additions (2026-08-01, [#417](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/417)):**

- [x] "Define step(s)" offers an append-to-existing-file action when an existing binding file already covers steps in this feature
- [x] Append action is skipped (falls back to new-file only) when the target file's structure can't be confidently parsed
- [x] New-file fallback's folder derives from the top-ranked candidate's directory, not a project-wide heuristic, when any candidate exists
- [x] Total offered actions capped at 6 (5 append candidates + 1 new-file fallback)
- [x] Exactly one action per title group is marked `isPreferred`
- [x] VS Code renders append-before-new-file in the expected order; Visual Studio does not (§11.3, OQ-7, accepted as a cosmetic VS-only limitation)

---

## 11. As-Built (v2, 2026-08-01) — Candidate-File Ranking and Append Support

This section records what actually shipped for [#417](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/417), which resolves this doc's deferred OQ-4 (append) and OQ-6 (directory-awareness). It stays entirely within Option A (standard `textDocument/codeAction`) — no protocol change, no client-side work in any IDE.

### 11.1 Component and naming corrections vs. the original plan

The handler shipped as `CodeActionHandler` in
`src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeActions/CodeActionHandler.cs` — **not**
`FeatureCodeActionHandler` under `Handlers/ProtocolHandlers/` as §5.1/§6/§8 originally named it.
The scaffolding types (`StepSkeletonDescriptor`, `StepSkeletonRenderer`, `IStepScaffoldService`,
`StepScaffoldService`, `StepDefinitionFileBuilder`) live in
`src/LSP/Reqnroll.IdeSupport.LSP.Core/Scaffolding/` (not `Editor/Scaffolding/`). Two new files were
added for v2 in that same folder:

- `CandidateStepDefinitionFileRanker.cs`
- (no new file for append — `StepDefinitionFileBuilder.AppendToFile` was added to the existing file)

### 11.2 Target selection, as built

1. **Rank candidates** (`CandidateStepDefinitionFileRanker.RankCandidateFiles`): group the
   feature's `FeatureBindingMatchSet.Defined` steps by the `.cs` file each one's matched binding
   lives in (`StepBindingMatch.BindingLocations` → `SourceLocation.SourceFile`), order descending
   by count. This answers "which existing file already has the most bindings matched to *this
   feature's* steps" — a per-feature signal, unlike the v1 `FindBestTargetFolder` heuristic (still
   present, now only a last-resort fallback), which only knew "which folder has the most binding
   files anywhere in the project."
2. **Filter to existing files** and cap at 5 (`MaxAppendCandidates` in `CodeActionHandler`).
3. **Attempt append** for each candidate via `StepDefinitionFileBuilder.AppendToFile` (§11.4). Only
   candidates that succeed are offered as append actions.
4. **New-file fallback**: always offered. Its target folder is
   `Path.GetDirectoryName(topRankedCandidate)` when at least one ranked candidate exists (even if
   that specific file was declined for append), or the v1 `FindBestTargetFolder` heuristic when the
   feature has zero `Defined` steps to rank from at all (e.g. a brand-new feature).
5. **Action fan-out**: one `CodeAction` per successful append candidate, plus the new-file
   fallback — capped at 6 total (`MaxTargetedActions`). Titles stay exactly as v1 produced them
   (e.g. `"Define missing step"`) when only one target resolves; a `" → <target>"` suffix (filename,
   or literally `"new file"`) is added only when more than one target is actually offered, so the
   common case (no existing candidate) is visually unchanged from v1.

### 11.3 `AppendToFile` and the VS ordering quirk (OQ-7)

`StepDefinitionFileBuilder.AppendToFile` uses a lightweight heuristic — not a full C#/Roslyn
parse — accepted as sufficient after evaluating both:

1. Mask out string/char literals and comments (same-length replacement, preserving newlines) so
   brace-counting isn't confused by braces inside them.
2. Find the first `class` keyword in the masked text, then its opening `{`, then the matching
   closing `}` by depth-counting.
3. Detect member indentation from the first non-blank line inside the class body (falls back to
   the caller's default indent for an empty body).
4. Insert the new snippet(s) immediately before the closing brace.

Returns `null` (caller falls back to new-file-only) on: no `class` keyword found, an unterminated
string/comment, or unbalanced braces — i.e. anything the heuristic can't confidently resolve, so a
malformed or unusually-structured hand-written file is never corrupted.

**Every append candidate is guaranteed to already carry `[Binding]`** by construction — it only
ever comes from the ranker, which requires an existing *matched* binding in that file — so no
separate `[Binding]`-presence check was needed before offering it (this was OQ-3-adjacent scope
explicitly confirmed out of need, not an oversight).

**Only the top-ranked action in each title group is marked `isPreferred: true`** (the top append
candidate, or the new-file action when no append candidate survives) — not every action, which was
an initial mistake (all actions defaulted to `isPreferred: true`, a holdover from when there was
only ever one action). Confirmed live 2026-08-01 that this alone does **not** control VS's display
order: `CodeActionResultComparer` in VS's own
`Microsoft.VisualStudio.LanguageServer.Client.Implementation.dll` sorts by the VS-only
`VSInternalCodeAction.Priority` extension field (`_vs_priority` on the wire — a distinct field from
standard `isPreferred`) first, and only falls back to `OrdinalIgnoreCase` title comparison when
that's absent for both sides — which it always is here, since OmniSharp's `CodeAction` model (used
server-side) has no extension-data hook to add it. VS Code and Rider don't do this — they render
the array in server order. Net effect: VS Code shows append-before-new-file correctly; VS shows
whichever title sorts alphabetically first (title-dependent, not actually wrong, just not
guaranteed to match the append-first intent). Filed as OQ-7 above and deliberately not fixed —
fixing it for VS specifically would need a custom JSON serializer just for that one field, for a
purely cosmetic ordering issue that doesn't affect correctness in any client.

### 11.4 Files

| Path | Change |
|---|---|
| `src/LSP/Reqnroll.IdeSupport.LSP.Core/Scaffolding/CandidateStepDefinitionFileRanker.cs` | New |
| `src/LSP/Reqnroll.IdeSupport.LSP.Core/Scaffolding/StepDefinitionFileBuilder.cs` | Added `AppendToFile` + supporting brace-scan helpers |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeActions/CodeActionHandler.cs` | Candidate ranking, action fan-out, per-group `isPreferred` |
| `tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/Scaffolding/CandidateStepDefinitionFileRankerTests.cs` | New |
| `tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/Scaffolding/StepDefinitionFileBuilderAppendTests.cs` | New |
| `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/CodeActions/CodeActionHandlerTests.cs` | Multi-candidate + `isPreferred` scenarios |
| `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs/Features/Editor/DefineSteps.feature` + `StepDefinitions/DefineStepsSteps.cs` | Append-to-existing-file integration scenario |
