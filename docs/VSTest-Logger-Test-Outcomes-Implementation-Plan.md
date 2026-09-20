# Test Outcomes via an IDE-Bundled VSTest Logger — Implementation Plan

**Status: Phases 0–3 IMPLEMENTED on branch `fix/700-vstest-logger-runsettings-injection` (2026-09-16),
live verification of the exit criteria pending.** Written after both feasibility spikes succeeded (see §1).

**Revision (2026-09-18): the per-run token described throughout this plan has been removed.** Live
VS Code testing surfaced a real design flaw: the server enforced the token as single-use, but VS Code
mints one registration per extension activation (there is no "a run is about to start" hook to rotate
a token against) and can see several test-host connections against that one registration in a
session — every connection after the first was rejected as "unknown or expired token". The token never
protected anything a loopback bind doesn't already (it travels to the runner inside the very
runsettings file that names the listener's port), so removing it fixes the VS Code bug and simplifies
every IDE's client-side code without changing the actual trust boundary. Every `Token`/`token` mention
below is historical; the as-built contract is `Endpoint` + `RunId` only.
This is an implementation plan, not a design record: it says what to build, in what order, and what
"done" means for each step; §6 records what each phase actually became. It deliberately does **not** rewrite
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
 │  - TestAdaptersPaths + LoggerRunSettings      │ per run│  params: Endpoint, RunId         │
 │    (Endpoint, RunId, IdeProcessId)            │        │                                  │
 ├───────────────────────────────────────────────┤        │  TestRunStart / TestResult /     │
 │ TestOutcomeListener (TCP loopback)             │◀───────│  TestRunComplete → NDJSON lines  │
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
| `RunId` | no | correlation id echoed in every message and in the IDE's logs |
| `IdeProcessId` | no | diagnostic only |
| `LogFilePath` | no | spike-era file sink; keep as an optional secondary sink for troubleshooting (`REQNROLL_TESTLOGGER_FILE` env var also honoured) |

\* absent → the logger stays inert (no connection, no file) and returns; a hand-written runsettings
that names the logger without an endpoint must not break a run.

**Wire format**: newline-delimited JSON over one TCP connection, UTF-8, one object per line, IDE
sends nothing back. `protocol` is an integer the IDE checks; unknown fields are ignored on both sides.

```jsonc
{"type":"hello","protocol":1,"runId":"…","runnerPid":17164,"idePid":14968,"targetFramework":".NETCoreApp,Version=v8.0"}
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

**`ReqnrollTestLoggerRunSettingsService`** (exists) — changes: emit `Endpoint`/`RunId` from the
listener instead of `LogFilePath`; keep `IdeProcessId`. The listener is a MEF singleton it imports.
Add a kill switch (`REQNROLL_IDE_DISABLE_TEST_LOGGER=1` env var for now; an Options-page toggle can
follow) so a misbehaving logger can be turned off without uninstalling.

**`TestOutcomeListener`** (new, VSSDKIntegration, in-proc) — `TcpListener` on `127.0.0.1:0`, started
lazily on first `AddRunSettings(Execution)`, one accept loop, one fresh `RunId` per `RegisterRun()`
call so the two-calls-per-Run quirk (§1) just mints one unused id. No per-connection secret — see §5.1
for why. Parses NDJSON with the JSON library the extension already has (Newtonsoft.Json via
`Reqnroll.IdeSupport.Common`), feeds `TestOutcomeStore`.

> **As built (2026-09-20):** `RunId` is a *correlation* id only. The store's running marks are keyed by a
> per-connection id the listener mints on `hello` (`<runId>/<seq>`), because a runId is not unique per
> connection in practice: VS Code registers once per session and bakes that id into a static runsettings
> file, and vstest.console in design mode Initializes the logger 2–3 times per Run click, only one of
> which ever sends `runStart`. Keying by runId let an idle instance's late socket drop clear the real
> run's running marks (seen in the 2026-09-20 VS Code log). Those idle drops are now logged at Verbose,
> not as "treated as aborted"; `runComplete` clears the marks immediately rather than at socket close.

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

### 4.3 Rider and VS Code (reconsidered — see Phase 3.5)

Superseded in its original form (below, kept for the record) by the LSP-server outcome pipeline
refactor (Phase 3.5): the receiver, `TestOutcomeStore`, and persistence that Phase 1–3 built as a
Visual Studio-only, in-proc pipeline now live once in the LSP server, shared by every IDE, rather
than VS/Rider/VS Code each growing an independent copy. Two things forced this reconsideration
before Rider/VS Code work started:

- **Registration feasibility differs per IDE, and isn't symmetric with VS's original design.** Rider
  already owns its `dotnet test --filter` invocation (`RunTestRunner.kt`), so injecting the two
  extra arguments is straightforward — no different in kind from what VS's `IRunSettingsService`
  hook already does. VS Code does **not** own test execution at all: C# Dev Kit provides the gutter
  run/debug and Test Explorer integration (a CodeLens-based "▶ Run" and a `vscode.TestController`
  migration were both tried and reverted — issue #504), so "VS Code has no in-tree runner yet" (the
  original wording above) was misleading — it reads as "not built yet" when it is actually a
  deliberate, already-settled position. The real Phase 5 question is whether the logger can be
  injected into C# Dev Kit's *own* `dotnet test`/vstest invocation (a runsettings-path setting it
  reads, or a file it auto-discovers) — unconfirmed, and a materially different, harder question
  than Rider's. Not yet spiked; Phase 5 is blocked on it.
- **One implementation, not three.** Once server-side registration was on the table for Rider/VS
  Code, keeping VS's copy of the store/persistence/receiver as a fourth, VS-only implementation
  stopped making sense. VS already runs an LSP client alongside its VSSDK extension, and other VS
  CodeLens providers (step-usage counts, hook-match counts) already flow through the shared LSP
  server rather than an in-proc VS-only path — the Run CodeLens outcome pipeline was the one
  exception. Phase 3.5 removes that exception.

Original text, superseded: both IDEs own their execution via `dotnet test --filter` (Rider today
parses a TRX; VS Code has no in-tree runner yet — nothing under `src/VSCode/src` invokes
`dotnet test`). Adoption means adding two arguments to the command line they already build and
listening on a loopback socket:

```
dotnet test <proj> --filter "<expr>" \
  --test-adapter-path "<plugin dir>/testlogger" \
  --logger "ReqnrollIde;Endpoint=127.0.0.1:<port>;RunId=<id>"
```

What it buys them over TRX/stdout scraping: per-row outcomes for outlines, structured error text,
and the step trace, as results arrive rather than after the process exits. The logger DLL ships in
the plugin/extension package the same way the LSP server already does. The JVM side is why the
transport is TCP loopback rather than a named pipe (§5.1). Rider's `RunTestResultStore` (keyed
`(uri, line)`) and VS's `TestOutcomeStore` (keyed by method) should converge on the method key; the
uri/line mapping is the same `resolveTestTargets` data on every IDE. The registration/store split is
now Phase 3.5's job instead of each IDE's own; the uri/line convergence point stands as written.

## 5. Decisions

### 5.1 Transport: TCP loopback, no per-connection secret (not a named pipe, not files)

Named pipes are natural for VS↔.NET, but the JVM has no first-class Windows named-pipe client, and
one transport for all three IDEs is worth more than idiomatic-per-platform. Loopback listeners on
`127.0.0.1` do not trigger Windows Firewall prompts. File-based (the spike's sink) stays as an
optional troubleshooting mirror only.

**No token.** An earlier revision minted a per-run random token and rejected any `hello` that didn't
present it, on the theory that this turned "any local process can connect" into "any local process
that has read this run's runsettings can connect". In practice those were never different bars — the
token travels to the runner process inside the very runsettings file that names the listener's port,
so reading one means reading the other — and treating the token as single-use broke VS Code, which
registers once per extension activation (no "a run is about to start" hook exists to rotate a token
against) and can legitimately see several test-host connections against that one registration. Removed
entirely (2026-09-18): the listener accepts any `hello` on its loopback port. The threat model is
unchanged in practice — "any local process can post fake outcomes to one IDE instance" — just stated
honestly instead of behind a token that didn't add a real barrier.

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

### Phase 0 — Land the spike as a feature branch — DONE (branch), issue pending

- ~~Rename `spike/vstest-logger-runsettings-injection` → `feature/702-vstest-logger`~~ Renamed to
  `fix/700-vstest-logger-runsettings-injection` (Chris's choice: this branch closes #700 as well as #702).
- Open the tracking issue ("Observe test outcomes via an IDE-bundled VSTest logger") linking this plan,
  the hand-off note and #702; comment on #702 pointing at it. Don't close #702 until Phase 2 is live.
- One live run with a user-selected runsettings file (`spike.runsettings` from the artifacts folder)
  to close the last "unit-tested only" item — record the merged XML from the ext log in the issue.
- **Exit:** issue open, branch green in CI (the new netstandard2.0 project builds on Linux; CI's
  `UseExternalLspServerBuild` path is unaffected by the new unconditional `ProjectReference`).

### Phase 1 — Transport and store (VS glyph for every scenario kind) — IMPLEMENTED (`2c51592d`)

As built, two deviations from the sketch below worth knowing: the file sink survived as an optional
troubleshooting mirror (`REQNROLL_IDE_TEST_LOGGER_MIRROR=1|<path>` on devenv, or `LogFilePath` /
`REQNROLL_TESTLOGGER_FILE` on the logger), and invalidation uses a **descriptor revision** rather than a
per-file mapping: `RunTestCodeLensRedirect.OutcomeRevision` is folded into every Run lens tag's
`ElementDescription`, so `NotifyOutcomesChanged()` (bump + tagger-only refresh) yields new descriptors and
the CodeLens host re-creates the OOP data points. The tagger now `Disconnect`s a replaced tag on the same
line. The end-to-end `dotnet test` test lives in `tests/Core/Reqnroll.IdeSupport.TestLogger.Tests`
against `tests/Core/TestLoggerFixtures/MsTestReqnroll` (net10.0 so CI's single runtime suffices) and
runs in `test-lsp.yml`'s unit-test job. **Live exit criteria below still to be run.**

Files (as originally built — see Phase 3.5 for where the receiver/store/persistence actually live
now): `TestLogger/NdjsonWriter.cs`, `TestLogger/OutcomeTransport.cs` (logger);
`VSSDKIntegration/TestLogger/TestOutcomeListener.cs`, `TestOutcomeStore.cs`, `RunTestOutcomeEntry.cs`
(the first two relocated to the LSP server in Phase 3.5; `RunTestOutcomeEntry.cs` — the OOP wire DTO —
stays);
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

### Phase 2 — Per-row details and the failed-step signal — IMPLEMENTED (`1f045fce`)

`Common/TestOutcomes/StepTraceParser` (17 tests incl. the fixture's real captured stdout);
`RowOutcome.Steps`/`FailedStep`; `RunTestOutcomeRow.StepCount/FailedStepIndex/FailedStepText/FailedStepOutcome`;
details table Example / Outcome / Duration / Failed step. **Live exit criterion (deliberately failing
middle step shows on the right step in the pane) still to be run.**

- `GetDetailsAsync` renders the row table (display name, outcome, duration) from the DTO — the
  "which example failed" answer that VS's own lens gives for `.feature.cs`.
- Parse `stdout` with the seven-outcome step-trace vocabulary already documented in
  Test-Runner-Integration-Design §6 into per-step outcomes; store them on `RowOutcome`. No UI yet
  beyond the details pane — the gutter mark is its own follow-up (#262's remaining scope), but the data
  is now in the IDE, which was the blocker.
- **Exit:** a deliberately failing middle step shows as `error` on the right step in the details pane;
  `binding error`/`undefined` outcomes parse.

### Phase 3 — Persistence, freshness, and run-in-progress — IMPLEMENTED (see §4.2 "as built")

Single global file instead of per-solution files (rationale in §4.2). **Live exit criteria (restart →
glyphs present; rebuild → glyphs cleared; two instances → no cross-talk) still to be run.**

- Store serialization per solution (§4.2), invalidated by source-assembly timestamp.
- `runStart` marks the listed test cases as running so the lens can show an in-progress state
  (`KnownMonikers.StatusRunning` or no glyph — decide live; VS's own lens shows a spinner).
- Live check: two VS instances open on different solutions run tests simultaneously without
  cross-talk (per-instance listener + token).
- **Exit:** restart VS → last-run glyphs present; rebuild → glyphs cleared; concurrent-instance check
  passes.

### Phase 3.5 — LSP-server outcome pipeline refactor — IMPLEMENTED

Moves the receiver, `TestOutcomeStore`, and persistence out of the Visual Studio-only
VSSDKIntegration project and into the shared LSP server (`src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/TestOutcomes/`),
so Rider and VS Code (once Phase 4/5 land) share the exact same aggregation/persistence code VS uses,
instead of each growing an independent copy — see §4.3 for why this was reconsidered before Rider/VS
Code work started.

- **New custom LSP protocol** (`LspMethodNames`): `reqnroll/testOutcomes/registerRun` (request —
  returns the listener's endpoint and a fresh run id to correlate with; no per-connection secret, see
  §5.1), `reqnroll/testOutcomes/getOutcome` (request — the outcome lookup, including the
  staleness/trust-window logic that used to live in `RunTestCodeLensCallbackListener`), and
  `reqnroll/testOutcomes/changed` (server→client push, mirroring `reqnroll/refreshCodeLens`). The
  server also advertises the feature via the standard `experimental` capability bucket in the
  `initialize` response — the spec's sanctioned extension point for exactly this.
- **VS integration**: `TestOutcomeListener`/`TestOutcomeStore`/`TestOutcomePersistence`/
  `TestOutcomeFreshness` deleted from VSSDKIntegration; `RunTestCodeLensCallbackListener.GetOutcomeAsync`
  now calls through `RunTestCodeLensRedirect.GetTestOutcomeAsync` (a new VS-Extension-set delegate,
  same pattern as the existing `GetTargetsForLineAsync`) instead of an in-proc store read.
  `ReqnrollTestLoggerRunSettingsService.AddRunSettings` — a **synchronous** VS Test Platform callback
  with no async overload — now blocks (bounded by a 5 s timeout, via `ThreadHelper.JoinableTaskFactory.Run`,
  the same sync-over-async bridge used elsewhere in this codebase) on the LSP round trip to register a
  run, instead of a same-process call. This was a deliberate, explicitly-decided trade-off (not a
  default choice): the alternative of a pre-fetched pool of endpoint/run-id pairs avoids the blocking
  call entirely at the cost of pool/race-handling complexity; blocking-with-a-bound was chosen for a
  simpler pipeline, accepting a few extra milliseconds of run-configuration latency and graceful
  ("inject nothing") degradation if the server is slow or unreachable.
- **Client-side push**: a new `TestOutcomesChangedInterceptor` (mirrors `CodeLensRefreshInterceptor`,
  but with no debounce/rate-limit of its own — the server already throttles this notification, and
  unlike the CodeLens refresh push this one never touches VS.Extensibility's `CodeLens.Invalidate()`,
  so it carries none of issue #156's reconnect risk) forwards the push to
  `RunTestCodeLensRedirect.NotifyOutcomesChanged()`, unchanged from Phase 1.
- Tests moved with the code: `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/TestOutcomes/`
  now covers `TestOutcomeStore`, `TestOutcomeFreshness`/`GetTestOutcomeHandler` (including the
  aged-out-vs-stale distinction from the earlier code-review fix pass), `TestOutcomeTcpListener`, and
  `TestOutcomePersistence`; VS-side `TestOutcomeDetailsTests` was trimmed to only the
  `RunTestCodeLensDataPoint` rendering tests, which stayed VS-side.
- **Not done here**: Rider/VS Code do not yet call the new requests (that's Phase 4/5, and Phase 5 is
  still blocked on the C# Dev Kit injection-point question above). VS's own registration/lookup calls
  are live end to end against the server.
- **Exit:** 1081/1082 LSP server tests, 396/396 VS tests, 17/17 logger tests, 182/182 Common tests
  green (the one LSP server failure is a pre-existing, unrelated performance-threshold test that is
  flaky under machine load and passes in isolation); full solution builds clean. **Live verification
  in VS — glyphs still update end to end now that registration and lookup are cross-process — not yet
  run; the same exit criteria as Phase 1/3 apply.**

### Phase 4 — Rider adoption — IMPLEMENTED (unit-tested; live devcontainer run not yet done)

- Bundled the logger via a new `publishTestLogger` Gradle task mirroring `publishServer`'s
  `-PlspServerBuildDir`-style external-build-dir mechanism (`-PlspTestLoggerBuildDir`), but with no
  per-RID loop: the logger targets netstandard2.0 with no self-contained runtime, so one
  framework-dependent publish serves every OS. `ReqnrollTestLoggerPathResolver` (mirrors
  `ReqnrollServerPathResolver`, minus the RID logic) resolves it under the plugin's own
  `testlogger/` directory.
- `RunTestRunner.run` now calls `reqnroll/testOutcomes/registerRun` before shelling to
  `dotnet test`; on success it adds `--test-adapter-path`/`--logger "ReqnrollIde;..."` to the
  *same* invocation that already asks for a TRX logger (vstest allows multiple `--logger` flags in
  one run), rather than replacing it. After the process exits it polls
  `reqnroll/testOutcomes/getOutcome` per distinct target method (bounded: 10 attempts × 100ms,
  since the logger's final `runComplete` write and the server processing it are not guaranteed to
  have landed the instant the runner process itself terminates) and combines the results with the
  same `Failed` > `Passed` precedence the server's own `TestOutcomeStore.Aggregate` uses. Any
  failure at any step (no server running, logger not bundled, nothing found after polling) falls
  back to the pre-#700 TRX-parsed result unchanged — the fallback this phase was scoped to keep.
- **Convergence point found, not assumed:** the server's `TestOutcomeKey.Source` needs the
  *compiled* container path, not the `.csproj` path `RunTestRunner` already had. Confirmed via
  `javap` against the actual bundled Rider 2024.3.5 classes (`RunnableProject.projectOutputs` →
  `ProjectOutput.exePath`) rather than assumed — Rider's RD protocol model has no
  test-project-specific "output assembly" field, so this reuses the same field the platform's own
  run infrastructure uses for a project's built artifact generally.
- Per-row detail (Scenario Outline rows, failed-step text) that TRX scraping never captured now
  populates `RunResult.rows`; the Run lens tooltip lists failed rows with their failing step when
  the server-sourced path is live, falling back to the plain title for a TRX-sourced result.
- **Verified:** `./gradlew compileKotlin compileTestKotlin test --offline` in the devcontainer —
  193 tests, 0 failures (was ~163 before this phase; new coverage in `RunTestRunnerTest`/
  `RunLensSupportTest`). **Not verified:** an actual live run (the devcontainer has no .NET SDK to
  publish the server/logger or run `dotnet test`, and there is no Windows-host Rider install to run
  outside it — see the devcontainer-verification memory) — outline rows reporting individually in
  Rider's live Run lens is the live exit criterion still to be run, same status as VS's own
  still-pending live checks.

### Phase 5 — VS Code adoption — plumbing and UI surface IMPLEMENTED

The C# Dev Kit injection-point question from §4.3 is answered, confirmed against the actual
`dotnet/vscode-csharp` source (the open-source extension C# Dev Kit builds its testing UI on) —
not assumed: `ms-dotnettools.csharp` contributes a genuine `dotnet.unitTests.runSettingsPath`
setting ("Path to the .runsettings file which should be used when running unit tests"), an
ordinary shared VS Code configuration key any extension can read and write via
`vscode.workspace.getConfiguration('dotnet')`. No C# Dev Kit-specific API, no cross-extension
`vscode.tests` access needed — the bundled logger reports to our own LSP server over the loopback
socket, exactly like VS/Rider, entirely independent of whatever C# Dev Kit's own Test Explorer UI
shows. This also means the earlier framing of this phase as "possibly not achievable without
reopening #504" was too pessimistic — it's achievable without touching #504's decision at all,
since nothing here adds a competing Run/Debug affordance or a `TestController`.

**Built (`src/VSCode/src/testOutcomes/`):** `publishTestLogger`-equivalent bundling
(`scripts/publish-testlogger.sh`, no RID needed — netstandard2.0, one build for every OS);
`reqnroll/testOutcomes/registerRun`/`getOutcome` wired into `lspMethods.ts`;
`runSettingsInjector.ts` (a TypeScript port of `TestLoggerRunSettings.cs`'s merge logic, using
`xml2js` — ported carefully, not by inspection alone: an early version broke on `Builder`'s
`Invalid character in name` because `parseStringPromise`'s `explicitArray: true` wraps every
*descendant* element in a one-item array but returns the parsed **root** element bare, a real
xml2js quirk confirmed by direct reproduction, not documented anywhere obvious); and
`testOutcomesService.ts`, which registers a run at extension activation and merges the logger into
whatever `dotnet.unitTests.runSettingsPath` already resolves to (the user's own file's content is
always preserved, never replaced).

**Session-scoped registration, not per-run.** VS and Rider re-register with the server for every run
because their own code launches the test process each time. Here, C# Dev Kit launches it, and there
is no confirmed VS Code event for "a test run is about to start" to hook for a per-run refresh —
`vscode.tests` exports no such signal, and C# Dev Kit's own run lifecycle isn't observable from
outside it. Registration happens once per extension activation instead, refreshed on window
reload/restart. This is safe because the server's endpoint doesn't expire and, since 2026-09-18,
carries no per-connection secret to go stale — see §5.1 for why an earlier per-run token was tried
and removed (it broke exactly this one-registration-many-connections shape: the token was single-use,
so every test-host connection after the first in a session was rejected).

**Off by default, opt-in via `reqnroll.testOutcomes.enabled`.** Unlike everything else this
extension does, this writes to a `dotnet.*`-namespaced setting shared with C# Dev Kit, persisted to
the workspace's own `.vscode/settings.json` (visible to teammates, possibly committed) — a bigger,
more visible side effect on shared state than anything VS/Rider's own state-only changes touch, so
it stays opt-in rather than defaulting to on.

**UI surface: a read-only `.feature`-file CodeLens (`src/VSCode/src/testOutcomes/testOutcomeCodeLens.ts`),
not a Run/Debug affordance.** VS Code's Reqnroll extension had tried and reverted two different
`.feature`-file UI surfaces for this exact class of feature already (a CodeLens, then a
`vscode.TestController` — see §4.3/#504's history), both because they duplicated C# Dev Kit's own
gutter/Test Explorer `Run` affordance for no benefit. This lens sidesteps that history by carrying
no action at all: its command (`reqnroll.testOutcomes.noop`) is a genuine no-op, registered only
because an unresolved `vscode.CodeLens` (no command) may never render, per `vscode.CodeLens`'s own
doc comment. It surfaces `✓`/`✗` on each Scenario/Scenario Outline line, sourced from
`reqnroll/testOutcomes/getOutcome`, refreshed by a new `reqnroll/testOutcomes/changed` push
notification (fired by the server after each debounced result batch) via
`CodeLensProvider.onDidChangeCodeLenses` — so a C# Dev Kit-triggered run updates the lens without
requiring the user to touch the `.feature` file. Scenario/Outline positions come from a standard
`textDocument/documentSymbol` request (`SymbolKind.Method` nodes, recursing through `Rule`-kind
`Namespace` nodes for nested scenarios), mirroring the Rider plugin's `RunLensSupport.collectMethodSymbols`.
The owning project's output assembly path (needed for `getOutcome`'s lookup key) is cached in
`ProjectManager` from the same MSBuild evaluation that already produces it for
`reqnroll/projectLoaded`, rather than re-evaluating. What this adds that C# Dev Kit's own UI
structurally cannot: C# Dev Kit annotates the *generated* `.cs` method, never the `.feature` file,
and has no notion of "one row of a Scenario Outline" — this lens sits on the `.feature`
Scenario/Outline line and names which row failed, on which step, when there's more than one.

- **Exit:** `reqnroll.testOutcomes.enabled` merges the logger into `dotnet.unitTests.runSettingsPath`
  without disturbing the user's existing runsettings content, and (same flag) renders the read-only
  outcome CodeLens; 32 new unit tests (253/254 in the full suite passing — the one failure is the
  same pre-existing, environment-sensitive fs-watcher test noted throughout this doc, unrelated to
  this change) exercise the runsettings merge logic, the bundling path-resolution logic, symbol
  collection, outcome aggregation, and title rendering. **Not yet verified live** (would need an
  actual C# Dev Kit-driven test run reporting through the merged runsettings, observed through the
  lens) — this remains the one open verification gap for the whole VS Code leg.

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
| Store | pure unit tests: aggregation, row upsert, path normalization, debounce | `Reqnroll.IdeSupport.LSP.Server.Tests/Features/TestOutcomes` (Phase 3.5; was VS-side) |
| Listener | uninvited connection accepted, registration reusable across connections, half-open connection, concurrent runs | same (Phase 3.5; was VS-side) |
| CodeLens wiring | interface-shaped glue; substitute the callback and assert store-first/bridge-fallback order | existing `RunTestCodeLens` test folder |
| VS live | the recipe in the hand-off note, extended with Debug, user runsettings, two instances | manual, recorded in the issue |
| Rider | devcontainer live run | manual |

Spec-suite (`.feature`) coverage is not the right layer here — nothing crosses the LSP wire.

## 8. Risks and open items

| # | Item | Impact | Plan |
|---|---|---|---|
| 1 | **Microsoft.Testing.Platform projects** (`HasTestingPlatformServerCapability: True`) don't use VSTest loggers; VS runs them in "testing platform server mode". A Reqnroll project on MSTest 3.x with `EnableMSTestRunner`, TUnit, or xunit.v3 in MTP mode gets no logger. | Glyphs fall back to the bridge for those projects; the bridge's outline blindness (#702) remains for them. | Detect per container (the capability is visible to VS; whether `ITestContainer` exposes it needs a look) and log it. MTP has its own extension model registered *in the test project* — that would be a core-repo conversation and is explicitly out of scope here. Drives §5.3. |
| 2 | **Path normalization** between `TestCase.Source` and `ScenarioTestTarget.OutputAssemblyPath` | Silent "no outcome" for a project | Reuse the extension's single normalization helper; unit-test the store with mixed case / trailing separators / `8.3` forms. #515 is the precedent. |
| 3 | `AddRunSettings(Execution)` called twice per Run | Two run ids minted per run, one unused | Cheap; never key state on "one call = one run". |
| 4 | A user's own runsettings lists a `TestAdaptersPaths` directory that contains an older `Reqnroll.IdeSupport.TestLogger.dll` (e.g. a copied extension folder) | Two loggers with the same friendly name; vstest picks one | Version the `ExtensionUri`? No — vstest resolves by friendly name. Log the full `TestAdaptersPaths` at Info; document it. Low likelihood. |
| 5 | Result batching: vstest batches testhost→runner results (`BatchSize` default 10 + timer) | Glyph latency up to ~1–2 s on long suites | Acceptable; `BatchSize` is ours to set in the injected runsettings if it isn't. |
| 6 | `Microsoft.VisualStudio.TestWindow.Interfaces` resolves to whatever VS is installed at build time (18.0.0.0 on the dev machine) while the VSIX targets 17.14+ | Same class of caveat the existing `TestWindow.Internal` reference already carries | Verify once on a 17.14 install or CI image before Phase 1 ships; VS ships binding redirects for its own assemblies, which is why the existing reference works. |
| 7 | NUnit / xUnit `ManagedMethod` shapes for outline rows not yet observed live | Wrong key → no glyph on those frameworks | Add NUnit and xUnit fixtures to the Phase 1 end-to-end test; it is exactly the property that test asserts. |
| 8 | Loopback listener flagged by endpoint-protection software | Logger can't connect; glyphs silently stale | Logger and listener both log the failure; Tests output pane gets a one-line warning on `runComplete`-without-connection detection. Named-pipe transport can be added behind the same `Endpoint` parameter later if this turns out to be real. |
| 9 | No per-connection secret (removed 2026-09-18, §5.1) | Any local process that can reach the loopback port can post fake outcomes to one IDE instance | Accepted — this was already true in practice with the token (it traveled inside the runsettings file the runner reads), so removing it changes nothing except honesty about the boundary. |

## 9. Issue / PR breakdown

1. Tracking issue (Phase 0) — links this plan; subtasks below as checkboxes.
2. PR: Phase 0 (spike branch as-is + CI green + user-runsettings live evidence).
3. PR: Phase 1 — logger transport + listener + store + CodeLens wiring + end-to-end test. Closes #702 on merge + live confirmation.
4. PR: Phase 2 — details pane + step-trace parsing.
5. PR: Phase 3 — persistence + in-progress + multi-instance check.
6. Issue each for Phase 4 (Rider) and Phase 5 (VS Code); Phase 6 is a decision recorded on the tracking issue.
7. Docs: revise Test-Runner-Integration-Design §6 after Phase 6, not before.
