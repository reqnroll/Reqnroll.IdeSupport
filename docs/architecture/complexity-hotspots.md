# Complexity Hot Spots — Reqnroll.IdeSupport

**Date:** 2026-09-03
**Scope:** all C# under `src/` — `LSP.Core`, `LSP.Server`, `LSP.Connector`, `Core.Common`, and the
Visual Studio extension/VSSDK integration assemblies.
**Status:** Analysis only. No code changed. Action items filed as issues; see §7.

---

## 1. Method

Complexity was **measured, not estimated**. Three passes over every `.cs` file under `src/`
(excluding `obj/`, `bin/`, `node_modules/`, `.vscode-test/`):

1. **File size** — lines per file.
2. **Per-method cyclomatic complexity and length** — a decision-point proxy (`if`, `while`, `for`,
   `foreach`, `case`, `catch`, `&&`, `||`, `??`) counted per method, with comments and string
   literals stripped first so branching inside doc comments or message strings isn't counted.
3. **Coupling** — constructor parameter count per class, as a proxy for "how many reasons this
   class has to change."

### 1.1 A measurement error worth recording

The first cyclomatic pass bounded each method by brace-depth matching and reported
`StepDefinitionFileBuilder.AppendToFile` at **CX=65 / 307 LOC** — by a wide margin the worst method
in the repo.

That was wrong. `AppendToFile` starts at line 86 and the next method begins at line 139, so it is
~53 lines. The brace matcher had over-run into three sibling methods, because the naive
string-literal stripping used at that point could not handle escaped quotes or verbatim strings —
and the file it was scanning is *itself* a hand-rolled C# lexer full of brace and quote characters.
The measurement tool was defeated by exactly the code it was measuring.

The second pass bounds each method by the start of the next declaration instead, which removes the
brace-matching fragility. All figures below come from that pass.

The conclusion happened to survive — `StepDefinitionFileBuilder` is still the top hot spot, via a
different method in the same file (§3.1) — but the specific method named was wrong, and a reader
acting on the first number would have refactored the wrong thing.

---

## 2. Overall shape: this is not a systemically complex codebase

Worth stating before the findings, so they are not over-corrected:

| Constructor dependencies | Classes |
|---|---|
| 13 | 2 |
| 12 | 1 |
| 10 | 3 |
| 9 | 2 |
| 8 | 5 |
| 7 | 8 |
| 6 | 10 |
| 5 | 16 |
| **< 5** | **143** |

**143 of ~190 classes have fewer than 5 dependencies**, and only 6 methods in the entire repository
exceed CX 20. The codebase has a small number of sharp hot spots, not pervasive complexity. The
recommendations below are targeted, and deliberately short.

---

## 3. Tier 1 — Redesign candidates

These three are structural. Decomposition alone would move the complexity around rather than
remove it.

### 3.1 `StepDefinitionFileBuilder` hand-rolls a C# lexer that Roslyn already provides ([#586](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/586))

**`src/LSP/Reqnroll.IdeSupport.LSP.Core/Scaffolding/StepDefinitionFileBuilder.cs:139`**
`MaskLiteralsAndComments` — **CX=32, the highest genuine complexity in the repository.**

The method is a hand-written lexer that walks the source character by character to mask out `//`
comments, `/* */` blocks, verbatim strings (`@"…"` with `""` escapes), regular strings, and char
literals — so that `FindMatchingCloseBrace` (`:224`) can locate a class's closing brace, and
`DetectMemberIndent` (`:240`) can infer indentation, in order to append generated step-definition
methods to an existing file.

**None of this needs to exist.** `LSP.Core` already takes a direct dependency on Roslyn:

```xml
<!-- src/LSP/Reqnroll.IdeSupport.LSP.Core/Reqnroll.IdeSupport.LSP.Core.csproj:23 -->
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.3.0" />
```

Six other files in the same assembly already use it, including `Parsing/CSharp/StepDefinitionFileParser.cs`
and `Parsing/CSharp/CSharpSyntaxTreeCache.cs` — the latter of which *caches parsed syntax trees*.
Roslyn yields `ClassDeclarationSyntax.CloseBraceToken.SpanStart` directly, correct by construction
for every literal form the hand-rolled masker enumerates by hand, plus raw string literals and
interpolated strings it does not handle at all.

**This is a deletion, not a refactoring** — roughly 150 lines of lexer replaced by a syntax-tree
query, in an assembly that already has the parser loaded and cached.

**It also has a downstream cost.** `AppendToFile` returns `null` when the brace structure is
ambiguous, which silently degrades the "Define missing step" code action: the user gets only the
"create new file" option rather than "append to the existing file." `CodeActionHandler` carries a
whole `successfulAppends` pre-flight loop specifically to tolerate this failure (§3.3). Fixing this
removes both the complexity and the failure mode it forced on its caller.

**Highest value-to-effort ratio in this document.**

### 3.2 `LspInterceptingPipe` is a god class assembled by incident ([#587](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/587))

**`src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/LspInterception/LspInterceptingPipe.cs`**
— **1042 lines, 35 methods, 9 fields.** The largest artifact in the repository.

Its own section headers enumerate the responsibilities:

| Line | Responsibility |
|---|---|
| 69 | Owned request/response correlation |
| 77 | Peer-session response routing (issue #395) |
| 105 | Session termination (issue #555) |
| 323 | Pump loops (bidirectional) |
| 518 | LSP frame reader (wire protocol) |
| 672 | Frame writer |
| 710 | Interceptor pipeline |
| 736 | Notification injection (VS → Server) |
| 803 | Request injection and response correlation (VS → Server → back) |
| 1000 | `IDisposable` |

That is eight or nine distinct concerns in one type. **Three of the sections are named after
incident numbers** (#395, #555) — the complexity accreted through bug fixes, each one bolting
another responsibility onto the same class rather than finding it a home. `ReceivePumpAsync`
(`:336`, LOC=91, CX=14) and `ShutdownServerAsync` in the sibling
`LspServerConnectionService` (`:548`, LOC=80, CX=14) are the visible symptoms.

Natural seams, in rough order of safety:

1. **Frame codec** — the reader (`:518`) and writer (`:672`) are pure wire-protocol
   serialization with no session state. Extractable with essentially no risk and independently
   testable against captured frames.
2. **Correlation/routing** — owned-request correlation (`:69`), peer-session routing (`:77`), and
   request injection (`:803`) are one coherent concern: mapping responses back to whoever asked.
   This is where #395 actually lived.
3. **Pump loops** reduce to a thin orchestrator over the two above.

This is the biggest payoff on the list and also the riskiest change: it sits on the live VS↔server
data path, and its accumulated behaviour encodes at least two production incidents. It needs its
own design pass and should not be attempted opportunistically. It is also **VS-only**, so it does
not block any LSP server work.

### 3.3 `CodeActionHandler.Handle` does six jobs in one method ([#588](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/588))

**`src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeActions/CodeActionHandler.cs:85`**
— **LOC=179, CX=20, 8 constructor dependencies.**

In sequence, the method:

1. guards (feature-file check, match-set lookup, cursor-actually-on-an-undefined-step);
2. resolves snippet style from project configuration;
3. derives target-file metadata — class name, default namespace, project folder, and a
   collision-avoiding `Name2.cs`/`Name3.cs` suffix loop;
4. ranks existing binding files as append candidates;
5. builds the actions;
6. assembles, caps at `MaxTargetedActions`, and emits telemetry.

The obstacle to splitting it is step 5: a **40-line nested local function `BuildTargetedActions`**
that performs file I/O (`ReadAllText`, `AppendToFile`) and closes over eight outer locals —
`snippets`, `appendCandidates`, `className`, `@namespace`, `csharpConfig`, `indent`, `newLine`,
`targetPath`. That capture set is precisely why it has never been extracted: pulling it out
requires a parameter object or a dedicated collaborator to carry the context.

Suggested shape: a `StepDefinitionTargetResolver` owning steps 2–4 (producing one context object),
and a `DefineStepsActionBuilder` owning step 5, leaving `Handle` as guards + orchestration +
telemetry. §3.1 should land first — it removes the `successfulAppends` failure-tolerance loop that
makes step 5 as convoluted as it is.

---

## 4. Tier 2 — Decomposition candidates

Long and/or branchy, but structurally sound. These want splitting, not redesign.

| Method | LOC | CX | Notes |
|---|---|---|---|
| [#589](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/589) `RenameHandler.HandlePrepareRenameAsync` (`Features/Rename/RenameHandler.cs:104`) | 143 | 17 | Two entirely separate flows — `.cs` cursor path and `.feature` cursor path — in one method, with ~12 early returns. Already refactored once (issue #139 / PR #140, 1063 → 477 lines) and still the second-worst method in the server. The two branches share nothing after the common validation prologue, so this is an unusually clean split. |
| [#590](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/590) `RunTestOutcomeBridge.TryGetOutcomeAsync` (VSSDK, `RunTestCodeLens/RunTestOutcomeBridge.cs:74`) | 132 | 30 | Second-highest CX in the repo. **Much of this is essential** — it is reflection-based interop against `internal` VS test-window APIs with no compatibility guarantee, and its documented contract is "never throws," so a defensive check at every reflection step is the point, not an accident. Recommend splitting into acquire-proxy / invoke / map-result stages for testability rather than trying to reduce the branch count. |
| [#591](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/591) `FeatureStepTextBuilder.TryBuildViaRegex` (`Rename/FeatureStepTextBuilder.cs:66`) | 70 | 27 | One of three fallback strategies for reconstructing feature step text after a rename (the caller chains `?? TryBuildViaOutlinePlaceholders ?? newExpression`). Dense capture-group/slot-injection logic. The sibling `DeriveExpressionFromEditedText` (`:264`, LOC=64, CX=12) is the same shape. |
| [#592](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/592) `BindingRegistryChangedHandler.RediscoverCsFilesAsync` (`Pipeline/BindingRegistryChangedHandler.cs:390`) | 103 | 16 | Post-connector-run reconciliation: collects open buffers plus closed files newer than the output assembly, then Roslyn-parses each. Already within the blast radius of #577. |
| [#593](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/593) `SemanticTokensService.Encode` (`Features/SemanticTokens/SemanticTokensService.cs:132`) | 134 | 13 | Long but mechanical — flattens a tag tree into the LSP 5-int delta encoding. Lowest priority in this tier. |

---

## 5. Tier 3 — Coupling

| Class | Ctor deps |
|---|---|
| `RenameHandler` | 13 |
| `BindingRegistryChangedHandler` | 13 |
| `ReqnrollLanguageClient` (VS) | 12 |
| `TextDocumentSyncHandler` | 10 |
| `CompletionHandler` | 10 |
| `LineKeyedCodeLensTagger` (VSSDK) | 10 |

Thirteen dependencies is approximately thirteen reasons to change.

**No separate action is proposed for `BindingRegistryChangedHandler`.** Its dependency count is a
symptom of the notification-contract problem already tracked in #577 — splitting
`BindingRegistryChangedNotification` into three named events should let the handler split with it.
Attacking the coupling directly, ahead of that, would fight the same problem twice.

The other rows are recorded as context for reviewers, not as standalone work items. Coupling here
is a lagging indicator: it drops when §3.3 and §4's decompositions land.

---

## 6. Explicitly not flagged

Recorded because a length-only metric would have fingered them, and because "we looked and decided
no" is more useful to a future reader than silence.

| Item | Measurement | Why it is fine |
|---|---|---|
| `Program.ConfigureServer` (`Hosting/Program.cs:167`) | LOC=187, **CX=2** | A declarative registration manifest, essentially branchless. Splitting would scatter the server wiring across files for no comprehension gain. |
| `LanguageServerOptionsExtensions.InitializeCustomProtocolRouting` (`:79`) | LOC=193, **CX=6** | Same — a routing table written as code. |
| `ReqnrollPackageDetector.GetReqnrollPackage` / `SpecFlowPackageDetector.GetSpecFlowPackage` | LOC=54, CX=16 | Package detection has irreducible case analysis. Both are small, stable, and tested. |
| `StepDefinitionFileParser.AttributeStringInfo` | LOC=94, **CX=1** | A data carrier, not logic. |

The first two are the clearest demonstration that **length without branching is not complexity** —
both are longer than every method in Tier 1 and neither is a problem.

---

## 7. Sequencing and tracked issues

Recommended order:

1. **§3.1 first.** Highest measured complexity, deletes code rather than moving it, removes a
   latent correctness gap (unhandled raw/interpolated string literals), and de-risks §3.3.
2. **§3.3 next**, once appends stop failing.
3. **§4 items** are independent of each other and of the above — good background work.
4. **§3.2 last**, or on its own schedule. Biggest payoff, highest risk, needs a design pass, and is
   VS-only so it blocks nothing.

Tier 1 and Tier 2 action items are tracked as individual issues:

| Issue | Tier | Item |
|---|---|---|
| [#586](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/586) | 1 | Replace the hand-rolled C# lexer with Roslyn — **do first** |
| [#588](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/588) | 1 | Decompose `CodeActionHandler.Handle` — after #586 |
| [#587](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/587) | 1 | Decompose `LspInterceptingPipe` — own schedule, VS-only |
| [#589](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/589) | 2 | Split `HandlePrepareRenameAsync` |
| [#590](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/590) | 2 | Stage `RunTestOutcomeBridge.TryGetOutcomeAsync` |
| [#591](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/591) | 2 | Simplify rename step-text reconstruction |
| [#592](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/592) | 2 | Decompose `RediscoverCsFilesAsync` — coupled to #577 |
| [#593](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/593) | 2 | Split `SemanticTokensService.Encode` — lowest priority |

Tier 3 is intentionally untracked — see §5.
