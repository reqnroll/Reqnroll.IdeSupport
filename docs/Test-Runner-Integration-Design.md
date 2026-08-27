# Test Runner Integration — Design

> **Status: Ready for implementation** — design drafted and all feasibility spikes resolved 2026-08-05.
> §6 test-result correlation confirmed live for all three IDEs (VS/VS Code via a `dotnet test` spike,
> Rider independently via Chris's devcontainer run); §7 items 1, 3, 5 resolved by decompilation; item 6
> resolved as a live-confirmed design constraint (target the whole parameterized method, not one row).
> Item 2 (Rider's exact `SMTestProxy` plugin-API accessor) and item 4 (breakpoint/DAP, explicitly out of
> scope) are the only remaining items, and neither blocks starting implementation.
> **Audience:** Core team contributors
> **Tracks:** [#262](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/262)
> **Scope decisions confirmed by Chris (2026-08-05):** in scope now (feasibility spike first, not
> deferred); Scenario Outline example rows follow the `allowRowTests` `reqnroll.json` setting, not a
> fixed UX choice; generated tests live in `<feature>.feature.cs`, a build output, method names derived
> from scenario titles; breakpoint/DAP support (§7 item 4, Appendix B) explicitly **out of scope** for
> this issue; VS Code goes with **Option 2 — no `TestController`**, CodeLens + custom gutter decorations
> only, no native Test Explorer tree presence (§5/§6) — chosen over owning a `TestController` to avoid
> duplicate entries against the .NET/C# Dev Kit testing extension's own listing of the same generated
> methods, since VS Code's Testing API has no way to delegate execution to another extension's
> controller and read its result back (confirmed against `vscode.d.ts` and VS Code's own command source
> — see §5).
>
> **VS Code — final status (issue #504, 2026-08-27): no run mechanism of its own.** The full arc: shipped
> as Option 2 above → migrated to an owned `TestController` when #504 showed C# Dev Kit's discovery
> already addresses `.feature` locations → reconsidered and fully reverted (this update) once live use
> showed the `TestController` wasn't adding value C# Dev Kit's own gutter/Test Explorer integration
> doesn't already provide. VS Code's Reqnroll extension now has **no Run/Debug/CodeLens/TestController
> for scenarios at all** — entirely deferred to C# Dev Kit, including its known discovery flakiness
> (§6's live-testing note on this being unreliable across a VS Code relaunch stands, but is accepted as
> C# Dev Kit's own problem to fix, not this extension's to work around). VS and Rider are unaffected by
> this — see their own sections in §5/§6.

---

## 1. Nature of the changes

Three IDE-side, mostly-independent UX pieces, all keyed off the same underlying question — "which
generated C# test method does this `.feature` scenario (or Scenario Outline example row) correspond
to?":

1. **Run/debug gutter affordance** on each `Scenario:`/`Scenario Outline:` line, invoking the IDE's
   own native test runner against the mapped method.
2. **Pass/fail indicator** on the same line, reflecting the last run's outcome for that scenario (or,
   for an Outline, an aggregate/per-row breakdown).
3. **Failed-step gutter mark** on the specific step line a failed scenario stopped at, with a hover
   tooltip carrying the captured error.

None of this is an LSP feature — LSP has no run/debug/test-result vocabulary. What *is* shared and
worth building once is the **mapping layer** (§3): every IDE needs the same scenario→test-method
resolution, and getting it right (Outline row-vs-parameterized behavior, generated-name sanitization)
is the one piece of this issue that's fiddly enough to be worth centralizing rather than
reimplementing three times.

---

## 2. Ground truth: how Reqnroll's generator names things

Confirmed by decompiling `Reqnroll.Generator.Generation.UnitTestMethodGenerator`
(`Reqnroll.Generator.dll`, shipped in the `Reqnroll.Tools.MsBuild.Generation` package) and
cross-checking against this repo's own generated `.feature.cs` specs — not inferred from docs.

**Regular scenario.** Test method name = `scenario.Name.ToIdentifier()` (the generator's own
identifier-sanitization helper — strips/replaces characters invalid in a C# identifier). The
`DisplayName` trait/attribute carries the scenario title verbatim. Every generated step statement is
wrapped in its own `#line N "path/to/File.feature"` pragma pointing at that step's real source line.

**Scenario Outline, `allowRowTests = true`** (the default we observed live in this repo's own specs —
xUnit `[Theory]` + one `[InlineData(...)]` per `Examples:` row; NUnit/MSTest providers use their own
row-attribute equivalents through the same `IUnitTestGeneratorProvider.SetRow` call). **One method**,
named `scenarioOutline.Name.ToIdentifier()`. `DisplayName` is the outline title for every row — a
specific row has no name of its own; its identity at runtime comes only from the parameter values in
that row's data attribute. The method's steps still get per-step `#line` pragmas, but those point at
the Outline's own step lines (shared across all rows), not at anything row-specific.

**Scenario Outline, `allowRowTests = false`** (`GenerateScenarioOutlineExamplesAsIndividualMethods`).
**One method per example row**, named:

```
{scenario.Name.ToIdentifier()}_{exampleSetIdentifier}_{variantName.ToIdentifier()}
```

- `variantName` = the row's first cell value, if first-cell values are unique across the
  `Examples:` block; otherwise `"Variant {index}"` (0-based).
- `exampleSetIdentifier` = the `Examples:` block's own `Name:` if given; `null` (folded out of the
  name entirely) if there is exactly one unnamed block; `"ExampleSet {n}"` if there are multiple
  unnamed blocks.
- `DisplayName` = `"{scenario title}: {variantName}"`.

**Row-tests mode is not just the `allowRowTests` setting.** Decompiled from
`UnitTestFeatureGenerator.CreateTestClassStructure`, the actual flag passed into
`TestClassGenerationContext` is:

```csharp
_testGeneratorProvider.GetTraits().HasFlag(UnitTestGeneratorTraits.RowTests) && _reqnrollConfiguration.AllowRowTests
```

— an **AND** of the config setting and the target framework provider's own declared capability. All
five of Reqnroll's shipped providers currently declare `RowTests`, so in practice this AND is a no-op
today (every supported framework supports row tests) — but the resolver should still implement it as an
AND against a real capability lookup, not hardcode "always true", since that's one provider-version
bump away from changing per framework.

**Declaring class name** — confirmed by decompiling `UnitTestFeatureGenerator.GenerateUnitTestFixture`:
`string.Format(TestClassNameFormat, reqnrollFeature.Name.ToIdentifier())` with
`TestClassNameFormat = "{0}Feature"` by default, i.e. `{feature.Name.ToIdentifier()}Feature` — matches
the observed sample (`Discovery - Platform Compatibility` → `Discovery_PlatformCompatibilityFeature`).
Namespace defaults to `ReqnrollTests` unless the MSBuild-driven code generation supplies a target
namespace (normally the project's root namespace plus relative folder path) — the resolver needs that
from existing project/namespace-resolution infrastructure, not from this generator class itself.

**Row-attribute name per test-framework provider** — decompiled directly from each provider's `SetRow`.
**Caveat that cost a wrong first pass**: several frameworks resolve their actual provider *type* by a
string key (`UseUnitTestProvider("mstest")`/`"nunit"`/`"xunit"`/etc.) registered by a small
per-framework `Reqnroll.<Framework>.Generator.ReqnrollPlugin.dll`, separate from the core
`Reqnroll.Generator.dll`. For MSTest specifically, that string key resolves to `MsTestV2GeneratorProvider`
(or `MsTestV4GeneratorProvider` when the project's `TargetMsTestVersion` is 4+, a subclass that only
overrides class-cleanup/display-name details) — **not** the plain `MsTestGeneratorProvider` base class
that also lives in `Reqnroll.Generator.dll` and is what a naive type-name search finds first. That base
class does throw `NotSupportedException` from `SetRow`/`SetRowTest` and declares `GetTraits() => None`,
but it's dead weight from this project's perspective — never the type actually instantiated for a
real MSTest project. Confirmed by decompiling `MsTestV2GeneratorProvider`, which **does** support row
tests via `DataRowAttribute` and declares `RowTests | ParallelExecution`, same as every other framework.
NUnit/xUnit's plugin DLLs register by name only (no version-specific subclass indirection), so their
`Reqnroll.Generator.dll` types decompiled below are confirmed correct as-is; TUnit/xUnit.v3 register
their provider type directly rather than by name, same conclusion.

| Framework | Row attribute | `GetTraits()` | Notes |
|---|---|---|---|
| xUnit (v2) | `Xunit.InlineDataAttribute` | `RowTests \| ParallelExecution` | Method carries `Xunit.SkippableTheoryAttribute` |
| xUnit.v3 | `Xunit.InlineDataAttribute` | includes `RowTests` | Method carries `Xunit.TheoryAttribute` (not the `Skippable` variant — v3 handles skip via the attribute's own `Skip` property) |
| NUnit3 | `NUnit.Framework.TestCaseAttribute` | includes `RowTests` | Method carries `NUnit.Framework.TestAttribute` |
| TUnit | `TUnit.Core.ArgumentsAttribute` | includes `RowTests` | Method carries `TUnit.Core.TestAttribute` + `TUnit.Core.DisplayNameAttribute` |
| MSTest (`MsTestV2GeneratorProvider`/`MsTestV4GeneratorProvider`) | `Microsoft.VisualStudio.TestTools.UnitTesting.DataRowAttribute` | `RowTests \| ParallelExecution` | Method carries `TestMethodAttribute`. Quirk: for a single-column `Examples:` table, the generator inserts an unused dummy `string` parameter (`notUsed6248`) ahead of the tags parameter — a generator implementation detail, not something the resolver needs to reproduce, just be aware a single-arg row's parameter count differs from other frameworks' |

This resolves §7 item 5 for all five frameworks. The row-attribute allowlist in §3 should be seeded
directly from this table — and the resolver's own framework-capability lookup should be driven by the
same provider-name resolution mechanism (`UseUnitTestProvider`/`TargetMsTestVersion`), not by naively
matching the first class it finds with a plausible name, per the MSTest lesson above.

### AST-transforming generator plugins (e.g. `Reqnroll.ExternalData`) — a structural wrinkle

Flagged by Chris (2026-08-05) and confirmed against Reqnroll's actual open source
(`reqnroll/Reqnroll`, `Plugins/Reqnroll.ExternalData`), not taken on description alone. The
`ExternalData` plugin lets an `Examples:` block's row **data** live in an external file (CSV/XLS/etc.)
instead of the `.feature` file, selected by a tag. Its `ExternalDataTestGenerator.ParseContent` override
parses the `.feature` file completely normally first, then runs `IncludeExternalDataTransformation` on
the resulting **in-memory AST**, injecting synthetic `TableRow`s read from the external file — entirely
inside Reqnroll's own generator pipeline, strictly after the point where the LSP server's own,
independent Gherkin parse of the same `.feature` text would already be done. The LSP server's AST never
sees the injected rows; they exist only in the generator's private copy.

The tracing turned up a wrinkle sharper than "an `Examples:` block shows as header-only on screen":
`IncludeExternalDataTransformation.GetTransformedScenarioOutline` explicitly handles a `Scenario
Outline:` with **no `Examples:` block at all** (`scenarioOutline.Examples == null ||
!scenarioOutline.Examples.Any()`) by falling through to the same transform used for a genuinely plain
`Scenario:`, which constructs a brand-new `ScenarioOutline` from scratch using the external file's own
columns as the header. **A plain `Scenario:` block, with the tag applied and no visible `Outline`
keyword or `Examples:` block whatsoever, can generate as a fully row-tests-parameterized method** — the
on-screen file gives no indication it's parameterized at all beyond the step text possibly containing
`<placeholder>` tokens.

**Why this doesn't break the design, and why it would have if §3 had gone the other way**: the
architecture decision above — read the generated `.feature.cs` via Roslyn rather than predict names from
the `.feature` file's own structure — means the resolver was never going to trust the `.feature` file's
Examples-row count or `Scenario`/`Scenario Outline` keyword to determine parameterization in the first
place. It has to anyway, now, for a real reason rather than a defensive one: Tier 1 discovers *whether* a
scenario is parameterized and *how many* generated methods exist by finding methods on the class,
either by exact predicted name (row-tests mode: `scenario.Name.ToIdentifier()`) or by name-prefix match
(individual-methods mode: every method matching `{scenario.Name.ToIdentifier()}_.*`) — never by counting
`.feature`-file rows. This was already the plan; ExternalData is the concrete case that makes it a
requirement rather than a preference. One place in Tier 2 needs an explicit scope note as a result: the
"*N*th row-attribute always corresponds to the *N*th `Examples:` row in the `.feature` file" positional
claim (below) is about correlating a *specific selected `.feature` row* to a row-attribute index for
per-row addressing — and per-row addressing was already ruled out entirely in §7 item 6 (native runners
don't support it reliably even in the ordinary case). For an ExternalData scenario there's no `.feature`
row to select in the first place — no on-screen line exists to attach a per-row gutter action to — so
this doesn't need separate handling: the UI-level "only offer whole-scenario run, never per-row" behavior
for such scenarios falls out for free from gutter icons being rendered against the LSP server's own
Gherkin parse, which has nothing to render at a row that was never there.

**What the resolver must not do**: treat "0 rows visible in the `.feature` file's `Examples:` block" or
"no `Examples:` block at all" as "0 targets" or "not parameterized, use the plain-scenario naming rule."
Both are true of the `.feature` file's own AST and both are wrong conclusions about the generated code.
Resolution must always go through the generated `.feature.cs`, never short-circuit on the `.feature`
file's own shape. This generalizes past `ExternalData` specifically — any other generator plugin (first-
or third-party) that transforms the AST between parse and codegen has the same property, and the design
already accounts for the general case, not just this one plugin.

---

## 3. Mapping layer — architecture decision

Two ways to compute the scenario→test-method mapping. **Recommendation: read the generated
`.feature.cs` code-behind, don't re-predict it.**

| | Predict the name (reimplement §2's rules) | Parse the generated `.feature.cs` |
|---|---|---|
| Accuracy | Drifts silently if Reqnroll changes its sanitization/naming rules across versions | Always matches whatever this project's installed Reqnroll generator actually produced |
| Availability | Works even before a build | Requires the project to have been built at least once (code-behind is a build output) |
| Complexity | Have to track `allowRowTests`, `ToIdentifier()` semantics, and class-naming per Reqnroll version | Ordinary Roslyn parse — same toolkit `CSharpBindingDiscoveryService`/`StepDefinitionFileParser` already use for `.cs` scanning |

The repo already has Roslyn-based `.cs` scanning infrastructure (`LSP.Core/Parsing/CSharp`) built for
binding discovery. Parsing `<feature>.feature.cs` reuses that pattern and stays correct automatically
as Reqnroll's generator evolves, at the cost of only working post-build (acceptable: a run/debug gutter
action is meaningless before the project has ever built anyway, since there's no test to run).
Predicting names is the fallback if we later find `.feature.cs` isn't reliably present at the point the
gutter needs to render (e.g., before any build in a fresh clone) — worth revisiting if that turns out
to matter in practice, but not the starting design.

**Correction (issue #491, 2026-08-26):** "ordinary Roslyn parse" in the trade-off table above
understates the real cost at scale. `RunTestCodeLensService` calls `reqnroll/resolveTestTargets`
once per scenario/Outline/Examples-row in a file, and the original implementation re-read and
re-parsed the same, unchanged `<feature>.feature.cs` from scratch on every single one of those
calls. For a large stress-corpus feature file (~1,300 rows) this meant ~1,300 sequential Roslyn
parses of the same file, pegging a CPU core for the file's entire time open. The fix is a shared
`ICSharpSyntaxTreeCache` (`LSP.Core/Parsing/CSharp/`, see [Architecture §3 item
4](LSP-IDE-Support-Architecture.md#3-module-architecture)) that both `ScenarioTestTargetResolver`
and `CSharpAttributeLiteralResolver` (the Rename feature's analogous per-attribute case) now read
through, so repeated resolutions against the same unchanged file within one logical operation
reuse the same parse. The "post-build only" trade-off itself is unaffected by this — the cache's
disk-mode freshness check (last-write-time) is what makes it safe to cache across an actual
rebuild without a dedicated file watcher.

**Correction (issue #495, 2026-08-26):** #492's shared syntax-tree cache made one
`reqnroll/resolveTestTargets` call cheap (~150ms → ~3.6ms), but every IDE-side caller still issued
one such call *per scenario in the whole file* on every recompute, not just for the scenario(s)
actually needed — a leftover "resolve everything, then filter" shape from before per-line/per-lens
callers existed. On the `VeryLargeFeature` stress corpus (~2,000+ scenarios) that still meant a
30-45s wall-clock walk per recompute, independently slow enough to exceed VS's own classic-CodeLens
per-data-point timeout (~26s) regardless of how cheap any one resolution had become. Root-caused and
fixed per client, since each platform's own extensibility contract determines what's actually
possible:

- **VS (classic CodeLens)** — `RunTestCodeLensService` split into `GetTargetsForLineAsync(fileUri,
  line)` (one resolution, called by each line's own `RunTestCodeLensDataPoint.GetDataAsync`) and
  `GetTagLocationsAsync(fileUri)` (symbol tree only, zero `resolveTestTargets` calls, used only by
  `RunTestCodeLensTaggerProvider` to know which lines get a tag placement). The async data-point API
  was already per-line; the fix was ending the whole-file walk each data point triggered to serve
  itself, not adding new async plumbing.
- **VS Code** — `runCodeLens.ts`'s `CodeLensProvider` now implements the standard two-phase
  contract: `provideCodeLenses` places one unresolved lens per scenario symbol (symbol tree only),
  and `resolveCodeLens` — which VS Code calls lazily, only for lenses that actually scroll into
  view — is where the single `reqnroll/resolveTestTargets` call for that one lens happens. VS Code's
  own API already supported this; the previous implementation simply never used the `resolveCodeLens`
  half of it.
- **Rider** — IntelliJ's `CodeVisionProvider.computeCodeVision(editor, uiData)` has no visible-range
  parameter and no per-entry resolve phase; it is always asked for the whole document, on the
  platform's own schedule. There is no lever to make Rider ask for only the visible lines. Instead,
  `RunTestTargetCache` (`Rider/testrunner/`) memoizes each `(uri, line)`'s resolved targets, keyed by
  a cheap identity string (scenario kind + name) built from the symbol tree — `RunLensSupport.computeEntries`
  still walks every scenario symbol on every call (unavoidable on this platform), but only re-sends
  `reqnroll/resolveTestTargets` for a scenario whose identity actually changed since the last walk.
  The cache is invalidated wholesale by the same `reqnroll/refreshCodeLenses` notification the sibling
  Hook/StepUsages CodeVision providers already act on (`ReqnrollCodeLensRefreshInterceptor`) — the real
  signal that an underlying resolution changed independent of the `.feature` file's own text (e.g. a
  `[Binding]` method renamed in `.cs`).

Net effect: VS and VS Code now issue `reqnroll/resolveTestTargets` proportional to the number of
*currently visible* Run lenses, not the scenario count of the whole file. Rider still walks the whole
symbol tree per recompute (platform limitation) but skips the RPC for everything that hasn't changed.

**"Parse the code-behind" is not one uniform operation, though — it splits into two tiers with very
different stability, because Reqnroll ships five test-framework providers (xUnit, xUnit.v3, NUnit,
MSTest, TUnit) with different attribute vocabularies and argument shapes that can also change across
provider versions:**

**Tier 1 — method/class existence and FQN.** Entirely framework-agnostic: `CreateTestMethod`/
`CreateScenarioOutlineTestMethod` (§2) always emit a plain C# method/class declaration regardless of
which `IUnitTestGeneratorProvider` is active — only the *attributes decorating* the method are
framework-specific. A Roslyn symbol walk that just resolves "does a method named `X` exist on type `Y`,
and what's its FQN" needs no per-framework knowledge at all. This alone is sufficient for: a plain
scenario, and "run the whole Outline" (invoke the Theory/parameterized method as a unit) in row-tests
mode.

**Tier 2 — per-row correlation for row-tests Outlines.** Assumes the `.feature` file's own `Examples:`
rows are what generated the row-attributes in document order — true for ordinary Outlines, **not** true
for a scenario transformed by an AST-injecting generator plugin like `Reqnroll.ExternalData` (see the
dedicated subsection above), where the `.feature` file may show zero rows or no `Examples:` block at
all. This is fine in practice only because per-row addressing was already ruled out for every IDE in §7
item 6 regardless of this case — Tier 2's positional correlation is used for *counting* rows to decide
between "run the whole method" and nothing more, not for selecting a specific row to run, so it degrades
safely (falls back to "run the whole method," same as the ordinary per-row-addressing fallback) rather
than needing separate handling for the ExternalData case. This is where framework-specific attribute
shape would normally bite — but it can be sidestepped almost entirely. The decompiled generator
(`GenerateScenarioOutlineExamplesAsRowTests`, §2) iterates `scenarioOutline.Examples` then each
`example.TableBody` in **document order**, calling `_unitTestGeneratorProvider.SetRow` exactly once per
row, in that order, with no reordering afterward. That ordering guarantee means: the *N*th row-attribute
instance on the generated method always corresponds to the *N*th `Examples:` row in the `.feature` file
— a fact of the generator's own control flow, not something that has to be reverse-engineered from
attribute argument contents. So the resolver never needs to parse `InlineData("net8.0", "0", ...)`-style
argument literals at all — it only needs to **count** row-attribute instances on the method, keyed by
the allowlist confirmed in §2 (`InlineDataAttribute` for xUnit/xUnit.v3, `TestCaseAttribute` for NUnit3,
`ArgumentsAttribute` for TUnit, `DataRowAttribute` for MSTest), and correlate by position against the
`.feature` file's own `Examples:` rows — which the LSP server already has from its native Gherkin parse,
independent of the C# side entirely.

That allowlist is fully known now (§2 table) — bounded by Reqnroll's five supported providers, and only
needs a new entry when Reqnroll adds or drops a supported test-framework provider, not on ordinary
framework point releases, since a given installed Reqnroll version's provider always emits one fixed
attribute type for that framework. `LSP.Core/TestTargets/` needs a small
`IReadOnlyDictionary<TestFramework, string> RowAttributeTypeName` table seeded from §2, sourced from
whatever mechanism the project's referenced test framework is already detected by (assumption to
confirm — F2 binding discovery may already resolve this from package references; if not, it needs its
own detection step here — and, per the MSTest lesson in §2, that detection needs to resolve the actual
provider the project's `Reqnroll.<Framework>.Generator.ReqnrollPlugin` wires up by name, including
MSTest's `TargetMsTestVersion`-driven V2-vs-V4 split, not just the referenced NuGet package). The
resolver's row-tests-vs-individual-methods branch should still replicate the generator's own
`GetTraits().HasFlag(RowTests) && config.AllowRowTests` logic (§2) rather than assuming row tests are
always available — currently true for all five frameworks, but worth keeping as a real capability check
rather than a hardcoded assumption.

**Residual risk this does *not* eliminate**, and the sharper version of the concern: even with
positional row-correlation solved on our side, *invoking* "run row N specifically" still goes through
each IDE's native test-explorer API, and some of those APIs address a specific parameterized case only
by matching the **runner's own formatted display name** for that case (which itself embeds the row's
argument values, formatted per test-framework-and-version) rather than by a stable positional index. If
that's the only addressing mechanism a given IDE/framework combination exposes, framework-version
formatting differences leak back in at the invocation layer even though our resolver itself stayed
framework-agnostic there. This needs checking per IDE alongside the §6 test-result-channel spike — if
positional/index-based invocation isn't reliably available, the fallback is "Outline gutter runs the
whole parameterized method, not an individual row" rather than attempting brittle display-name matching.

**New core service**, `LSP.Core/TestTargets/`:

```csharp
public interface IScenarioTestTargetResolver
{
    IReadOnlyList<ScenarioTestTarget> Resolve(Uri featureUri, GherkinRange scenarioRange);
}

public sealed record ScenarioTestTarget(
    string DeclaringTypeFullName,   // e.g. Discovery_PlatformCompatibilityFeature
    string MethodName,              // generated method name
    bool IsParameterized,           // true for the row-tests Outline case
    IReadOnlyDictionary<string,string>? RowArguments, // present only when resolving a specific example row
    int? RowIndex);                 // 0-based index into the data-attribute list, for parameterized methods
```

For a plain scenario this resolves to exactly one target. For a Scenario Outline, resolving at the
`Scenario Outline:` line returns every row's target (so the IDE can offer "run all rows"); resolving at
an individual `Examples:` row returns just that row's target — in row-tests mode, same `MethodName` for
every row, distinguished by `RowIndex`/`RowArguments`; in individual-methods mode, a distinct
`MethodName` per row.

---

## 4. New LSP message

Following the existing custom-request pattern ([F17](LSP-IDE-Support-Feature-Designs.md#f17--hook-navigation)'s
`reqnroll/goToHooks`, [F25](LSP-IDE-Support-Feature-Designs.md#f25--hook-match-count-codelens-hook-bindings)'s
`reqnroll/goToMatchingScenarios`), named after the message per [[protocol-handler-naming]]:

| Direction | Method | Purpose |
|-----------|--------|---------|
| Client → Server | `reqnroll/resolveTestTargets` (`uri`, `range`) | Resolve the generated test method(s) for a scenario or Outline range |
| Server → Client | `ScenarioTestTarget[]` | See §3 shape |

Handler: `ResolveTestTargetsHandler` in `LSP.Server/Features/TestTargets/`, thin wrapper delegating to
`IScenarioTestTargetResolver`. No new registration-option surface needed (not a standard LSP capability;
a plain `workspace/executeCommand`-style custom request is enough, matching F17/F25).

---

## 5. Run/debug gutter affordance, per IDE

| VS Code | Visual Studio | Rider |
|---------|---------------|-------|
| 🔧 Plugin — `CodeLens` + custom gutter decorations (own execution, no `TestController`) | 🔧 Plugin — Test Explorer editor-margin `KnownMonikers` | 🔧 Plugin — `CodeVisionProvider` (as-built; see note below — `RunLineMarkerContributor` wasn't viable) |

All three reuse each platform's own run/debug/pass/fail glyph set rather than inventing
Reqnroll-branded icons (see the issue's own survey — no distinct Gherkin/BDD icon convention exists
elsewhere to match), except VS Code (below), which owns its glyph rendering since it isn't going
through the native Testing UI.

> **VS Code note: historical record only, not current.** Everything below in this VS Code bullet
> (Option 2, the CodeLens, the analysis that led to it) describes what was actually shipped and later
> removed. See the top-of-document status block ("VS Code — final status") for where this landed: no
> run mechanism of VS Code's own at all, fully deferred to C# Dev Kit. Kept here because the
> `vscode.tests` API research is still accurate and would need re-doing if this is ever revisited.

- **VS Code — decided against delegating to C# Dev Kit's `TestController` (Option 2).** Investigated
  whether the CodeLens could invoke the existing .NET/C# Dev Kit testing extension's own run/debug
  machinery directly, mirroring VS's approach (below) — checked against `vscode.d.ts` and VS Code's own
  command source rather than assumed. Two findings closed this off:
  1. **No cross-controller result read.** The entire `vscode.tests` namespace exports exactly
     `createTestController` — there is no API to enumerate another extension's controller or read its
     `TestRun` results. Each controller's pass/fail/message data is private to the extension that
     created it. (Confirmed directly against `vscode.d.ts`; an earlier guess that a `tests.testResults`
     snapshot API existed was wrong and has been removed from this doc.)
  2. **Triggering via the generic run-at-cursor command is possible but comes with a real cost, and
     still doesn't solve (1).** `testing.runAtCursor`/`debugAtCursor` (`ExecuteTestAtCursor` in VS
     Code's own `testExplorerActions.ts`) dispatch through the central `ITestService`, which does span
     all registered controllers — so it *could* reach a test item C# Dev Kit's controller owns. But it
     requires a real, focused code editor at the exact cursor position (`codeEditorService.getActiveCodeEditor()`
     + `editor.getPosition()`) — there's no command form that accepts an arbitrary URI+range without
     actual editor focus. Triggering it from a `.feature` CodeLens would mean navigating to the
     generated method's line in `.feature.cs` first, likely a visible tab switch away from the
     `.feature` file the user is looking at. And even if that UX cost were accepted, finding (1) still
     means we'd have no way to read the result back afterward.

  **Decision (2026-08-05): Option 2 — own execution, no `TestController`, no native Test Explorer tree
  presence.** `▶ Run` / `🐛 Debug` `CodeLens` per scenario/row (same shape as F18's step-usage
  `CodeLensProvider` — new provider, no new plumbing pattern), calling `reqnroll/resolveTestTargets`
  to resolve each lens, then shelling to `dotnet test --filter "FullyQualifiedName=..."` directly
  against the resolved `DeclaringTypeFullName`/`MethodName` (confirmed to precisely target one
  method — §6). Pass/fail and failed-step state are tracked entirely in our own extension state and
  rendered via custom `TextEditorDecorationType` gutter icons plus the CodeLens label, not through
  `vscode.TestRun`/`TestMessage`. Trades away native Test Explorer tree presence for avoiding
  duplicate entries against C# Dev Kit's own listing of the same generated methods, and keeps VS
  Code's design fully within our own extension's control — no dependency on another extension's
  behavior or its future changes.
  **Correction (issue #495):** the resolution call happens in `resolveCodeLens`, not
  `provideCodeLenses` — VS Code calls the former lazily, only for lenses that scroll into view, so
  `provideCodeLenses` itself only ever places unresolved lens ranges from the symbol tree. See the
  §3 correction above.
- **Rider**: as-built, a `CodeVisionProvider` (`RunTestCodeVisionProvider`/`RunLensSupport`), not the
  `RunLineMarkerContributor` originally proposed here — this plugin registers `.feature` with no
  `ParserDefinition`/PSI tree at all (see `ReqnrollFeatureLanguage`'s own doc comment), and
  `RunLineMarkerContributor` is inherently PSI-based, so `CodeVisionProvider` (operates on
  `Editor`/`Document` offsets, same as every other `.feature` editor feature in this plugin) is used
  instead — the same substitution already made for the closely analogous hook-match-count lens
  (`HookCodeVisionProvider`). Rendered inline rather than as a gutter icon as a result (Rider's
  `CodeVisionProvider` API doesn't offer a gutter-icon presentation), using ▶/✓/✗ glyphs in place of
  `AllIcons.Actions.Execute`/`TestState.Green2`/`Red2`. Invokes Rider's native JVM-side test runner
  against the resolved method (`RunTestRunner`). Note the current `reqnroll/Reqnroll.Rider` plugin
  has **no** scenario-level run marker today
  ([reqnroll/Reqnroll.Rider#8](https://github.com/reqnroll/Reqnroll.Rider/issues/8)), so there's no
  existing behavior to match or avoid conflicting with. See the §3 correction above for how this
  provider avoids re-resolving every scenario on every recompute, given `CodeVisionProvider` has no
  visible-range or per-entry-resolve hook to lean on the way VS Code's `resolveCodeLens` does.
- **Visual Studio — resolved, §7 item 3.** There is no separate "Test Explorer editor margin"
  extension point to investigate — decompiling VS 18's own `Microsoft.VisualStudio.TestWindow.CodeLens.dll`
  shows that VS's built-in run/debug/pass-fail affordance for ordinary `[Fact]`/`[TestMethod]`/`[Test]`
  methods **is itself a classic CodeLens data-point provider** (`TestStatusProvider : AbstractTestProvider
  : IAsyncCodeLensDataPointProvider`, `[ContentType("CSharp")]`/`"Basic"`/`"C/C++"`-scoped). It's the exact
  same API family (`Microsoft.VisualStudio.Language.CodeLens`) [F24](LSP-IDE-Support-Feature-Designs.md#f24--hook-match-codelens-featurescenariostep)
  already built a bridge for — no new extension-point research needed, reuse F24's `HookCodeLensDataPointProvider`
  pattern (in-process `ITaggerProvider` + out-of-process `IAsyncCodeLensDataPointProvider`) directly, scoped
  to `Gherkin` content instead of `CSharp`.

  Better still, `TestStatusProvider`'s glyphs come from `KnownMonikers.StatusOK`/`StatusError`/
  `StatusWarning`/`StatusAlert` (not the `KnownMonikers.RunTest`/`TestPassed`/... set originally assumed
  here), and — this is the useful part — its "Run"/"Debug" actions in the Details popup are wired to
  **VS's own internal Test Explorer commands**, not anything provider-specific:

  ```csharp
  new CodeLensDetailEntryCommand { CommandId = 898, CommandName = ".TestExplorer.RunTestsFromCodeLens",
                                    CommandSet = Guid.Parse("1E198C22-5980-4E7E-92F3-F73168D1FB63") }
  new CodeLensDetailEntryCommand { CommandId = 899, CommandName = ".TestExplorer.DebugTestsFromCodeLens",
                                    CommandSet = /* same GUID */ }
  ```

  invoked via `CodeLensDetailPaneCommand { CommandId = ..., CommandArgs = new[] { testMethodIdentifier } }`,
  where `testMethodIdentifier` is a `Microsoft.VisualStudio.TestWindow.TestMethodIdentifier(OutputFilePath,
  NormalizedFullyQualifiedName, ManagedType, ManagedMethod)` — fields that map directly onto
  `ScenarioTestTarget.DeclaringTypeFullName`/`MethodName` (§3) plus the test assembly's build output path
  (already known to the LSP server's project model). Our own custom CodeLens data point for `.feature`
  scenarios can emit a `CodeLensDetailPaneCommand` against these **same command IDs**, letting Test
  Explorer itself do the actual run/debug — no VS-specific run invocation logic needed at all, just
  building the right `TestMethodIdentifier`. `TestMethodIdentifier` addresses a **method**, not an
  individual parameterized-test row (its `Equals` only compares `OutputFilePath`+`ManagedType`+
  `ManagedMethod` or the FQN) — confirms the §3 residual risk finding for VS specifically: row-tests
  Outline invocation targets "run the whole parameterized method," not one row, via this path.

### Scenario Outline — "run all examples" affordance

§3 already has the resolver return every row's `ScenarioTestTarget` when `reqnroll/resolveTestTargets`
is called at the `Scenario Outline:` line, specifically so a gutter action there can run every example.
What that action actually *does* with those targets splits by generation mode:

**Row-tests mode (default) — free.** All targets share one `MethodName`. "Run all examples" is
identical to the plain "run this method" action already described above for a single scenario — the
native runner executes every `InlineData`/`TestCase`/`DataRow`/`Arguments` row as part of running that
one method. No extra invocation logic needed in any IDE; the Outline-level gutter action and the
single-scenario gutter action are the same code path.

**Individual-methods mode (`allowRowTests = false`) — needs a real multi-target invocation, one per IDE:**

- **VS Code**: owns its own `dotnet test` invocation, so this is the simplest case — OR the resolved
  targets' exact FQNs into one filter expression, `dotnet test --filter "FullyQualifiedName=A|FullyQualifiedName=B|..."`,
  and parse each method's own stdout block from the combined output. (A `~`-contains prefix filter on
  the shared `{scenario.Name.ToIdentifier()}_` prefix, e.g. `FullyQualifiedName~ThisScenario_`, would
  also work and needs fewer terms, at the cost of a small collision risk if another scenario's generated
  name happens to contain the same substring — the exact-FQN OR list is safer and preferred.)
- **Visual Studio**: `CodeLensDetailPaneCommand.CommandArgs` is an array — the decompiled
  `TestStatusProvider` only showed a single-element usage (`new TestMethodIdentifier[1] { testMethod }`),
  but `.TestExplorer.RunTestsFromCodeLens`/`DebugTestsFromCodeLens` are the same commands Test Explorer's
  own "run selected tests" multi-select action drives, so passing an N-element `TestMethodIdentifier[]`
  built from all N resolved targets is expected to work the same way. **Not yet confirmed** — the VS
  live check already needed for §6's ServiceHub wiring should also verify multi-target `CommandArgs`
  specifically, since only the single-target shape has been observed in the decompiled source.
- **Rider**: whether a single `RunLineMarkerContributor` action can target multiple discovered test
  items in one invocation, or whether it's constrained to one per marker, **hasn't been checked** — a
  follow-up for whoever implements the Rider side, likely resolvable by looking at how Rider's own
  built-in `[Fact]`/`[Test]`-class-level line marker (which already offers "run all tests in this class")
  is implemented, since that's the same shape of problem.

---

## 6. Test-result correlation

Substantially de-risked by research below, but each IDE still needs one **live** confirmation before
implementation — none of this has been run against a real Reqnroll-generated test yet. Status per IDE:

**Visual Studio pass/fail glyph — reconsidered and implemented via reflection (issue #504 follow-up,
2026-08-27).** Initially decided against (see the superseded reasoning originally here): unlike
`TestMethodIdentifier` (public, already used by the shipped Run/Debug delegation — see §5),
**`ICodeLensTestInformationService`, `CodeLensTestInformationProxy`, `CodeLensTestInformationCallbackService`,
and `RemoteTestWindowServiceProvider` are all `internal`** to `Microsoft.VisualStudio.TestWindow.Internal.dll` —
not part of the public extensibility surface, no compile-time contract, no deprecation notice if a VS
servicing update reshapes or removes any of it. Chris subsequently decided the glyph was worth the risk
provided any failure degrades gracefully rather than crashing the CodeLens host.

**As-built**: `RunTestOutcomeBridge` (`src/VisualStudio/.../RunTestCodeLens/RunTestOutcomeBridge.cs`)
reflects into `RemoteTestWindowServiceProvider.Instance.GetServiceStreamAsync` → constructs a
`CodeLensTestInformationProxy` over the returned stream (passing a `null` callback target — this bridge
only polls, it never subscribes to change notifications) → invokes `ICodeLensTestInformationService.GetTestOutcomeAsync`
via the (also internal) interface's `MethodInfo`, since the concrete proxy implements it as an explicit
interface member. The outcome enum value is read back by name (`.ToString()`) rather than cast to the
real `TestOutcome` type, so even a renamed/reshaped enum degrades to "unrecognized" instead of an
`InvalidCastException`. Every step is wrapped in one `try/catch`; the **first** failure anywhere in the
chain sets a permanent-for-the-process `_unavailable` flag — no retry storm, no per-call reflection cost
once the API is known gone, and Run/Debug (§5, unaffected — public API only) keep working regardless.
Deliberately does **not** call the simpler `AbstractTestProvider.GetServiceProxyAsync` convenience
method VS's own `TestStatusProvider` uses: that method depends on a private static VS-process-id field
only populated once VS's own CSharp/Basic/C/C++-scoped test CodeLens providers have themselves run in
this ServiceHub host — not guaranteed for a user who only opens `.feature` files. The glyph mapping
itself (`TestOutcome` → `KnownMonikers.StatusOK`/`StatusError`/`StatusWarning`) needs no reflection —
`KnownMonikers` is a fully public, stable API, mirroring `TestStatusProvider.ToImageId`.

**Visual Studio — API shape confirmed by decompilation; the stack-trace question is now live-tested,
and the answer is a correction, not a confirmation.** The same `ICodeLensTestInformationService` that
backs `TestStatusProvider` (§5) is reachable over the identical out-of-process CodeLens ServiceHub
channel F24 already wired up (`RemoteTestWindowServiceProvider` → `GetServiceStreamAsync` →
`ICodeLensTestInformationProxy`) — **now consumed via reflection, see the correction immediately above.**

```csharp
Task<TestOutcome> GetTestOutcomeAsync(Guid dataPointId, TestMethodIdentifier testMethod, CancellationToken ct);
Task<ICollection<CodeLensTestDetail>> GetTestDetailsAsync(TestMethodIdentifier testMethod, int limit, CancellationToken ct);
```

`GetTestOutcomeAsync` gives the aggregate pass/fail/skip for the pass/fail gutter indicator directly —
no separate result channel to build, it's the same bridge as the run/debug affordance. `GetTestDetailsAsync`
returns `CodeLensTestDetail { TestCaseRecord, TestResultRecords: ICollection<TestResultRecord>, Outcome, Duration }`
— `TestResultRecords` is a **collection**, one entry per parameterized-test row for a row-tests Outline
method, each carrying `Outcome`, `ErrorMessage`, `ErrorStackTrace`, `StandardOutput`, `StandardError`,
`DisplayName`, `TestCaseDisplayName` (decompiled field list, `Microsoft.VisualStudio.TestWindow.Records.TestResultRecord`).

**Live-tested (2026-08-05)**: built a throwaway Reqnroll 3.3.4 + xUnit project with a scenario whose
middle step (`When`) deliberately throws, ran `dotnet test`, and inspected the actual failure output.
The hypothesis that `ErrorStackTrace` would report the *failing step's* `.feature:line` — reasoned from
the `#line`-pragma mechanism in §7 item 4 — **is wrong**. The stack trace does contain a `.feature:line`
frame, but decompiling the generated `.feature.cs` explains why it's the wrong line: every step's
`#line N` pragma is immediately followed by `#line hidden` (§2's generator behavior), and the scenario's
final `await this.ScenarioCleanupAsync();` call — where a previously-caught step exception is actually
re-thrown — sits inside that trailing hidden region. The CLR attributes the re-throw frame to the
*last* non-hidden `#line` before it, which is always the scenario's **last step**, regardless of which
step actually failed. Confirmed directly: a 3-step scenario failing on step 2 reported `.feature:line 6`
(the `Then` line) in `ErrorStackTrace`, not line 5 (the failing `When`). **This is a generation artifact,
not a per-scenario coincidence** — it will misattribute the failure to the wrong step on every scenario
where the failing step isn't the last one. `ErrorStackTrace`/PDB sequence points are not usable for the
failed-step mark.

**The reliable signal, found in the same spike**: Reqnroll's own step-by-step trace, already present in
`TestResultRecord.StandardOutput` by default — no `reqnroll.json` opt-in required (none was configured
in the throwaway project and the trace appeared anyway). It pairs each step's keyword+text with a
`-> done: ... (0.0s)` / `-> error: ... (0.0s)` / `-> skipped ...` outcome line, in step execution order:

```
Given a passing step
-> done: StepDefinitions.GivenAPassingStep() (0.0s)
When a failing step is executed
-> error: deliberate failure for stack trace inspection (0.0s)
Then this line is never reached
-> skipped because of previous errors
```

This is directly parseable and correlates unambiguously to the `.feature` file's own step order — which
the LSP server already has from its native Gherkin parse (§3), no C#-side line mapping needed at all.
This corrects and replaces the original §6(c) hypothesis (Reqnroll's `trace.traceTimings` config as an
"opt-in" requirement) — the default step trace already carries what's needed.

**Full outcome-prefix vocabulary** (decompiled from `Reqnroll.Tracing.TestTracer`, not inferred from
one example — the throwaway spike only exercised `done`/`error`/`skipped`; the Rider live check below
exercised two more the parser needs to handle):

| Prefix | Emitted by | Meaning |
|---|---|---|
| `done: {match} ({duration}s)` | `TraceStepDone` | Step passed |
| `error: {message} ({duration}s)` | `TraceError` (via `WriteErrorMessage`) | Step threw |
| `skipped: {message}` | `TraceStepSkipped` | Step explicitly skipped (e.g. via a skip exception) |
| `skipped because of previous errors` | `TraceStepSkippedBecauseOfPreviousErrors` | Step never ran — an earlier step in the scenario already failed |
| `pending: {match}: {message}` | `TraceStepPending` | Step bound but marked pending (`PendingStepException`) |
| `binding error: {message}` | `TraceBindingError` | Step text matched more than one binding (`AmbiguousBindingException`) or another binding-resolution failure |
| `undefined: {skeleton}` | `TraceNoMatchingStepDefinition` | No binding matches the step text at all |

The parser needs to branch on all seven, not just the three seen in the original spike — `binding error`
and `undefined` in particular are failures with **no underlying step method to attribute a stack trace
to at all**, reinforcing that stdout-trace parsing (not stack-trace inspection) has to be the mechanism,
since those two outcomes have no C# frame to misattribute in the first place.

**VS Code — decided: own execution, no `TestController` (Option 2, confirmed 2026-08-05).** The Testing
API's `vscode.TestMessage.location` field would have been the exact primitive for the failed-step mark,
but as §5 details, `vscode.tests` exposes no cross-controller result read at all (confirmed against
`vscode.d.ts`), and the generic `testing.runAtCursor` delegation path requires real editor focus and
still wouldn't solve that read problem — so this design does not go through the Testing API or
`TestController` at all, and does not attempt to invoke or observe the .NET/C# Dev Kit extension's own
controller.

**Live-tested (2026-08-05)**: `dotnet test --filter "FullyQualifiedName=Namespace.ClassFeature.MethodName"`
correctly and precisely targeted the one generated method, matching exactly the
`DeclaringTypeFullName`/`MethodName` shape `ScenarioTestTarget` (§3) already produces — no
display-name matching or extra lookup needed. Our CodeLens's run/debug handler shells to this directly,
captures stdout, and parses Reqnroll's `-> done:`/`-> error:`/`-> skipped` lines in order against the
`.feature` file's own step list (§6, above). Pass/fail state and the failed-step location are rendered
via our own `TextEditorDecorationType` gutter icons (with a hover message carrying the captured error
text) and CodeLens label updates — entirely within our own extension, no `vscode.TestRun`/`TestMessage`
involved, and no presence in the native Testing panel. What's left is standard implementation work
(decoration lifecycle on file edit/close, debounce during a run, `dotnet test --filter` invocation for
the debug case via a debug-adapter launch config rather than plain `dotnet test`) — no open design
question remains for VS Code.

**Rider — live-confirmed (2026-08-05, via the devcontainer, Chris driving).** Chris built the Rider
devcontainer, ran a real `SampleReqnrollSolution` with an ambiguous-binding scenario (two step bindings
both matching "the second number is 5") through Rider's native Test Runner window, and reported back the
Test Runner's output pane directly (screenshot). This independently confirms both findings from the
`dotnet test` spike above, on a completely different test host/IDE:

- **Same stack-trace misattribution.** The `Reqnroll.AmbiguousBindingException`'s stack trace reported
  `SecondFeature.feature:line 10` — the scenario's **last** step ("Then another unused expression") —
  not line 7, the actual step that failed ("And the second number is 5"). Same `#line hidden`-region
  artifact as VS/`dotnet test`; confirms this is a property of Reqnroll's generated code, not anything
  IDE- or test-host-specific, so the design's "don't use stack traces for step attribution" conclusion
  applies uniformly across all three IDEs.
- **Same step-trace stdout, confirmed live.** Rider's console pane showed the identical
  `-> done:`/`-> skipped because of previous errors` lines, plus two outcomes the earlier spike hadn't
  exercised — `-> binding error: ...` (this scenario's actual failure) and `-> undefined: ...` (a
  deliberately-unbound step also present in the sample feature) — both now in the confirmed vocabulary
  table above. Rider's own test-runner captures and displays Reqnroll's default stdout trace exactly
  like `dotnet test` does — no Rider-specific signal needed, no `reqnroll.json` opt-in required.
- **Row-test naming, confirmed live.** The Test Runner tree showed a row-tests Outline's parameterized
  entry named `adding numbers(first: "50", second: "5", result: "1", __pickleIndex: "0", exampleTags: [])`
  — Rider identifies a specific row by a **formatted display name embedding the row's argument values**,
  not a positional index. This is live confirmation of the §3/§7 item 6 residual risk: targeting one
  specific Outline row via Rider's native runner would need to match on this formatted name (itself
  built from the row's arguments), not a stable index — reinforcing "run the whole parameterized method,
  not one row" as the safer default for Rider's row-tests Outline invocation, same conclusion reached
  for VS's `TestMethodIdentifier`.
- **Clicking the failed test navigates to the last step too** (Chris confirmed) — consistent with the
  stack-trace finding; this closes off "click-to-navigate" as a failed-step-mark mechanism for Rider the
  same way it was already closed off for VS/`ErrorStackTrace`.

**What's still genuinely unconfirmed**: the exact plugin-API shape for consuming this programmatically
— whether it's `SMTRunnerEventsListener`/`SMTestProxy` as hypothesized, and the specific method/field
that exposes the captured stdout (something like `SMTestProxy.getOutput()`/`printOn(Printer)` in
IntelliJ Platform's usual shape, not decompiled or verified this session). This is now a narrow,
low-risk "look up the exact API surface" task rather than an open feasibility question — the UI
evidence above proves the *data* is there and captured; only the *plugin-facing accessor* for it needs
confirming, and that's answerable by decompiling `intellij-community`'s `smRunner` module (a follow-up
task, not something blocking the design any further).

**Presentation target** (from the issue's own follow-up comment and the legacy Rider plugin's
confirmed source): a **gutter icon** at the failed step's line — not inline diagnostics, not
line-background highlighting — with a hover tooltip showing the failure output. Registered at
info/hint severity, not error severity, to stay visually low-noise against genuine diagnostics
([F3](LSP-IDE-Support-Feature-Designs.md#f3--gherkin-file-diagnostics)'s error/warning squiggles).

**What still needs a live session, concretely:**

1. ~~**Visual Studio**~~ Resolved (2026-08-27) — **pass/fail glyph implemented via reflection**, guarded
   so any future shape change degrades to "no glyph" rather than throwing (see §6 correction above,
   `RunTestOutcomeBridge`). Run/Debug delegation to Test Explorer (§5) is unaffected either way — it
   only ever needed the public `TestMethodIdentifier`/`CodeLensDetailPaneCommand` surface.
2. ~~**VS Code**~~ Resolved — Option 2 decided (own execution via `dotnet test --filter`, no
   `TestController`, no native Testing panel presence), after confirming via `vscode.d.ts` and VS Code's
   own command source that there's no way to delegate to C# Dev Kit's controller and read its result
   back. No open design question or live-testing need remains for VS Code.
3. ~~**Rider**~~ Resolved (2026-08-05) — Chris ran a real ambiguous-binding scenario through the
   devcontainer's Rider Test Runner and confirmed both the stack-trace misattribution and the stdout
   step-trace independently, on a different test host than the `dotnet test` spike. No open design
   question remains for Rider; only a narrow follow-up (confirming the exact `SMTRunnerEventsListener`/
   `SMTestProxy` plugin-API accessor for the already-proven-present data) is left, and it doesn't block
   the design.

**All three IDEs' test-result correlation designs are now closed out — §6 is fully resolved.**

---

## 7. Open items carried into implementation

1. ~~**Class-name generation rule**~~ **Resolved.** Confirmed by decompiling
   `UnitTestFeatureGenerator.GenerateUnitTestFixture`: `{feature.Name.ToIdentifier()}Feature`
   (`TestClassNameFormat = "{0}Feature"` by default). See §2.
2. ~~**§6 test-result channel**~~ **Resolved for all three IDEs.** A live `dotnet test` spike (throwaway
   Reqnroll+xUnit project, deliberately-failing scenario) settled the two biggest unknowns:
   `ErrorStackTrace`/`#line`-mapped frames do **not** reliably identify the failing step (they always
   attribute to the scenario's last step, a `#line hidden`-region artifact — corrects the original
   optimistic hypothesis); the real signal is Reqnroll's own default step-trace stdout (full seven-outcome
   vocabulary decompiled from `TestTracer`, §6), present with no `reqnroll.json` opt-in, and `dotnet test
   --filter "FullyQualifiedName=..."` precisely targets a resolved `ScenarioTestTarget`. Chris then
   independently confirmed both findings live in Rider via the devcontainer (real ambiguous-binding
   scenario, same misattribution, same stdout trace, plus the row-test formatted-display-name finding).
   VS Code's directly-shelled `dotnet test` (§5, Option 2) and Rider's Test Runner both consume this
   signal. VS's `ICodeLensTestInformationService`/`TestResultRecord.StandardOutput` path (2026-08-27,
   see §6 correction) is now consumed via a guarded reflection bridge (`RunTestOutcomeBridge`) rather
   than `TestResultRecord.StandardOutput`'s step-trace parsing — the outcome-only glyph doesn't need the
   failed-step detail that stdout parsing was originally for; a Rider-side plugin-API-accessor lookup
   (which exact `SMTestProxy` method exposes the already-proven-present stdout) remains open but doesn't
   block the design.
2a. ~~**VS Code `TestController` vs. own-execution design fork**~~ **Resolved — Option 2, own execution,
   no `TestController` (2026-08-05).** `vscode.tests` exposes no way to read another extension's
   controller results (confirmed against `vscode.d.ts` — an earlier guess that a `tests.testResults`
   snapshot API existed was wrong, corrected here), and the generic `testing.runAtCursor` delegation path
   requires real editor focus and still wouldn't solve the read problem. Chose to avoid duplicate Test
   Explorer entries against C# Dev Kit's own listing over gaining native Testing-panel presence — see §5.
3. ~~**VS editor-margin extension point**~~ **Resolved — there is no separate extension point.**
   Decompiling `Microsoft.VisualStudio.TestWindow.CodeLens.dll` shows VS's own run/debug/pass-fail
   affordance for ordinary tests (`TestStatusProvider`) is itself a classic CodeLens data-point
   provider — the exact API family F24 already bridged. See §5's rewritten Visual Studio bullet.
4. **Out of scope for now — relationship to Appendix B "Debug Support for Feature Files."** Reqnroll's
   `#line` pragmas mean the compiled test assembly's PDB already carries `.feature`-relative sequence
   points — the same mechanism Razor/T4 use for direct template debugging. This suggests breakpoint
   support may be a narrower "register `.feature` as a valid breakpoint source / path-map it to the
   native debugger" problem per IDE, not a from-scratch DAP implementation. **Explicitly descoped from
   this issue's implementation** (2026-08-05) — recorded here as a lead for whoever picks up the
   Appendix B item separately, not something #262 needs to spike or deliver. No dependency runs the
   other way: #262's run/debug/pass-fail/failed-step work stands on its own without this.
5. ~~**Per-framework row-attribute allowlist**~~ **Resolved** (§3 Tier 2, §2 table). All five
   providers' actual `SetRow` decompiled — `InlineDataAttribute` (xUnit/xUnit.v3), `TestCaseAttribute`
   (NUnit3), `ArgumentsAttribute` (TUnit), `DataRowAttribute` (MSTest, via `MsTestV2GeneratorProvider`/
   `MsTestV4GeneratorProvider` — **not** the `MsTestGeneratorProvider` base class, a first-pass mistake
   corrected in §2; that base class is superseded/unused and its `NotSupportedException` doesn't apply
   to real MSTest projects). All five frameworks support row tests. Still open: whether the project's
   active test framework — including MSTest's `TargetMsTestVersion`-driven provider split — is
   resolvable from existing project-reference detection (F2 binding discovery) or needs its own
   detection step here.
6. ~~**Row-level invocation addressing, per IDE**~~ **Resolved (falls back to "run all rows").** VS's
   `TestMethodIdentifier` (decompiled, §5) addresses a method, not a row. Rider's Test Runner
   (live-confirmed, §6) names a row by a formatted display string embedding its arguments, not a stable
   index. Neither gives reliable positional addressing, so the design commits to: a row-tests Outline's
   gutter targets the whole parameterized method by default; per-row addressing is not attempted in the
   initial implementation for any IDE (VS Code's own-execution design, §5, could add it later via
   `dotnet test --filter` with a `DisplayName`-based filter expression per row, since it owns the
   invocation — worth revisiting post-implementation, not blocking now).
7. **Multi-target "run all examples" invocation for individual-methods-mode Outlines, per IDE** (§5, new
   subsection). VS Code's own-execution design handles this trivially (OR'd `dotnet test --filter`
   expression). VS's multi-element `TestMethodIdentifier[]` `CommandArgs` is plausible but unconfirmed —
   only a single-target usage was observed in the decompiled `TestStatusProvider` source; fold into the
   same live VS check §6 already calls for. Rider's multi-target line-marker capability is unchecked
   entirely — a follow-up for Rider implementation, likely answerable by looking at how Rider's own
   built-in per-class "run all tests" line marker works. Neither blocks starting implementation, since
   row-tests mode (the framework default) doesn't need this at all.
8. ~~**AST-transforming generator plugins (e.g. `Reqnroll.ExternalData`)**~~ **Resolved (2026-08-05,
   flagged by Chris).** Confirmed against Reqnroll's actual source that such plugins (which inject
   `Examples:` rows into the AST after parsing but before codegen, so the `.feature` file can show zero
   rows or no `Examples:` block at all) don't require design changes — the resolver was already going
   to determine parameterization from the generated `.feature.cs`, never from the `.feature` file's own
   row count, and per-row addressing was already ruled out entirely (item 6), so this degrades safely to
   the existing "run the whole method"/"run all methods" fallback with no special-casing needed. See §2's
   dedicated subsection and §3 Tier 2's scope note. Worth a specs/unit-test fixture in §8 exercising a
   scenario with an `ExternalData`-style tag and zero visible rows, to lock this in as a regression case
   rather than leaving it as an untested inference.

---

## 8. Testing approach

- **Core unit** — `ScenarioTestTargetResolverTests`: feed hand-built `.feature.cs` fixtures (or a
  small generated corpus covering plain scenario / row-tests Outline / individual-methods Outline with
  named and unnamed `Examples:` blocks) and assert the resolved `ScenarioTestTarget[]`. Per
  [[core-tests-avoid-stubidescope]], build inputs directly rather than through `VsxStubs`.
- **Server unit** — `ResolveTestTargetsHandlerTests`: URI/range handling, absent-buffer and
  not-yet-built (no `.feature.cs` present) cases.
- **Acceptance (specs)** — a `.feature` spec exercising `reqnroll/resolveTestTargets` against a real
  generated fixture project (reusing the sample-project-generator infrastructure already used by F2
  discovery specs), covering all three Outline naming modes from §2, **plus a fixture using
  `Reqnroll.ExternalData`** (or a hand-rolled AST-injecting test double, to avoid an external-file
  dependency in the test project) with a plain `Scenario:` and zero visible `Examples:` rows, asserting
  the resolver still finds the generated row-tests method via Tier 1 rather than concluding "0 targets"
  from the `.feature` file's own row count — locks in §7 item 8 as a regression case, not just an
  untested inference.
- Per-IDE run/debug and result-correlation glue is IDE-side only and follows
  [[vs-extension-testing-strategy]] — native-runner invocation and result-event handling aren't
  practically unit-testable; verify live per IDE instead.
