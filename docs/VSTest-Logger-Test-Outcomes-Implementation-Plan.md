# Test Outcomes via an IDE-Bundled VSTest Logger — Implementation Plan

**Status: DRAFT, 2026-09-16.** Written after both feasibility spikes succeeded (see §1). This is an
implementation plan, not a design record: it says what to build, in what order, and what "done" means
for each step. It deliberately does **not** rewrite
[Test-Runner-Integration-Design.md](Test-Runner-Integration-Design.md); that document still describes
the as-shipped state (reflection bridge for VS, own-execution runners for Rider/VS Code) and will be
revised once this plan has landed. Where this plan contradicts it, this plan is the intent.

Related: [#702](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/702) (Scenario Outline glyphs never
update — the bug this retires), [#700](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/700) /
[PR #701](https://github.com/reqnroll/Reqnroll.IdeSupport/pull/701) (ordinary-scenario glyph fix on the
existing bridge — merges independently, not blocked by this), #450–#455 (pass/fail + Debug follow-ups
from #262).

---

## 1. What is already proven (don't re-spike)

Two spikes, both positive, both on 2026-09-16:

| Spike | Where | Proved |
|---|---|---|
| **vstest side** — hand-written `.runsettings` + throwaway logger in the Quickstart sample | Desktop hand-off note `VS-Test-Logger-DataCollector-Feasibility-Spike.md` + `VS-Test-Logger-Spike-artifacts\` | VS Test Explorer's design-mode run honours `<LoggerRunSettings>`; `<TestAdaptersPaths>` pointing **outside** the test project's `bin/` is honoured; `<Logger><Configuration>` children arrive as the `ITestLoggerWithParameters` parameter dictionary; a Scenario Outline yields **one `TestResult` per example row**; one netstandard2.0 DLL loads in both VS's net48 `vstest.console.exe` and the SDK's `vstest.console.dll`. |
| **IDE side** — branch `spike/vstest-logger-runsettings-injection` (`ce0852f5`, off `origin/main`) | `src/Core/Reqnroll.IdeSupport.TestLogger`, `src/VisualStudio/…VSSDKIntegration/TestLogger/`, 16 unit tests | A `[Export(typeof(IRunSettingsService))]` in the VSIX is composed by Test Explorer, invoked per request, and its merged runsettings survive chaining with VS's own additions and other adapters' services (Boost, GoogleTest). vstest loaded the logger from the **deployed extension directory** in the runner process (PID ≠ devenv), parameters intact. `TestCase.ManagedType`/`ManagedMethod` properties reach the logger — signature-bearing, row-invariant (`So23(System.String,…,System.String[])`). Design mode reuses one `vstest.console` across runs but re-`Initialize`s the logger per run. |

Consequences that shape everything below:

- **Nothing ships from the Reqnroll core repo.** The logger is IDE-bundled; users' test projects are untouched.
- **The logger is per-run, out-of-process, fire-and-forget.** It connects to the IDE when a run starts and goes away when it ends.
- **The aggregation key is `(source, ManagedType, ManagedMethod-without-signature)`**, not `FullyQualifiedName` (NUnit bakes `TestCase` arguments into row FQNs; `ManagedMethod` is signature-only on every adapter). Reqnroll never generates overloaded scenario methods in one feature class, so dropping the signature is safe and lets the key match `TestMethodIdentifier.MethodName` directly.
- **`AddRunSettings(Execution)` is called twice per Run click** by VS's `RunFromCodeLensOperation`. The merge is idempotent; anything stateful must not assume one call per run.

## 2. Goals and non-goals

**Goals**

1. Live pass/fail glyphs on the Run CodeLens for **every** scenario kind in VS — ordinary and outline — sourced from the vstest pipeline rather than VS's internal push. Closes #702.
2. Remove the dependency on reflection into `Microsoft.VisualStudio.TestWindow.Internal` for *reading* outcomes (`RunTestOutcomeBridge`). Run/Debug delegation (public `TestMethodIdentifier` + `CodeLensDetailPaneCommand`) is unaffected and stays.
3. One logger, one wire format, reusable by Rider and VS Code so all three IDEs observe results the same way.
4. Carry Reqnroll's step trace (`TestResult.Messages`, stdout category) alongside outcomes, so the failed-step mark designed in Test-Runner-Integration-Design §6 becomes possible in VS without a second channel.

**Non-goals (for this plan)**

- Native Test Explorer / Unit Tests tool-window presence in Rider or VS Code — settled elsewhere, unchanged.
- CLI-only (`dotnet test` from a terminal) runs updating IDE glyphs. The IDE hands the logger its endpoint per run; a terminal run has none. Same limitation as today.
- Microsoft.Testing.Platform (MTP) runs. See §8 — this is the reason the bridge is *retained as a fallback* rather than deleted in this plan.
- Rewriting Test-Runner-Integration-Design.md.

## 3. Architecture

```
 devenv.exe (in-proc)                                    vstest.console.exe (per run)
 ┌───────────────────────────────────────────────┐        ┌──────────────────────────────────┐
 │ ReqnrollTestLoggerRunSettingsService (MEF)    │        │ Reqnroll.IdeSupport.TestLogger   │
 │  - Execution requests only                    │ inject │  ITestLoggerWithParameters       │
 │  - Reqnroll containers only                   │───────▶│  loaded via TestAdaptersPaths    │
 │  - TestAdaptersPaths + LoggerRunSettings      │ per run│  params: Endpoint, Token, RunId  │
 │    (Endpoint, Token, RunId, IdeProcessId)     │        │                                  │
 ├───────────────────────────────────────────────┤        │  TestRunStart / TestResult /     │
 │ TestOutcomeListener (TCP loopback, token)     │◀───────│  TestRunComplete → NDJSON lines  │
 │  - accepts N runs, one connection per run     │ NDJSON │  fire-and-forget, never blocks   │
 ├───────────────────────────────────────────────┤        └──────────────────────────────────┘
 │ TestOutcomeStore                              │
 │  key (source, type, method) → rows + aggregate│
 │  → RunTestCodeLensRedirect.TaggerRegistry     │
 │      .InvalidateFile/All  (existing path)     │
 └───────────────┬───────────────────────────────┘
                 │ ICodeLensCallbackService (existing OOP→devenv RPC)
                 ▼
 CodeLens ServiceHub host (OOP)
 ┌───────────────────────────────────────────────┐
 │ RunTestCodeLensDataPoint.GetDataAsync         │
 │  outcome = callback GetOutcome(id)            │
 │            ?? RunTestOutcomeBridge (fallback) │
 └───────────────────────────────────────────────┘
```

Two existing mechanisms carry most of the weight, which is why the new code is small:

- **Invalidation** already flows devenv → OOP: `RunTestCodeLensRedirect.TaggerRegistry.InvalidateFile/InvalidateAll()` makes the in-proc tagger raise `TagsChanged`, CodeLens re-creates the data points, `GetDataAsync` runs again. The store just needs to call it when outcomes change. Use the **tagger-only** refresh, not `RunTestCodeLensRedirect.InvalidateAll()` — that one also drops the resolved-target cache and re-triggers the `resolveTestTargets` N+1 (#491).
- **OOP → devenv lookup** already exists: `RunTestCodeLensCallbackListener` (`ICodeLensCallbackListener`, `[ContentType("Gherkin")]`). Add a second `[JsonRpcMethod]` next to `GetTargetsForLine`.

## 4. Components and contracts

### 4.1 `Reqnroll.IdeSupport.TestLogger` (exists on the spike branch; grows a transport)

Constraints (already enforced by the csproj comments): netstandard2.0, assembly name ends in
`TestLogger`, only `Microsoft.TestPlatform.ObjectModel` referenced (compile-time), nothing from this
repo referenced. **No JSON library** — the runner may or may not have one we can bind to; the writer is
~40 lines of hand-rolled escaping for a flat object with string/number/bool fields.

**Parameters** (`<Logger><Configuration>` children → `Initialize(events, parameters)`):

| Key | Required | Meaning |
|---|---|---|
| `Endpoint` | yes* | `127.0.0.1:<port>` the IDE is listening on for this run |
| `Token` | yes* | per-run random secret; first line of the connection; IDE drops the connection if it doesn't match |
| `RunId` | no | correlation id echoed in every message and in the IDE's logs |
| `IdeProcessId` | no | diagnostic only |
| `LogFilePath` | no | spike-era file sink; keep as an optional secondary sink for troubleshooting (`REQNROLL_TESTLOGGER_FILE` env var also honoured) |

\* absent → the logger stays inert (no connection, no file) and returns; a hand-written runsettings
that names the logger without an endpoint must not break a run.

**Wire format**: newline-delimited JSON over one TCP connection, UTF-8, one object per line, IDE
sends nothing back. `protocol` is an integer the IDE checks; unknown fields are ignored on both sides.

```jsonc
{"type":"hello","protocol":1,"token":"…","runId":"…","runnerPid":17164,"idePid":14968,"targetFramework":".NETCoreApp,Version=v8.0"}
{"type":"runStart","runId":"…","testCount":2,"sources":["C:\\…\\ReqnrollQuickstart.Specs.dll"]}
{"type":"result","runId":"…","source":"C:\\…\\ReqnrollQuickstart.Specs.dll",
  "managedType":"ReqnrollQuickstart.Specs.Features.PriceCalculation2Feature",
  "managedMethod":"So23(System.String,System.String,System.String,System.String,System.String[])",
  "fqn":"ReqnrollQuickstart.Specs.Features.PriceCalculation2Feature.So23",
  "displayName":"so23(Electric guitar,1,180.0,2)",
  "outcome":"Passed",                       // Passed | Failed | Skipped | NotFound | None
  "durationMs":43.7,
  "errorMessage":null,"errorStackTrace":null,
  "stdout":"Given …\n-> done: …\n…"}        // TestResult.Messages, StandardOutCategory, capped (64 KB, truncated flag)
{"type":"runComplete","runId":"…","executed":2,"aborted":false,"canceled":false}
```

**Behaviour rules**

- Connect in `Initialize` with a short timeout (2 s). Failure → log once to the optional file sink, then
  become inert. A logger must never slow down or fail a test run.
- Writes are synchronous on the event thread with a bounded send buffer; on any socket error, stop
  sending for the rest of the run (no retries, no blocking).
- `stdout` is only included when the result carries messages; capped and flagged when truncated.
- Close the connection on `TestRunComplete`. The IDE treats connection close without `runComplete`
  as an aborted run.

### 4.2 Visual Studio

**`ReqnrollTestLoggerRunSettingsService`** (exists) — changes: emit `Endpoint`/`Token`/`RunId` from the
listener instead of `LogFilePath`; keep `IdeProcessId`. The listener is a MEF singleton it imports.
Add a kill switch (`REQNROLL_IDE_DISABLE_TEST_LOGGER=1` env var for now; an Options-page toggle can
follow) so a misbehaving logger can be turned off without uninstalling.

**`TestOutcomeListener`** (new, VSSDKIntegration, in-proc) — `TcpListener` on `127.0.0.1:0`, started
lazily on first `AddRunSettings(Execution)`, one accept loop, one `Token` per run handed out by
`RegisterRun()` and expired when that run completes or after a timeout (5 min) so the two-calls-per-Run
quirk (§1) just creates one unused token. Parses NDJSON with the JSON library the extension already has
(Newtonsoft.Json via `Reqnroll.IdeSupport.Common`), feeds `TestOutcomeStore`.

**`TestOutcomeStore`** (new, VSSDKIntegration, pure, unit-tested) —

- Key: `(normalizedSourcePath, managedType, methodName)` where `methodName` = `managedMethod` up to
  the first `(`. Path normalization must be the same routine as the rest of the extension uses for
  output-assembly comparison — #515 was an entire class of bugs caused by two different normalizations.
- Value: `MethodOutcome { Aggregate, IReadOnlyList<RowOutcome> Rows, LastUpdatedUtc, RunId }`;
  `RowOutcome { DisplayName, Outcome, ErrorMessage, ErrorStackTrace, Stdout, DurationMs }`.
- Row upsert by `DisplayName` within a method: a row-filtered run (one example) must not erase the
  other rows' last-known outcomes.
- Aggregate: `Failed` if any row failed; else `Passed` if any row passed; else `Skipped` if any row
  skipped; else `None`. (Mirrors VS's dominant-state rule closely enough; document any divergence
  found during live testing rather than reverse-engineering `GetDominantState` further.)
- Raises `Changed(IReadOnlyCollection<key>)` after each `result` batch (debounced ~250 ms) and on
  `runComplete`. The VS glue maps changed keys → open `.feature` URIs via the already-resolved
  targets (`RunTestCodeLensResultCache` knows `(uri, line) → RunTestTargetEntry`) and calls
  `RunTestCodeLensRedirect.TaggerRegistry.InvalidateFile(uri)`; if the mapping is unavailable, fall back
  to `TaggerRegistry.InvalidateAll()`.

**`RunTestCodeLensCallbackListener`** (exists) — add
`[JsonRpcMethod("Reqnroll.RunTestCodeLens.GetOutcome")] Task<RunTestOutcomeEntry?> GetOutcomeAsync(string assemblyPath, string typeFullName, string methodName, CancellationToken ct)`
returning a small serializable DTO (`Outcome` as string + row count + failed row display names) —
same shape discipline as `RunTestTargetEntry`.

**`RunTestCodeLensDataPoint`** (exists, OOP) — `GetDataAsync` asks the callback first; only if it
returns null does it consult `RunTestOutcomeBridge.TryGetOutcomeAsync` (fallback, §8). The
`ToImageId` mapping stays as is. `GetDetailsAsync` can later show the per-row table from the DTO.

**Persistence** (Phase 3) — VS's `TestStore` remembers outcomes across sessions; ours starts empty.
Serialize the store to `%LOCALAPPDATA%\Reqnroll\test-outcomes\<solution-key>.json` on `runComplete`,
load on solution open, discard entries whose source assembly is newer than the stored `LastUpdatedUtc`
(stale after a rebuild — arguably better than VS, which shows a stale green for rebuilt code).

### 4.3 Rider and VS Code (adopt, don't redesign)

Both IDEs own their execution via `dotnet test --filter` (Rider today parses a TRX; VS Code has no
in-tree runner yet — nothing under `src/VSCode/src` invokes `dotnet test`). Adoption means adding two
arguments to the command line they already build and listening on a loopback socket:

```
dotnet test <proj> --filter "<expr>" \
  --test-adapter-path "<plugin dir>/testlogger" \
  --logger "ReqnrollIde;Endpoint=127.0.0.1:<port>;Token=<token>;RunId=<id>"
```

What it buys them over TRX/stdout scraping: per-row outcomes for outlines, structured error text,
and the step trace, as results arrive rather than after the process exits. The logger DLL ships in
the plugin/extension package the same way the LSP server already does. The JVM side is why the
transport is TCP loopback rather than a named pipe (§5.1). Rider's `RunTestResultStore` (keyed
`(uri, line)`) and VS's `TestOutcomeStore` (keyed by method) should converge on the method key; the
uri/line mapping is the same `resolveTestTargets` data on every IDE.

## 5. Decisions

### 5.1 Transport: TCP loopback + per-run token (not a named pipe, not files)

Named pipes are natural for VS↔.NET, but the JVM has no first-class Windows named-pipe client, and
one transport for all three IDEs is worth more than idiomatic-per-platform. Loopback listeners on
`127.0.0.1` do not trigger Windows Firewall prompts. The token makes "any local process can connect"
into "any local process that has read this run's runsettings can connect" — that document is visible
in VS's Tests output pane at Diagnostic level, which is acceptable for a per-run, minutes-lived
secret whose only power is to post fake outcomes to one IDE instance. File-based (the spike's sink)
stays as an optional troubleshooting mirror only.

### 5.2 Format: NDJSON, hand-serialized in the logger

Flat objects, a handful of fields, no nesting beyond arrays of strings. Hand-rolling the writer is
smaller than any dependency story inside the runner process. The IDE side uses whatever JSON library
it already has. `protocol` is versioned from day one.

### 5.3 The reflection bridge stays as a **fallback** (for now)

`RunTestOutcomeBridge` is not deleted in this plan. Two reasons: (a) MTP-mode test projects bypass
VSTest loggers entirely (§8), and VS's `TestStore` is the only outcome source for them; (b) until
Phase 3 persistence lands, the bridge is what shows last-session outcomes after a restart. The lookup
order is store → bridge. Removing the bridge is a separate decision after Phase 3, informed by how
many Reqnroll users are on MTP.

PR #701's subscription code (VS's test-outcome-changed push) becomes redundant once the store drives
invalidation, but it is harmless and should merge on its own merits first; it is removed together with
the bridge, not before.

### 5.4 No Reqnroll core repo involvement

Re-stated because the first draft of the hand-off note got it wrong: the logger is discovered by file
name from a directory the IDE names in runsettings. There is no NuGet package, no project dependency,
no generator change.

### 5.5 Reqnroll-container gating stays

`Reqnroll.dll` beside the container's output is the cheapest reliable signal. Mixed runs (Reqnroll +
non-Reqnroll containers) still get the logger; the store simply never finds a target for the foreign
results.

## 6. Phases

Each phase is one PR-sized unit with its own exit criteria. Phases 1–3 are VS; 4–5 are the other IDEs;
6 is the retirement decision.

### Phase 0 — Land the spike as a feature branch

- Rename `spike/vstest-logger-runsettings-injection` → `feature/702-vstest-logger` (or branch from it).
- Open the tracking issue ("Observe test outcomes via an IDE-bundled VSTest logger") linking this plan,
  the hand-off note and #702; comment on #702 pointing at it. Don't close #702 until Phase 2 is live.
- One live run with a user-selected runsettings file (`spike.runsettings` from the artifacts folder)
  to close the last "unit-tested only" item — record the merged XML from the ext log in the issue.
- **Exit:** issue open, branch green in CI (the new netstandard2.0 project builds on Linux; CI's
  `UseExternalLspServerBuild` path is unaffected by the new unconditional `ProjectReference`).

### Phase 1 — Transport and store (VS glyph for every scenario kind)

Files: `TestLogger/NdjsonWriter.cs`, `TestLogger/OutcomeTransport.cs` (logger);
`VSSDKIntegration/TestLogger/TestOutcomeListener.cs`, `TestOutcomeStore.cs`, `RunTestOutcomeEntry.cs`;
edits to `ReqnrollTestLoggerRunSettingsService`, `RunTestCodeLensCallbackListener`,
`RunTestCodeLensDataPoint`, `ReqnrollLanguageClient` (wire store → `TaggerRegistry` invalidation).

Tests: NDJSON writer round-trip (write with the logger's serializer, parse with Newtonsoft);
`TestOutcomeStore` aggregation/upsert/normalization; listener token handling (bad token, second
connection, close-without-complete); `TestOutcomeListener`+logger **end-to-end over a real socket**
without VS: an xUnit test that spawns `dotnet test` on a tiny fixture project with
`--test-adapter-path`/`--logger` pointing at an in-test listener and asserts the received messages
(a fixture project similar to `tests/LSP/…Specs.BindingsFixture`). This is the test that would have
caught #702 in CI.

- **Exit:** in the experimental instance, Run on an ordinary scenario and on an outline both update the
  glyph within ~2 s of run completion without touching `RunTestOutcomeBridge` (verify via the ext log:
  `GetOutcome` hit, bridge not consulted). Debug run from Test Explorer behaves the same. Kill switch
  verified. #702's repro (renamed outline) passes.

### Phase 2 — Per-row details and the failed-step signal

- `GetDetailsAsync` renders the row table (display name, outcome, duration) from the DTO — the
  "which example failed" answer that VS's own lens gives for `.feature.cs`.
- Parse `stdout` with the seven-outcome step-trace vocabulary already documented in
  Test-Runner-Integration-Design §6 into per-step outcomes; store them on `RowOutcome`. No UI yet
  beyond the details pane — the gutter mark is its own follow-up (#262's remaining scope), but the data
  is now in the IDE, which was the blocker.
- **Exit:** a deliberately failing middle step shows as `error` on the right step in the details pane;
  `binding error`/`undefined` outcomes parse.

### Phase 3 — Persistence, freshness, and run-in-progress

- Store serialization per solution (§4.2), invalidated by source-assembly timestamp.
- `runStart` marks the listed test cases as running so the lens can show an in-progress state
  (`KnownMonikers.StatusRunning` or no glyph — decide live; VS's own lens shows a spinner).
- Live check: two VS instances open on different solutions run tests simultaneously without
  cross-talk (per-instance listener + token).
- **Exit:** restart VS → last-run glyphs present; rebuild → glyphs cleared; concurrent-instance check
  passes.

### Phase 4 — Rider adoption

- Bundle the logger in the plugin (same Gradle path that bundles the LSP server), add a
  `ServerSocket(0, …, loopback)` listener in `testrunner/`, pass `--test-adapter-path`/`--logger`,
  key `RunTestResultStore` by method, keep the TRX path as the fallback for one release.
- Verify in the devcontainer (`./gradlew test --offline` + a live run — see the memory on
  devcontainer verification; the host cannot build Gradle).
- **Exit:** outline rows report individually in Rider's Run lens; TRX parsing no longer on the hot path.

### Phase 5 — VS Code adoption

- Depends on VS Code having an own-execution runner at all (none in-tree today). When it does, same
  two arguments, `net.createServer` on loopback, the same store shape in TypeScript.

### Phase 6 — Retirement decision for `RunTestOutcomeBridge`

- Inputs: MTP prevalence among Reqnroll users; whether Phase 3 persistence made the bridge's
  cross-session memory redundant; any VS servicing breakage seen in the meantime.
- Output: either delete the bridge + PR #701's subscription code, or keep the bridge MTP-only behind
  a container check. Either way, update Test-Runner-Integration-Design §6 then.

## 7. Testing strategy

| Layer | How | Where |
|---|---|---|
| Logger serialization + transport | xUnit, in-proc listener, subclass `TestLoggerEvents` to raise events directly | new `tests/Core/Reqnroll.IdeSupport.TestLogger.Tests` |
| Logger end-to-end (no IDE) | spawn `dotnet test` on a fixture with `--test-adapter-path`/`--logger`, assert NDJSON received | same project, `[Trait("Category","Integration")]` |
| Runsettings merge | existing 16 tests, plus a case where the user's file already lists a *different* `*TestLogger.dll` directory | `Reqnroll.IdeSupport.VisualStudio.Tests/TestLogger` |
| Store | pure unit tests: aggregation, row upsert, path normalization, debounce | same |
| Listener | token accept/reject, half-open connection, concurrent runs | same |
| CodeLens wiring | interface-shaped glue; substitute the callback and assert store-first/bridge-fallback order | existing `RunTestCodeLens` test folder |
| VS live | the recipe in the hand-off note, extended with Debug, user runsettings, two instances | manual, recorded in the issue |
| Rider | devcontainer live run | manual |

Spec-suite (`.feature`) coverage is not the right layer here — nothing crosses the LSP wire.

## 8. Risks and open items

| # | Item | Impact | Plan |
|---|---|---|---|
| 1 | **Microsoft.Testing.Platform projects** (`HasTestingPlatformServerCapability: True`) don't use VSTest loggers; VS runs them in "testing platform server mode". A Reqnroll project on MSTest 3.x with `EnableMSTestRunner`, TUnit, or xunit.v3 in MTP mode gets no logger. | Glyphs fall back to the bridge for those projects; the bridge's outline blindness (#702) remains for them. | Detect per container (the capability is visible to VS; whether `ITestContainer` exposes it needs a look) and log it. MTP has its own extension model registered *in the test project* — that would be a core-repo conversation and is explicitly out of scope here. Drives §5.3. |
| 2 | **Path normalization** between `TestCase.Source` and `ScenarioTestTarget.OutputAssemblyPath` | Silent "no outcome" for a project | Reuse the extension's single normalization helper; unit-test the store with mixed case / trailing separators / `8.3` forms. #515 is the precedent. |
| 3 | `AddRunSettings(Execution)` called twice per Run | Two tokens minted per run | Tokens are cheap and expire; never key state on "one call = one run". |
| 4 | A user's own runsettings lists a `TestAdaptersPaths` directory that contains an older `Reqnroll.IdeSupport.TestLogger.dll` (e.g. a copied extension folder) | Two loggers with the same friendly name; vstest picks one | Version the `ExtensionUri`? No — vstest resolves by friendly name. Log the full `TestAdaptersPaths` at Info; document it. Low likelihood. |
| 5 | Result batching: vstest batches testhost→runner results (`BatchSize` default 10 + timer) | Glyph latency up to ~1–2 s on long suites | Acceptable; `BatchSize` is ours to set in the injected runsettings if it isn't. |
| 6 | `Microsoft.VisualStudio.TestWindow.Interfaces` resolves to whatever VS is installed at build time (18.0.0.0 on the dev machine) while the VSIX targets 17.14+ | Same class of caveat the existing `TestWindow.Internal` reference already carries | Verify once on a 17.14 install or CI image before Phase 1 ships; VS ships binding redirects for its own assemblies, which is why the existing reference works. |
| 7 | NUnit / xUnit `ManagedMethod` shapes for outline rows not yet observed live | Wrong key → no glyph on those frameworks | Add NUnit and xUnit fixtures to the Phase 1 end-to-end test; it is exactly the property that test asserts. |
| 8 | Loopback listener flagged by endpoint-protection software | Logger can't connect; glyphs silently stale | Logger and listener both log the failure; Tests output pane gets a one-line warning on `runComplete`-without-connection detection. Named-pipe transport can be added behind the same `Endpoint` parameter later if this turns out to be real. |
| 9 | Token visible in VS's Diagnostic-level Tests output | Local disclosure of a short-lived secret | Accepted (§5.1). Never log it in our own logs. |

## 9. Issue / PR breakdown

1. Tracking issue (Phase 0) — links this plan; subtasks below as checkboxes.
2. PR: Phase 0 (spike branch as-is + CI green + user-runsettings live evidence).
3. PR: Phase 1 — logger transport + listener + store + CodeLens wiring + end-to-end test. Closes #702 on merge + live confirmation.
4. PR: Phase 2 — details pane + step-trace parsing.
5. PR: Phase 3 — persistence + in-progress + multi-instance check.
6. Issue each for Phase 4 (Rider) and Phase 5 (VS Code); Phase 6 is a decision recorded on the tracking issue.
7. Docs: revise Test-Runner-Integration-Design §6 after Phase 6, not before.
