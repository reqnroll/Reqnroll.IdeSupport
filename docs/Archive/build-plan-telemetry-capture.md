# Build Plan: Telemetry Capture & Publication

**Status: implemented (with documented exceptions).** This document records the as-built
telemetry pipeline and the status of every capture point. For the analytics *transport*
refactor (where the `TelemetryClient` lives, why, and how it is disposed) see the sibling
document [`plan-refactor-analytics-appinsights.md`](plan-refactor-analytics-appinsights.md).

## Objective

Wire a complete telemetry pipeline covering both IDE-originated events and LSP-server
events, with an event schema that captures discovery source, trigger context, and command
usage.

Telemetry is **emitted** by the LSP server (where the work happens) and **transmitted** by
the IDE host (where consent, identity, and the App Insights client live). There is one
`Microsoft.ApplicationInsights` `TelemetryClient`, and it lives in the **VS host layer
(`VSSDKIntegration`)** — *not* in `Common`. The cross-platform `net10.0` server carries no
AppInsights dependency; see §0.

---

## 0. Pipeline (as built)

```
LSP server component (handler / discovery provider)
  → ILspTelemetryService.SendEvent(eventName, properties)        [Reqnroll.IdeSupport.LSP.Server.Telemetry]
      → ILanguageServerFacade.SendNotification("telemetry/event", { eventName, properties })
          ── LSP notification ──▶
VS Extension: TelemetryEventInterceptor (Receive, method == "telemetry/event")
  → IAnalyticsTransmitter.TransmitEvent(new GenericEvent(eventName, properties))
      → VSSDKIntegration.AnalyticsTransmitter
          → IEnableAnalyticsChecker (opt-out gate)
          → Microsoft.ApplicationInsights.TelemetryClient.TrackEvent / TrackException
              → flushed on ReqnrollPluginPackage dispose
```

Key points:

- **`ILspTelemetryService`** (`LspTelemetryService`) is registered as a singleton in
  `ServiceCollectionExtensions.AddReqnrollLspCoreServices`. Server-side telemetry never
  references App Insights — it only emits `telemetry/event` notifications.
- Every emitting component takes an **optional** `ILspTelemetryService? = null` constructor
  parameter. When null (unit tests, or a client with no telemetry sink) emission is a no-op,
  so telemetry is non-intrusive to the feature logic.
- The server's `IMonitoringService` is `NullMonitoringService` (no-op); telemetry is not
  collected on the server side beyond emitting notifications.

---

## 1. Infrastructure status (implemented)

| Piece | Location | Status |
|-------|----------|--------|
| `IAnalyticsTransmitter` + events (`IAnalyticsEvent`, `GenericEvent`) | `Common/Analytics` | ✅ contracts (no AppInsights) |
| Concrete `AnalyticsTransmitter` + `TelemetryClient` | `VSSDKIntegration/Analytics` | ✅ host-side, MEF-exported |
| `ILspTelemetryService` / `LspTelemetryService` | `LSP.Server/Telemetry` | ✅ emits `telemetry/event` |
| DI registration of `ILspTelemetryService` | `LSP.Server/Hosting/ServiceCollectionExtensions` | ✅ singleton |
| `telemetry/event` client handler | `VS Extension/LspInterception/TelemetryEventInterceptor` | ✅ forwards to `IAnalyticsTransmitter` |
| Legacy `MonitorReqnrollDiscovery` | `MonitoringService` / `IMonitoringService` | ✅ removed |

---

## 2. Event Schema: Discovery (implemented)

Emitted by the LSP server. All use event name **`ReqnrollDiscoveryExecuted`** (renamed from
`Reqnroll Discovery executed` in issue #627) with a `DiscoverySource` discriminator. All names
are defined in the shared catalog `TelemetryEvents` (`Reqnroll.IdeSupport.Common.Telemetry`);
the live per-event schema is `docs/Telemetry-Events-Inventory.md`.

### 2.1 Sources & triggers

| Source | Where | TriggerContexts |
|--------|-------|-----------------|
| `Connector` | `ConnectorBindingRegistryProvider.RunDiscoveryAsync` (out-of-proc reflection) | `projectLoad`, `build` |
| `Roslyn` | `CSharpBindingDiscoveryService.UpdateFromSourceAsync` (in-proc source-level) | `csOpen`, `csEdit` |

`projectLoad` vs `build` is tracked by the `_isFirstRun` flag on the provider (first run after
project-scope creation = `projectLoad`; subsequent `TriggerRefresh` runs = `build`).

### 2.2 Connector discovery — three outcomes (all ✅)

| Outcome | Properties emitted |
|---------|--------------------|
| **Success** (registry swapped) | `DiscoverySource=Connector`, `TriggerContext`, `IsFailed=false`, `StepDefinitionCount`, `HookCount`, `ProjectTargetFramework` |
| **Hash no-op** (nothing changed) | `DiscoverySource=Connector`, `HashMatched=true`, `TriggerContext` |
| **Failure** (discovery threw) | `DiscoverySource=Connector`, `TriggerContext`, `IsFailed=true`, `ErrorMessage`, `ProjectTargetFramework` |

> **Counts reported.** `StepDefinitionCount` and `HookCount` come from
> `newRegistry.StepDefinitions.Length` / `newRegistry.Hooks.Length`. The legacy
> `Reqnroll.VisualStudio` extension reported only `StepDefinitionCount`; `HookCount` is new.
> **StepArgumentTransformations are deliberately not reported:** the connector surfaces them
> (`StepArgumentTransformationData`), but `ProjectBindingRegistry` does not model them, so there
> is no count available at this site. Adding it would require carrying transformations through
> the connector→registry import — out of scope for this telemetry work.

The failure event is emitted from the `catch (Exception)` block; `OperationCanceledException`
(a newer trigger cancelling an in-flight run) is treated as normal and emits nothing.
`_isFirstRun` is not cleared on failure so a retry still reports `projectLoad`.

### 2.3 Roslyn discovery (✅)

`DiscoverySource=Roslyn`, `TriggerContext=csOpen|csEdit`, `IsFailed=false`, `AffectedFile`,
`ProjectCount`, `ProjectTargetFramework`.

> **Known simplification:** the per-file binding delta and post-update step count are computed
> and logged inside `ApplyToProjectAsync` per owning project, but are not aggregated into the
> telemetry event (the original `BindingDelta` / `StepDefinitionCount` schema fields). The event
> fires once per `UpdateFromSourceAsync` call across all owners, so a single aggregate is
> ambiguous when a file is linked into multiple projects. Left out deliberately rather than
> emitting a misleading aggregate.

---

## 3. Event Schema: Commands (implemented)

All emitted from the corresponding LSP server handler at its success exit point via
`ILspTelemetryService`.

| Event | Properties | Handler(s) | Status |
|-------|-----------|---------|--------|
| `GoToStepDefinition command executed` | `LocationCount` | `DefinitionHandler` (`textDocument/definition`), `FindStepDefinitionsHandler` (`reqnroll/findStepDefinitions`) | ✅ |
| `FindHooks command executed` | — | `FindHooksHandler` (every `reqnroll/findHooks` lookup, incl. CodeLens prefetch) | ✅ (renamed from `GoToHook command executed` in issue #698; genuine navigations are client-originated as `GoToHook command executed`) |
| `FindUnusedStepDefinitions command executed` | `UnusedStepDefinitions`, `ScannedFeatureFiles`, `IsCancellationRequested` | `FindUnusedStepDefinitionsHandler` | ✅ |
| `CommentUncomment command executed` | — | `CommentToggleHandler` | ✅ |
| `Rename step command executed` | `Erroneous`, `Reason`, `ChangeAnnotationsUsed`, `EditedFileCount` | `RenameHandler` (every terminal path) | ✅ (see note) |
| `FindStepDefinitionUsages command executed` | `UsagesCount`, `IsCancelled`, `Protocol` | `FindStepUsagesHandler` + `ReferencesHandler` | ✅ (issue #581 finding 3) |
| `GoToMatchingScenarios command executed` | — | `FindMatchingScenariosHandler` | ✅ |
| `DefineSteps command offered` | `UndefinedStepCount`, `ActionsOffered` | `CodeActionHandler` | ✅ |
| `OpenProject command executed` | `FeatureFileCount` | `LspWorkspaceScopeManager` | ✅ (issue #581 finding 2) |
| `RenameTargets resolved` | `TargetCount` | `RenameTargetsHandler` | ✅ (issue #581 finding 5) |
| `ResolveTestTargets command executed` / `ResolveContainerTestTargets command executed` | — | `ResolveTestTargetsHandler` / `ResolveContainerTestTargetsHandler` | ✅ |
| `TestOutcomesRunCompleted` | `ResultCount`, `Aborted`, `Canceled`, `ReporterKind` | `TestOutcomeTcpListener` | ✅ (issue #722) |

> **Deviation from the original plan:** Rename was planned as a VS *client-side*
> (`RenameStepService` → `IAnalyticsTransmitter`) capture. It is instead emitted **server-side**
> from `RenameHandler.HandleRenameAsync`, consistent with the "server emits / host
> transmits" pipeline and avoiding a second emission path. Every terminal path emits —
> success and each validation/rejection branch — so `Erroneous` finally carries the signal its
> name promises, with `Reason` naming the rejecting validation (issue #581 finding 4).

**Deferred / deliberately absent:** `AutoFormatTable` — on-type table formatting fires per
keystroke, not per command, so it stays out of usage telemetry (perf sampling covers it via
`PerfTargets.OnTypeFormatting`). `DefineSteps`, `FindStepDefinitionUsages` and
`AutoFormatDocument` were **implemented** after this plan was written (see the rows above).

---

## 4. New Events

### 4.1 Completion inserted — **deferred** (not server-observable)

The original plan located this "in the LSP completion handler." That is not feasible:
standard LSP gives the server no notification when a completion item is **accepted/inserted**
— the client applies the `textEdit` locally (`GherkinCompletionHandler` even sets
`ResolveProvider = false`, so there is no resolve round-trip either). Emitting on every
`textDocument/completion` request would measure *offers*, not *insertions*, and would be very
high-volume (fires per keystroke/trigger char).

**Correct locus (future work):** a VS-client completion-commit handler in the extension,
emitting `Completion inserted` directly via `IAnalyticsTransmitter` (low volume — only on
actual acceptance, with `InsertedTextLength` / `Context` known at commit time). Out of scope
for the server.

### 4.2 Connector hash-noop rate — ✅ implemented

See §2.2 (the `HashMatched=true` outcome). Emitted when `RunDiscovery` returns the unchanged
hash, giving a build-churn signal without a full discovery payload.

### 4.3 Error recovery — **partially addressed / deferred**

- **Connector discovery failure** is captured (§2.2 failure outcome).
- **Server crash / auto-restart** is *not* self-reported: a process that has crashed cannot
  emit its own telemetry. If desired this belongs in the VS LSP **client** (the
  `ILanguageClient` restart callback), emitted host-side via `IAnalyticsTransmitter`. Deferred.

### 4.4 `PerfSample` — ✅ implemented (Performance Verification, Layer 4)

Field performance instrumentation. Every instrumented interactive handler records its duration
through `IOperationDurationRecorder` (`Diagnostics/Performance`); the primary sink is a `PERF`
line in the server log, and — when sampling is enabled — a **sampled** `PerfSample` telemetry
event is emitted for real-world P95 aggregation. See
[`Performance-Verification-Implementation-Plan.md`](../Performance-Verification-Implementation-Plan.md)
(Part B / T3) for the design.

| Property | Example | Notes |
|----------|---------|-------|
| `Operation` | `textDocument/completion#step` | LSP method (completion split into `#keyword` / `#step` per the distinct §9 targets) |
| `DurationMs` | `42` | rounded wall-clock ms |
| `DurationBucket` | `<=50` | coarse band for cheap aggregation |
| `IDEClient` | `visualstudio` | from `--ide`; enables per-IDE P95 breakdown |

**Privacy:** the event carries **no URI, path, or file content** — only the operation label,
duration, bucket and IDE client. (The `PERF` *log* line may include the URI for local diagnosis;
the *telemetry* payload never does.) Covered by `OperationDurationRecorderTests`.

**Sampling & opt-out:** emission is gated on `IPerfTelemetrySampler`, whose rate comes from the
`REQNROLL_PERF_TELEMETRY_SAMPLE` env var (fraction in `[0,1]`, default **0** = opt-in). When a
rate is set, sampled events still pass through the existing host-side opt-out gate
(`IEnableAnalyticsChecker`) before transmission. Volume is bounded by the sample rate, unlike the
rejected per-request "Completion inserted" idea in §4.1.

---

## 5. Build order — status

| Phase | Items | Status |
|-------|-------|--------|
| **A — Infrastructure** | AppInsights placement, transmitter, `telemetry/event` handler, `ILspTelemetryService` + DI | ✅ done (transmitter is host-side in VSSDKIntegration, not Common) |
| **B — Discovery events** | connector + Roslyn events; remove legacy `MonitorReqnrollDiscovery` | ✅ done; failure event added |
| **C — Command events** | GoToStepDef, GoToHook, FindUnused, CommentUncomment, Rename | ✅ done (Rename server-side) |
| **D — New events** | hash-noop ✅; completion-inserted deferred (client-side); error-recovery partial | ◑ partial |
| **E — Cleanup** | dead monitor methods removed; opt-out gate honored in transmitter | ✅ done |

---

## 6. Testing (as built)

### 6.1 Transport (VS host)

`tests/VisualStudio/Reqnroll.VisualStudio.Tests/Analytics/AnalyticsTransmitterTests.cs` —
the `IAnalyticsTransmitter` sink: opt-out gate, event/exception forwarding, flush-on-dispose,
catch-all safety. Uses a real `TelemetryClient` + in-memory channel (see the refactor doc §5).

### 6.2 LSP emission

All under `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/`:

| Test file | Covers |
|-----------|--------|
| `Telemetry/LspTelemetryServiceTests.cs` | `SendEvent` calls `SendNotification("telemetry/event", …)` with name + properties |
| `Discovery/ConnectorBindingRegistryProviderTests.cs` | discovery success, hash-noop, **failure**, projectLoad→build transition, null-telemetry safety, no-emit on cancellation |
| `Discovery/CSharpBindingDiscoveryServiceTests.cs` | Roslyn discovery event |
| `Features/Definition/GoToStepDefinitionsHandlerTests.cs` | `GenerateSnippet`; no-emit on non-feature / no-buffer |
| `Features/Definition/GoToHooksHandlerTests.cs` | hook command event |
| `Features/FindUnusedStepDefs/FindUnusedStepDefinitionsHandlerTests.cs` | unused-count properties |
| `Features/Commenting/CommentToggleHandlerTests.cs` | comment command event |
| `Features/Rename/StepRenameHandlerTests.cs` | rename command event |

Pattern: a `CreateSutWithTelemetry(Substitute.For<ILspTelemetryService>())` overload asserts
`telemetry.Received(1).SendEvent("…", Arg.Is<Dictionary<string,object?>>(d => …))`, with a
companion `DidNotReceive` test on guard-rail paths. Full suite: **419 tests green**.

### 6.3 Integration / manual

| Check | How |
|-------|-----|
| Connector discovery event fires | Trigger build; verify in App Insights |
| Roslyn discovery event fires | Open/edit a `.cs` step file; verify event |
| Command events fire | Invoke GoToStepDefinition etc.; verify events |
| Opt-out suppresses all | `REQNROLL_TELEMETRY_ENABLED=0`; verify no events (gate is in the host transmitter) |
| Shutdown flushes | Close VS; verify no lost events |

---

## 7. Risk / notes

| Item | Resolution |
|------|------------|
| OmniSharp support for `telemetry/event` | ✅ confirmed working via `ILanguageServerFacade.SendNotification`; covered by `LspTelemetryServiceTests` |
| `telemetry/event` client handler | ✅ implemented (`TelemetryEventInterceptor`) |
| Roslyn discovery fires per keystroke (volume) | Roslyn re-discovery is itself triggered per `.cs` edit; the telemetry event rides that frequency. If volume becomes a concern, coalesce at the emission site. Connector discovery is debounced (500 ms). |
| Completion volume | Why §4.1 is deferred to a client-side commit hook rather than emitted per completion request |
| Server crash cannot self-report | §4.3 — belongs in the VS LSP client restart callback if wanted |

---

## 8. Debugging: local telemetry mirror (implemented)

A developer aid that persists every telemetry message to a local newline-delimited-JSON file for
later review. **Off by default; independent of the opt-out gate** so you see what the system
*produced* even when transmission is disabled or events are dropped downstream.

### Toggle

Environment variable `REQNROLL_TELEMETRY_DEBUG_LOG` (resolved by `Common.Diagnostics.TelemetryDebugLog`):

| Value | Effect |
|-------|--------|
| unset / empty / `0` / `false` | disabled (no-op sink) |
| `1` / `true` | enabled → `%LOCALAPPDATA%\Reqnroll\reqnroll-telemetry-{yyyyMMdd}.jsonl` |
| any other value | enabled → treated as the target file path |

Separate from `REQNROLL_TELEMETRY_ENABLED` (the transmission opt-out). The VS-launched LSP server
is a child process and inherits the host's environment, so setting the variable once enables both
sinks; with the default path they append to the **same** daily file and lines interleave,
distinguished by the `source` field.

### Two capture points

- **Server** — `FileLoggingLspTelemetryService` decorates `ILspTelemetryService` (wired in
  `ServiceCollectionExtensions`), mirroring every emitted event *before* forwarding. Cross-IDE
  (lives in the shared server), independent of the host. Records `source="server"`.
- **Host** — `VSSDKIntegration.AnalyticsTransmitter` records both events and exceptions:
  - `TransmitEvent` records every event *before* the opt-out gate, capturing the outcome.
    Covers host-only events (wizards, install/upgrade) the server never sees.
  - `TransmitException` (the chokepoint for normal + fatal exception telemetry) records each
    exception with `event="(exception) {Type}"`, the reported type/message and any extra props
    (e.g. `IsFatal`) in `props`, and `enabled=null` — the exception path is **not** gated by the
    opt-out checker, so the mirror reflects that it transmits regardless.

Running both at once is the key diagnostic: an event present with `source="server"` but missing
the matching `source="host"` line isolates a `TelemetryEventInterceptor`/wire fault from a
server-emission fault.

### Record schema (one JSON object per line)

```json
{"ts":"2026-06-27T14:02:11.314Z","source":"server","event":"ReqnrollDiscoveryExecuted","props":{"DiscoverySource":"Connector","StepDefinitionCount":42,"HookCount":7},"enabled":null,"transmitted":null,"error":null}
{"ts":"2026-06-27T14:02:12.991Z","source":"host","event":"Welcome dialog dismissed","props":{},"enabled":false,"transmitted":false,"error":null}
{"ts":"2026-06-27T14:02:13.402Z","source":"host","event":"(exception) DiscoveryException","props":{"ExceptionType":"...DiscoveryException","Message":"connector timed out","IsFatal":"True"},"enabled":null,"transmitted":true,"error":null}
```

`enabled`/`transmitted`/`error` are host-only (null on server lines). For exception records the
reported exception's text lives in `props` (`ExceptionType`/`Message`); the top-level `error`
field is reserved for a *transmission* failure (non-null only when `TrackException`/`TrackEvent`
itself threw). Writes are append-only,
lock-guarded within a process, and all I/O errors are swallowed — the mirror never affects the
feature path. (Cross-process concurrent appends to the shared default file are best-effort; a
dropped debug line under contention is acceptable. Use distinct paths per process if that matters.)

### Tests

| Test | Covers |
|------|--------|
| `Common.Tests/Diagnostics/TelemetryDebugLogTests` | env-var parsing (on/off/path), JSONL round-trip, never-throws on bad path |
| `LSP.Server.Tests/Telemetry/FileLoggingLspTelemetryServiceTests` | decorator mirrors `source="server"` and forwards unchanged; null-sink still forwards |
| `Reqnroll.VisualStudio.Tests/Analytics/AnalyticsTransmitterTests` | host mirror records `enabled=false/transmitted=false` when opted out, `true/true` when sent, `error` when an event transmission throws; and exception telemetry with type/message/`IsFatal` in props (`enabled=null`), incl. the transmit-failure case |
