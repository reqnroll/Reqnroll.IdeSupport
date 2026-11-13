# Rider: a PSI for `.feature` files — implementation and rework plan

Status: **in progress.** Phases 1 (backend module, Gradle wiring, closed one-release range), 2a, 2b and the
Phase 4 policy switch are committed on `feature/453-rider-gherkin-psi`; none has been run in a live Rider, and
the CI matrix, publishing automation and Phases 3 and 5–10 are not started. This supersedes the scope of
[Rider-Feature-Breakpoints-Plan.md](Rider-Feature-Breakpoints-Plan.md). That plan's backend module is
kept unchanged as [Phase 1](#6-phases); its frontend half (§4, the `TEXT` registration) becomes a
temporary shim that [Phase 4](#6-phases) removes.

Facts marked *(verified)* were checked against this repository or the Rider 2026.2.3.1 binaries.
Everything else is a design decision or an assumption listed in [§9](#9-not-verified).

## 1. Why

The Rider plugin registers `.feature` as a file type whose language (`ReqnrollFeatureLanguage`, id
`Gherkin`) has **no `ParserDefinition`** *(verified)*. IntelliJ therefore builds a plain-text PSI whose
language is `TEXT`, not `Gherkin`. Every platform feature that dispatches by PSI language then either
never fires for us or fires for the wrong language. The plugin has worked around that one feature at a
time, with hand-managed `Editor` models and action overrides *(verified: `plugin.xml`, the doc comments
of `ReqnrollFeatureInlayHintsController`, `ReqnrollFeatureFoldingController`,
`RunTestCodeVisionProvider`, `ReqnrollCommentByLineCommentAction`)*.

The goal is to give `.feature` a real, thin PSI and move each workaround onto the platform extension
point it was standing in for. Users gain native behaviour. The plugin loses code it has to keep
working on every Rider release.

### Non-goals

- Replacing the language server. Diagnostics, step binding, completion content, rename edits and test
  discovery stay server-side. The PSI describes **structure**, never meaning.
- A backend (ReSharper) Gherkin PSI. The Phase 1 stub stays a stub.
- Changing VS or VS Code.

## 2. Design

### 2.1 Thin PSI, server stays authoritative

The PSI is a lexer plus a tree parser, **both built on the Cucumber Gherkin parser** rather than
written by hand ([§2.5](#25-reusing-the-gherkin-parser)). It never reports errors (the server owns
diagnostics) and never resolves anything. It exists so the platform can answer "what language, what
token, what block is this line in".

### 2.2 Lexer

A `LexerBase` that replays the real Gherkin token matcher over the buffer ([§2.5](#25-reusing-the-gherkin-parser))
and emits one IntelliJ token per matched span. It produces these token kinds:

| Token | Notes |
|---|---|
| Structural keyword | `Feature`, `Rule`, `Background`, `Scenario`, `Scenario Outline`, `Examples` and their localised forms |
| Step keyword | `Given`, `When`, `Then`, `And`, `But`, `*` and localised forms |
| Text | Names, descriptions, step text |
| Tag | `@name` |
| Comment | `# …` |
| Language header | `# language: xx` |
| Pipe / table cell | Table rows |
| Doc-string delimiter / content | `"""` and ` ``` ` |
| Placeholder | `<name>` inside Outline steps and cells |
| Whitespace / newline | |

- **Localisation.** The matcher resolves keywords from `# language: xx` using the library's own
  dialect data (`GherkinDialects` is public API), so nothing is bundled or duplicated. Editing the
  header therefore needs one full re-highlight.
- **Restartable state.** The simple version rescans the whole buffer on every `start()` and caches the
  token list per document modification stamp. Linear cost; measure on a large file
  ([§8](#8-verification)). Restarting from saved state would be faster but needs the matcher's private
  state, so it is a later optimisation.
- **Project default language.** The server resolves a per-project default language and falls back from
  `en-US` to `en` (`ReqnrollGherkinDialectProvider`). The client needs that value too, as per-file user
  data, with a re-parse when it changes. Until it is wired, files that rely on project config for a
  non-English language mis-parse in the PSI (the server still treats them correctly).
- **Colour parity.** Each token type maps to the same `TextAttributesKey` that
  `ReqnrollSemanticTokensSupport` already assigns to the matching `reqnroll.*` legend name. When the
  server's tokens arrive the file looks identical, so the swap is invisible apart from the instant
  first paint.

### 2.3 Tree

```
FEATURE_FILE
  FEATURE (tags, keyword, name, description)
    BACKGROUND (STEP*)
    RULE
      SCENARIO | SCENARIO_OUTLINE
        STEP (DOC_STRING | TABLE)?
        EXAMPLES (TABLE)
```

The tree comes from Gherkin's own grammar: the library parser's `startRule` / `endRule` / `build(token)`
callbacks map directly onto `PsiBuilder` markers and tokens, so rule boundaries (including Gherkin's
description-versus-step and doc-string corner cases) are exactly the grammar's. A malformed file still
produces a full tree, because the adapter closes every open marker on an error or end of file; the tree
must never throw away text.

- Typed elements only where a feature needs them: `ReqnrollStep` (keyword, text, owning scenario),
  `ReqnrollScenario` (name, kind, line range), `ReqnrollTable`. Everything else is a generic
  `ASTWrapperPsiElement`.
- **No `PsiNamedElement` and no `PsiReference` in the first iterations.** Naming an element or giving
  it a reference makes platform Rename, Find Usages and "highlight usages" engage on it, and none of
  those can be answered without a PSI target in a C# file, which the Rider frontend does not have
  *(see [§9](#9-not-verified))*.

### 2.4 Registration

```xml
<lang.parserDefinition language="Gherkin" implementationClass="…ReqnrollFeatureParserDefinition"/>
<lang.syntaxHighlighterFactory language="Gherkin" implementationClass="…"/>
```

`ReqnrollFeatureFileType` already declares `language="Gherkin"`, so no file-type change is needed.

### 2.5 Reusing the Gherkin parser

Checked against the published jars (Maven Central `io.cucumber:gherkin`, NuGet `Gherkin`) and the
server's `IdeSupportGherkinParser`. The bridge itself has not been built.

| Option | Verdict |
|---|---|
| **Java `GherkinParser` (public API) as the tree source** | Rejected. Given an invalid file (unterminated doc string, unknown `# language:`) it returns a `ParseError` and **no partial document** *(verified by running it)*. A PSI must cover the whole text. Fine only as a structure-only side channel, which cannot anchor markers or highlighting. |
| **.NET `Gherkin` package, via the server** | Rejected. The server already does this and tolerates errors, but the PSI is needed synchronously on the frontend. |
| **Java internal parser, driving `PsiBuilder`** | **Chosen.** `Parser`, `Token`, `TokenScanner` and `GherkinTokenMatcher` are package-private *(verified)*, but `Parser.Builder` is exactly the callback shape `PsiBuilder` needs. |
| **Hand-written lexer and parser** | Rejected. It would re-implement the grammar's corner cases and keyword tables and need to track upstream. |

**Version.** Use Java `io.cucumber:gherkin` **39.1.0**, the version matching the server's `Gherkin`
39.1.0 *(verified: both are published through 42.0.1 in lock-step)*. Dependencies are `messages` and
`jspecify`; the jar is about 240 KB and `messages` about 120 KB, with Jackson only as a test dependency
of `messages` *(verified from the POMs)*. Gherkin is MIT-licensed upstream.

**Reaching the internal classes.** Put one small adapter class in package `io.cucumber.gherkin` inside
our plugin jar, written in Java (the Gradle build gains a Java source set; today it is Kotlin only), so
it can use the package-private types on the classpath. The fallback is to vendor the library source.
Both need checking in Rider ([§9](#9-not-verified)).

**Adapter responsibilities.**

1. A `Parser.Builder` that maps `startRule` / `endRule` to `PsiBuilder` markers and `build(token)` to
   tokens, using each token's line and column for offsets.
2. Catch the exception the library throws at an error or at end of file, and close all open markers so
   the tree is complete.
3. A restartable `Lexer` that replays `TokenScanner` and `GherkinTokenMatcher` and emits sub-line spans
   (keyword, text, tags, table cells, doc-string content) from each `Token`.
4. The per-file default language ([§2.2](#22-lexer)).

**Parity with the server.** Both sides come from the same Cucumber release train, so grammar and
keywords should agree. Contract tests: run the Cucumber shared test corpus (the `good/*.feature` files
with their expected token output) through the adapter, and compare structure for a fixed sample against
the server's `documentSymbol`.

## 3. Area-by-area rework

Ranked by user impact, as agreed. Each row names the code that goes away.

| # | Area | Today | After the PSI | Removed / changed | Risk |
|---|---|---|---|---|---|
| 1 | **Instant syntax colour** | Semantic tokens only; nothing coloured until the server answers | `SyntaxHighlighter` from the lexer; semantic tokens overlay it | Nothing removed; adds `BraceMatcher`, `QuoteHandler` | Low |
| 2 | **Run / Debug gutter** | `RunTestCodeVisionProvider` inline lens; no Debug | `RunLineMarkerContributor` gives a native gutter marker with the standard Run/Debug menu on `SCENARIO` / `SCENARIO_OUTLINE` | `RunTestCodeVisionProvider` (after a deprecation release behind a setting). `FailedStepGutterMarks` stays: it paints per-run state, not structure | Medium |
| 3 | **Breakpoints** | Spike registers the debugger policy for `TEXT`, so every plain-text file passes Gate 1 | Register the policy for `Gherkin` only | The `TEXT` line in `plugin.xml`. The backend stub stays | Low |
| 4 | **Navigation and refactoring entry points** | Promoter and action for F12 / Ctrl+B / Ctrl+click; claimed Shift+F6; Comment action overridden by ID | `Commenter`; `renameHandler` extension point for Rename Step; navigation re-evaluated in [Phase 8](#6-phases) | `ReqnrollCommentByLineCommentAction` (if parity holds). Shift+F6 claim | **High** for navigation, Low for the rest |
| 5 | **Hand-managed editor visuals** | `editorFactoryListener`s drive `foldingModel` and `inlayModel`; tag links by hand | **Prefer what Rider's generic LSP client now does itself** (see the principle below): native folding from 2025.2.2, native inlay hints from 2025.2.2, native outline from 2025.3, driven by the server's responses. Our controllers remain only for older builds and are switched off where the platform does the work | The folding controller, once `pluginSinceBuild` reaches 2025.2.2; the inlay-hint controller likewise, after checking for double rendering | Medium |
| 6 | **Typing assists** | Two `typedHandler`s for `|` realignment and `@` / space popups | Same handlers, now language-scoped. Add `SpellcheckingStrategy`, `IndexPatternBuilder` (TODO), breadcrumbs | Nothing | Low |

### Notes per area

1. **Highlighting.** Platform brace matching and quote handling arrive with the language. Check that
   the semantic-token overlay does not flicker against the lexer colours when a file is first opened.
2. **Run gutter.** The marker's actions call the existing `RunTestRunner`, so execution is unchanged.
   The **Debug** entry is [Phase 10](#6-phases): rather than starting `dotnet test` with the VSTest
   host-debug handshake and attaching the debugger by hand, the backend module hands the scenario's
   generated test to Rider's own unit-test runner, which gives Debug and the Unit Tests window together.
   Keep the inline lens for one release so users who like it can switch back.
3. **Breakpoints.** Gate 1 keys on `psiFile.language`, which becomes `Gherkin`. Do this as soon as
   Phase 2 lands rather than leaving the `TEXT` shim in place.
4. **Navigation.** Go to Definition works today through the generic LSP client plus the 2026.2
   promoter. A `PsiReference` on step text would need a PSI target in a `.cs` file, and Rider's frontend
   has no C# PSI to offer. So this area is an investigation with a go/no-go, not a committed rewrite. The
   committed parts are the `Commenter` and a native `renameHandler`. Rename Step stays a server rename:
   the handler only changes how it is invoked (Refactor menu, user keymaps), not how the edit is built.
5. **Visuals.** *Principle (decided after building a tree-based folding and Structure view and then
   reversing it):* anything the server already answers through a standard LSP request is driven by that
   response in every IDE, so there is one set of rules. The PSI is for what an LSP response cannot give:
   instant colour before the server starts, the breakpoint gate, run markers, the rename handler. A
   tree-based copy of the fold and outline rules drifted from the server's within one commit (a Feature's
   fold started on a different line), so it was removed.
   - **Folding:** the server's `foldingRange`. Rider 2025.2.2+ folds from it natively (verified in the 2026.2
     binaries: `LspFoldingBuilder` registered for any language, on by default); `NativeLspFolding` switches the
     old controller off there. Below that the controller renders the same ranges.
   - **Outline:** the server's `documentSymbol`, native from Rider 2025.3. Rider before that has no outline.
   - **Inlay hints, code lens, document links:** Rider's client also gained native support for these (inlay
     hints 2025.2.2, document links 2025.1, code lens 2026.1, per JetBrains' LSP page). Phase 6 starts by
     checking, on a new Rider, whether the platform already draws what our controllers draw, since both
     being active would double-render.

## 4. Cross-cutting concerns

- **Double rendering during migration.** Today's workaround controllers and the new language-scoped
  platform features will both fire once the language is real. Every area migrates in a single PR that
  adds the platform feature **and** removes its workaround. No area lands half-way.
- **Spell checking, TODO indexing and other language-dispatched features start running on `.feature`.**
  Phase 2 ships a `SpellcheckingStrategy` that skips keywords, tags, placeholders and step parameters,
  so users do not get a wall of squiggles.
- **Legacy plugin (`net.reqnroll`).** It registers its own `.feature` file type and PSI. With both
  plugins present, two parsers fight over one extension. **Done:** `plugin.xml` declares
  `<incompatible-with>net.reqnroll</incompatible-with>` and its description no longer tells users they can run
  both. Still to verify: that Rider honours it for a plugin ID.
- **What the PSI does and does not tie to Rider releases.** The PSI is Kotlin, compiled against 2024.3.5, and
  stays one build for 243 – 262.\* (`verifyPlugin` checks it across the range). Only the backend module is tied
  to one release, and only builds made with `-PwithBackend=true` carry it; see the closed-range rules in
  [the breakpoints plan §6.2](Rider-Feature-Breakpoints-Plan.md#62-gradle).
- **Formatting.** `ReqnrollFormattingService` is registered `order="first"` and keeps winning. No
  `FormattingModelBuilder` is added, so Reformat Code still goes through the server.
- **Compatibility range.** The Kotlin jar is shared by all legs (243 – 262.*). Every extension point
  used must exist in 2024.3. `verifyPlugin` checks binary compatibility of that Kotlin code across the
  range, so the PSI work is partly self-checking, unlike the .NET backend.
- **Marketplace.** No new legs. The frontend-only leg for older Rider gets the PSI features too
  (highlighting, gutter run, folding, outline), since none of them need the backend. Only breakpoints
  need the backend leg.
- **Parity with the server.** See [§2.5](#25-reusing-the-gherkin-parser). Pin the Java jar to the
  server's Gherkin version and bump them together.
- **Packaging and classloading.** The Gherkin and `messages` jars ship inside the plugin. The
  split-package adapter and any `io.cucumber` classes already in the IDE need checking
  ([§9](#9-not-verified)).
- **Testing in Rider.** Rider's `BasePlatformTestCase` fixtures fail with "solution can't be null"
  (recorded from issue 566). `ParsingTestCase`, which uses a mock project, **does work** in this build
  *(verified: `ReqnrollFeatureParserTest` passes under `./gradlew test`)*, as do plain unit tests. The
  earlier plan's `BasePlatformTestCase` for the breakpoint policy registration should instead be a test
  that parses `plugin.xml`.

## 5. Dependency order

```mermaid
flowchart LR
    P1["1 Backend breakpoints<br/>(TEXT shim)"] --> P4
    P2["2 PSI foundation"] --> P3["3 Highlight + matchers"]
    P2 --> P4["4 Breakpoints on Gherkin"]
    P2 --> P5["5 Folding + outline"]
    P2 --> P6["6 Inlay hints"]
    P2 --> P7["7 Run gutter"]
    P2 --> P8["8 Rename + navigation"]
    P3 --> P9["9 Cleanup + docs"]
    P4 --> P9
    P5 --> P9
    P6 --> P9
    P7 --> P9
    P8 --> P9
    P1 --> P10["10 Run/Debug through<br/>Rider Unit Tests (backend)"]
    P7 -.-> P10
```

## 6. Phases

Sizes are rough estimates in working days and have not been validated.

| Phase | Deliverable | Exit criterion | Est. |
|---|---|---|---|
| **0** | Live check of the backend spike in `runIde` | Go/no-go for Phase 1 (checks in [§8](#8-verification)) | 1 |
| **1** | Backend module and Gradle wiring (**built and committed**; the `TEXT` shim is gone). Build now derives a closed one-release range from `riderSdkVersion` and defaults to frontend-only. Still to do: CI matrix, per-release backend smoke test, publishing automation, changelog-rendered change notes (breakpoints plan §6–7) | Breakpoint binds and hits in a live Rider | per earlier plan |
| **2a** | **Parser bridge**: Java source set, Gherkin 39.1.0 dependency, same-package adapter, `PsiBuilder` builder, restartable lexer; no registration yet | Shared corpus passes; invalid files yield a full tree; spike confirms classloading in Rider | 3–4 |
|  | *Status: built and unit-tested (corpus, lexer, `PsiBuilder` replay via `ParsingTestCase`); `ReqnrollFeatureParserDefinition` exists but is deliberately not registered in `plugin.xml`. Classloading in a real Rider is the open item.* | | |
| **2b** | **PSI foundation**: `ParserDefinition`, typed elements, per-file default language, `SpellcheckingStrategy`, no behavioural change otherwise | Every existing feature still works with the language now real; no double rendering | 2–3 |
|  | *Status: registered in `plugin.xml` (parser definition, spellchecking strategy), and the debugger policy moved from `TEXT` to `Gherkin` in the same change, since the PSI language changed (this also completes Phase 4). The default language is read from the project's `reqnroll.json` / `specflow.json` by `FeatureDefaultLanguages` (`App.config` not read). Typed elements are deferred to the first phase that needs them. Re-parsing open files when the configuration changes was done in Phase 3 (`FeatureLanguageConfigListener`). Still open: a live check that nothing double-renders.* | | |
| **3** | **Highlighting and matchers**: `SyntaxHighlighter`, `BraceMatcher`, `QuoteHandler`, `Commenter`; retire the comment action override if parity holds | First paint coloured before the server starts; comment toggling matches the server's behaviour | 2–3 |
|  | *Status: built and unit-tested. Done: the lexer-based `SyntaxHighlighter` (sharing `ReqnrollFeatureTextAttributes` with the semantic tokens, with header and body table cells distinguished), the `Commenter`, and re-parsing open files when `reqnroll.json` / `specflow.json` changes (`FeatureLanguageConfigListener`). **Not done, deliberately:** `BraceMatcher` and `QuoteHandler` (the grammar has no bracket or quote tokens: a placeholder is one token and quotes are plain step text), and retiring the comment action override (it stays until a live check shows the platform action matches the server's toggle for doc strings and tables). Unverified: that `FileContentUtil.reparseFiles` also refreshes an open editor's highlighter, and that nothing flickers when semantic tokens arrive.* | | |
| **4** | **Breakpoints on `Gherkin`**: switch the policy, drop `TEXT` | `.txt` gutter clicks no longer reach Gate 1 | 0.5 |
| **5** | **Folding and Structure view**: ~~`FoldingBuilder`, `StructureViewBuilder` from the tree~~ — reversed, see §3 note 5; server ranges and symbols in every IDE | Rider 2025.2.2+ folds natively with the controller off; older Rider folds through the controller; both use the server's ranges | 1 |
|  | *Status: a tree-based `FoldingBuilder` and Structure view were built and committed, then reverted in favour of server ranges everywhere. Done: `NativeLspFolding` (checks for the platform's `LspFoldingRangeSupport` class) switches the folding controller off on builds with native folding; docs corrected (the old claim that Rider's client has no folding consumer was true only for 2024.3). Left as it was: no outline before Rider 2025.3. **Verified live (2026.2.3.1):** `.feature` files fold natively with `Gherkin` as the PSI language; the server log shows the platform's `textDocument/foldingRange` requests and the plugin log has no `ReqnrollFeatureFoldingController` entries, so the controller is off. Not separately checked: whether the platform's folding refreshes while typing.* | | |
| **6** | **Native-rendering audit** (replaces the declarative inlay provider, per the §3 note 5 principle): confirm on a new Rider that inlay hints, code lens and document links each render once | One hint, lens and link per source, no duplicates | 1 |
|  | *Status: static audit done against the Rider 2026.2.3.1 binaries. Inlay hints: `NativeLspInlayHints` already switches the controller off where the platform's `LspInlayHintsProviderFactory` is registered. Code lens: `NativeLspCodeLens` does the same for `LspCodeVisionProvider`. **Document links: not gated.** 2026.2 has `LspDocumentLinkCache` (sends `textDocument/documentLink`) and `LspDocumentLinkReferenceProvider` (an implicit PSI reference, so Ctrl+click navigation), and `LspHighlightingPassFactory` is registered; whether it draws the underline for `.feature` files, and whether Ctrl+click then opens the browser twice next to `ReqnrollFeatureTagLinkController`, needs a live check. Gating the controller without that check could drop the permanent underline and hover hint. **Live check (2026.2.3.1): Ctrl+click opened the link twice**, once through the platform's reference (which works now that `.feature` has a PSI) and once through the controller. The platform's switch (`LspDocumentLinkDisabled`) is a descriptor customization the plugin cannot set while it compiles against 2024.3, so the controller now leaves the click to the platform wherever `LspDocumentLinkSupport` exists (`NativeLspDocumentLinks`). It keeps drawing the underline and the hover hint and still reports the click telemetry. **Second live check:** one tab, but the first few clicks after opening the solution failed with "cannot find declaration": on 2026.2 Ctrl+click runs `ReqnrollGoToDefinitionAction` (Go to Declaration), and the platform's reference exists only once its `LspDocumentLinkCache` has fetched the links, which it does lazily; the controller had them about 20 s before the first click. So the action now opens a tag link under the mouse or caret itself, through the controller's links (`openTagLinkAt`), and skips Go to Declaration; the controller's own click handling stands down wherever the action takes the shortcuts. This also restores the `TagLinkSupport.isOpenableUrl` scheme block list and makes F12 on a tag open it. Where the platform follows document links but the action is not enabled, the click is still left to the platform. **Verified live (2026.2.3.1): the first Ctrl+click after opening the solution opens one tab, with no error.*** | | |
| **7** | **Run gutter**: `RunLineMarkerContributor`; keep the lens behind a setting for one release. If Phase 10 option A lands, this is for the frontend-only build only | Run works from the gutter; failed-step marks unaffected | 3 |
| **8** | **Rename and navigation**: `renameHandler`; navigation investigation with go/no-go | Rename Step appears under Refactor and honours keymaps; navigation decision recorded | 2 + spike |
| **9** | **Cleanup and docs**: remove dead workarounds, update `client-rider.md` §7 and §8, feature pages, CHANGELOG | Doc comments no longer say "no PSI" | 1–2 |
| **10** | **Run and Debug through Rider's Unit Tests** (backend build; replaces the earlier "Debug launch" track). See [Phase 10 design](#phase-10-run-and-debug-through-riders-unit-tests). Decided: option A, a daemon stage that adds Rider's own test highlighting (by reflection) on scenarios | Each scenario has Rider's native Run/Debug gutter mark with outcome icons. Runs show in the Unit Tests window. Debug stops on `.feature` breakpoints. The frontend-only build keeps `dotnet test` | 5–8 + spike |

Phases 3 and 5–8 are independent of each other once Phase 2 is in, so they can be reviewed as
separate PRs in any order. Phase 2 is the only large risk: it changes the language of every `.feature`
editor.


### Phase 10: Run and Debug through Rider's Unit Tests

**Problem (reported 2026-11-12).** The Run lens calls `dotnet test` itself (`RunTestRunner`). A run never appears
in Rider's Unit Tests window, and there is no way to start a Debug run from the lens.

**Why it was out of reach, and why it no longer is.** Rider's .NET unit testing lives in the ReSharper backend,
and the JVM frontend can only signal sessions the backend creates (pitfall 7 of
`docs/Test-Runner-Integration-Design.md`). The plan was blocked on needing a C# backend plugin. This branch now
has one (`src/Rider/dotnet`, built with `-PwithBackend=true`).

**No test elements need to be created.** Rider already discovers the MSTest, NUnit and xUnit methods Reqnroll
generates in `.feature.cs`, so they are in its element repository. The backend only has to find the existing
elements for a scenario and launch them.

**Preferred: Rider's own test gutter marks in the `.feature` file (option A).** Rider's C# Run/Debug gutter icons
are not drawn by the frontend. They are backend daemon highlightings, and a plugin can add them to another file
type. Checked in the 2026.2.3.1 `JetBrains.ReSharper.UnitTestFramework.dll`:
- `UnitTestDaemonStage` runs `UnitTestFileExplorer.Explore` on every visible document whose source file
  `ProvidesCodeModel`. It is not limited to C#; the JavaScript and C++ test providers use it too.
- `Explore` calls every registered `IUnitTestExplorerFromFile` (`Provider`, plus
  `ProcessFile(IFile, IUnitTestElementObserverOnFile, interrupted)`) on the file's dominant PSI.
- Each `observer.OnUnitTestElementDisposition(element, navigationRange, containingRange)` becomes a
  `UnitTestElementHighlighting` at that range. That is the standard test gutter mark: the Run/Debug/Profile/Cover
  menu, the passed, failed and ignored icon from `IUnitTestResultManager`, and the Unit Tests window.

So the backend module would register an `IUnitTestExplorerFromFile` for the `.feature` PSI. For each Scenario,
Scenario Outline and Examples row, it finds the unit-test element Rider already discovered in the generated
`.feature.cs`, and reports it at the scenario's range in the `.feature` file. Rider then does the rest:
- Run and Debug from the gutter.
- Outcome icons.
- Run All for a Feature or Rule (as a container mark on that line).
- Re-running from the Unit Tests window, and navigation from that window to the scenario.

None of our runner, TRX parsing or lens glyph code is needed on a backend build.

Mapping a scenario to its element:
- The backend can read the generated file's `#line N` directive at the start of each test method, which is the
  scenario's line in the `.feature` file. It can then look the method's element up in `IUnitTestElementRepository`
  (by source file and range) or with `observer.GetElementById`.
- This needs no server round trip and no name matching.
- Outline rows are child elements (MSTest `DataRow`, NUnit `TestCase`, xUnit `InlineData`) and need their own
  mapping to the Examples lines.

Open risks for option A:
- **Provider ownership.** Each `IUnitTestExplorerFromFile` names one `Provider`, and `Explore` opens a discovery
  transaction per provider with the `.feature` file as the source. Reporting MSTest, NUnit or xUnit elements from a
  second source file might move them, or make them flip between the `.cs` and `.feature` files. This is the first
  thing the spike must check.
- **Code model.** The backend `.feature` PSI is a stub tree (one file node). The daemon stage requires
  `ProvidesCodeModel`, and the explorer needs scenario offsets, which can come from the document text.
- **Duplicate marks.** On a backend build, Phase 7's frontend `RunLineMarkerContributor` and the inline Run lens
  must stand down, or each scenario gets two Run icons.

**Fallback: the lens drives Rider's runner (option B).** This keeps our own lens and gutter, and only replaces the
launch:
1. **Frontend.** The Run / Debug actions resolve test targets as today (`reqnroll/resolveTestTargets`: project
   plus fully qualified test names, rows for outlines). They send them to the backend instead of starting
   `dotnet test`.
2. **Channel.** A small RD protocol model call, for example `runScenarioTests(projectPath, testNames, debug)`. It
   would be the module's first protocol model, which brings rd-gen into the build.
3. **Backend.**
   - Look the elements up in `IUnitTestElementRepository`.
   - Run them with `IUnitTestingFacade.Run(elements).Using(hostProvider).In.CurrentOrNewSession()`, passing
     `RunHostProvider` or `DebugHostProvider` (from `UnitTestingFacadeFluentExtensions`).
4. **Outcomes.** The lens glyph and the failed-step gutter mark would need Rider's results mapped back, or they
   could be left to the Unit Tests window (§10).

**In both options**, the frontend-only build, and any scenario the backend cannot match, keep the existing
`dotnet test` lens.

**Spike first, to settle:**
- Option A's provider ownership: whether reporting an MSTest, NUnit or xUnit element from a `.feature`
  file disturbs its discovery from `.feature.cs`. If it does, use option B.
- That the `.feature` source file passes the daemon stage's `ProvidesCodeModel` gate.
- That `#line` directives map scenarios, and outline rows, to elements for MSTest, NUnit and xUnit. For outline
  rows, Rider names a row by a display string, not an index (see that design's pitfall about rows).
- That `DebugHostProvider` stops on `.feature` breakpoints set through the backend's breakpoint variants.
- The cost of adding an RD model to the module's build and CI.

**Spike result (2026-11-12, Rider 2026.2.3.1, MSTest sample; code on the local branch
`spike/rider-feature-test-gutter`).** Option A works, with two changes to the shape above:

1. **The backend never analysed `.feature` files at first.** With a frontend PSI language, Rider kept the
   editor frontend-only, so `UnitTestDaemonStage` never ran on the file. Two `plugin.xml` lines fix it, the same
   ones Razor uses:
   - `<rd.languageAssociation backendLanguage="REQNROLL_FEATURE" frontendLanguage="Gherkin"/>`
   - `<backend.markup.adapterFactory language="Gherkin" implementationClass="com.jetbrains.rdclient.daemon.FrontendMarkupAdapterFactory"/>`

   After that, the stage ran with `ProvidesCodeModel=True` on the stub backend PSI. The live check showed no
   visible change to highlighting or typing.
2. **Reporting the real elements breaks them (the ownership risk was real).** The first Debug from the
   `.feature` gutter worked. After that, the test disappeared from the Unit Tests window and the mark stopped
   launching. Rider's `FakeUnitTestElement` avoids this: it is a gutter-only stand-in that "can appear in UI, like
   in gutter menus", and its menu (`UnitTestBulbItemsCollector`) lists `GetRelatedUnitTestElements()`, each with
   Run/Debug. Reporting one stand-in per scenario that points at the real element kept the tests correctly placed
   in the Unit Tests window, and Run/Debug from the gutter worked.

What the spike showed live:
- Marks appear on the Feature line and on every Scenario line.
- The `#line` mapping (the first `#line N` inside each element's range in `.feature.cs`) finds every scenario.
- Run and Debug from the marks start Rider's session.
- No elements were lost from the Unit Tests window with stand-ins.

Not working, or not checked, with stand-ins:
- **No outcome icon.** `UnitTestElementHighlighting.AttributeId` always gives a stand-in the neutral container
  style, so the mark never turns green or red.
- **Marks did not respond at first** after the solution opened, and worked later. The cause is not known; most
  likely the clicks came before discovery of `.feature.cs` had finished.
- The menu has one more level than in C# (the related test, then Run/Debug).

**Second iteration (2026-11-13): status-aware, and the recommended shape.** No discovery participation at all:
a `[DaemonStage]` of our own runs on `.feature` files and adds Rider's own `UnitTestElementHighlighting` for the
**real** element, with a `UnitTestElementDisposition` pointing at the scenario line in the `.feature` file.
- The highlighting class is internal, so it is created by reflection. Its constructor
  `(IUnitTestLikeElementDisposition, DocumentRange)` is public. The backend module is built for one Rider release,
  so a per-release smoke test catches a change to it.
- Because it is Rider's own highlighting:
  - `AttributeId` reads the real element's result from `IUnitTestResultManager`, so the mark shows pass/fail.
  - The gutter menu (`UnitTestElementGutterMarkBase` with `UnitTestBulbItemsCollector`) is the native C# one.
  - `UnitTestingBetterGutterMarkIconsUpdater` tracks every highlighter implementing `IUnitTestElementHighlighting`
    with a real disposition in any document, so the icon refreshes after a run without code of ours.
- The stage isn't tied to one provider, so the same code should serve MSTest, NUnit and xUnit (only MSTest was
  run).
- **Verified live (Rider 2026.2.3.1, MSTest):**
  - Marks appear on the Feature and Scenario lines.
  - Debug and Run from them start Rider's sessions.
  - The marks turn green/red with the result.
  - The tests stay in the Unit Tests window across runs (no explorer or session-tree warnings in the backend log).
- **Still not covered:**
  - Outline rows (child elements are skipped) and Rule containers.
  - NUnit and xUnit.
  - Whether Debug stops on a `.feature` breakpoint.
  - The inline Run lens standing down on backend builds (both show).
  - A dialect-aware Feature line.
  - Element lookup under the unit-testing read lock.
  - Whether the early unresponsiveness recurs.

## 7. Rollback

- Phases 2–8 each ship as one PR with a revert path. Phase 2's `ParserDefinition` registration is a
  single `plugin.xml` line, so a field problem with the PSI can be mitigated by removing it while the
  platform features keep their old controllers, but only if the controllers of later phases are still
  present. That is why each migration removes its workaround in the same PR as it adds the replacement,
  and why Phase 2 must not remove anything.
- The Run lens stays behind a setting for one release (Phase 7).

## 8. Verification

Live, in Rider, on a sample Reqnroll project (Windows first).

| Check | Expected |
|---|---|
| Open a `.feature` with the server stopped | Coloured at once; the colours do not change when the server starts |
| Localised file (`# language: fr`) | Keywords coloured and folded correctly; editing the header re-highlights |
| Project whose default language is not English, no header | PSI and server agree on keywords |
| Invalid file (unterminated doc string, unknown language) | Whole text still in the tree; no exception in the log |
| Large file (several thousand lines) | No typing lag; no EDT stalls in the log; measure the lexer rescan cost |
| Fold / unfold, Structure view | Same folds as VS Code on the same file (Rider 2025.2.2+ natively, older through the controller, never both); outline from Rider 2025.3 |
| Inlay hints, code lens, document links | Each rendered once on Rider 2025.2.2+, 2026.1+ and 2026.2; tag link Ctrl+click opens once |
| Gutter marker | Run works; failed-step marks appear after a failed run |
| Breakpoints | As in the earlier plan's table; no response on a `.txt` gutter |
| Comment, Rename Step, F12 | Unchanged behaviour on all supported Rider versions |
| Spell check | No squiggles on keywords, tags, placeholders or parameters |
| Legacy plugin also installed | Rider reports the incompatibility |

## 9. Not verified

- Everything in the backend breakpoint spike (see the earlier plan §11).
- That a frontend PSI is created for `.feature` in Rider, and how it interacts with the platform LSP
  client's own `.feature` handling. The legacy plugin does it, which is the only evidence.
- That Rider's frontend has no usable PSI for C# files, which decides whether Phase 8's navigation
  go/no-go can be a go.
- Whether the semantic-token overlay and the lexer highlighter composite without flicker on every
  supported Rider.
- Whether `RunLineMarkerContributor` markers show the standard Debug entry for a non-C# language in
  Rider, rather than needing a custom action.
- Phase 10:
  - That a plugin's `IUnitTestExplorerFromFile` can report another provider's elements from a `.feature`
    file without disturbing their discovery from `.feature.cs`.
  - That generated tests, including outline rows, can be mapped back to scenarios by `#line`.
  - That a `DebugHostProvider` launch stops on `.feature` breakpoints.
  - Option B only: the build and CI cost of the module's first RD protocol model.
- Whether a `Commenter` reproduces the server's comment toggle for doc strings and tables.
- That a class in package `io.cucumber.gherkin` inside our plugin jar can reach the package-private
  parser types in Rider's plugin classloader (the same runtime package), and that the IDE bundles no
  conflicting `io.cucumber` classes. Fallback: vendor the source.
- That Java Gherkin 39.1.0 behaves the same as .NET 39.1.0 on the shared corpus.
- Whole-buffer lexer rescans stay fast enough on large files.
- That the parser's error behaviour at end of file lets the adapter always close a complete tree.
- The effort estimates in [§6](#6-phases).

## 10. Decisions needed

1. ~~Commit to the PSI programme, or ship Phase 1 alone and stop~~ — **decided 2026-11-13: commit.**
   Phases 1–6 were built together in one change, and Phase 1 now depends on the PSI. The debugger policy is
   registered for `Gherkin`, and Step Into, the breakpoint line filter and the tag-link fix assume the PSI.
   Shipping Phase 1 alone would mean restoring the `TEXT` shim, which accepts breakpoints in every plain-text
   file. What is left to choose is the order of Phases 7–10. Release infrastructure for the backend build gates
   all of it reaching users.
2. ~~Phase 2 sequencing: before Phase 1 ships, or after~~ — **superseded**: Phases 1 and 2 were built
   together, so neither ships before the other.
3. ~~Legacy plugin: declare it incompatible~~ — done; verify Rider enforces it.
3a. Backend support window and an owner and response time for each new Rider release (see breakpoints plan §7.2).
4. Keep the inline Run lens as a setting for one release (proposed: yes).
5. Whether navigation (Phase 8) is in scope or only Rename and the `Commenter`.
6. ~~Phase 10: option A or option B~~ — **decided 2026-11-13: option A** in its daemon-stage form. The
   reflective dependency on Rider's internal `UnitTestElementHighlighting` is accepted. Each backend build
   targets one Rider release, and the per-release smoke test must cover the gutter marks. If the type or its
   constructor is missing, the stage logs it and adds no marks, so the inline Run lens must stay available in
   that case.
