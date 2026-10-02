# Telemetry Events — Inventory & Schema

> **Scope:** every telemetry event the Reqnroll IDE Support product transmits today, the
> component that fires it, when, its property schema, and how its data can be used in
> Application Insights. Issue #797.
>
> **Source of truth for names:** `src/Core/Reqnroll.IdeSupport.Common/Telemetry/TelemetryEvents.cs`
> (one catalog covering the .NET server and host; VS Code mirrors it in
> `src/VSCode/src/telemetryEvents.ts` and Rider in `RiderTelemetryTransmitter.kt` for
> client-originated names only). This document is descriptive prose on top of that catalog —
> if they disagree, the code wins. The pre-#627 story (why the catalog exists, the two renames)
> is recorded on the catalog class itself.

## 1. How telemetry flows

There is **one** Application Insights resource (`InstrumentationKey 3fd018ff-819d-4685-a6e1-6f09bc98d20b`)
shared by every Reqnroll IDE client, so event names form one flat namespace regardless of emitter.

Two emission paths exist:

1. **Server-originated (most events).** The LSP server emits
   `telemetry/event { eventName, properties }` via `ILspTelemetryService.SendEvent`
   (`LspTelemetryService.cs`). Each IDE host intercepts the notification and forwards it to
   its transmitter: VS's `TelemetryEventInterceptor` → `TelemetryTransmitter`
   (Microsoft.ApplicationInsights SDK), VS Code's `registerTelemetry` in `telemetry.ts` →
   `@vscode/extension-telemetry`, Rider's `ReqnrollTelemetryEventInterceptor` →
   `RiderTelemetryTransmitter` (direct HTTP POST to the ingestion endpoint). The server never
   touches Application Insights directly.
2. **Host-originated (VS lifecycle events).** The VS extension builds a `VsGenericEvent`/
   `GenericEvent` and hands it to `ITelemetryTransmitter` directly. These are events that fire
   before the server starts (install/upgrade), have no server-side equivalent (wizard dialogs,
   link clicks), or describe something only the IDE client can observe (a genuine "Go to Hooks"
   navigation). VS Code and Rider similarly originate `GoToHook command executed` themselves.

Gate: `REQNROLL_TELEMETRY_ENABLED` (unset or `1` = on, anything else = off) is the cross-IDE
kill switch; each host additionally honors its own opt-out (VS telemetry settings,
`telemetry.telemetryLevel` in VS Code). Debugging: `REQNROLL_TELEMETRY_DEBUG_LOG` mirrors every
event (server- and host-side) to a local JSONL file — see the archived
`docs/Archive/build-plan-telemetry-capture.md` §8.

**Client identity (issue #844).** Every event carries one canonical client identity so
cross-IDE queries (`where customDimensions.IdeClient == "vscode"`) work uniformly. The decision:

| Key | Stamped by | Value |
|---|---|---|
| `IdeClient` | **canonical.** Server (`IdentityStampingLspTelemetryService`, outermost decorator, so the debug-log mirror sees it) on every server-originated event; each host also stamps it on every event it transmits (covers host-originated events) | `visualstudio` \| `vscode` \| `rider` (the `--ide` vocabulary) |
| `ServerVersion` | Server | assembly informational version |
| `SessionId` | Server | random GUID per server process (not user-identifying; links an event stream to one server run) |
| `Ide` / `IdeVersion` / `ExtensionVersion` | Each host, on every event it transmits (VS, Rider, and — since #844 — VS Code) | human-readable IDE product name, IDE product version (**the single source of the IDE version**), extension version |

Stamping never overrides a key the caller already set, and server-side stamping copies the property
dictionary rather than mutating the caller's. `PerfSample`'s former `IDEClient` key is retired in
favor of `IdeClient` (historical `IDEClient` data is not back-filled). `Ide*`/`ExtensionVersion`
remain host-stamped because only the host knows the IDE product and extension build. There is
no server-stamped IDE version: the host stamps `IdeVersion` on every event it transmits (server-
and host-originated), whereas `InitializeParams.ClientInfo.Version` is optional, client-defined,
and absent before `initialize`, so the server records it in its startup log only.

**Schema conventions.** Events are identified by name only — there are no event IDs, and the
property dictionaries are the schema. Transmitters stringify every property value; booleans
become `"True"`/`"False"`, numbers their invariant string. All property names are PascalCase, including on the `telemetry/event` wire: the server sends
properties as a JSON object whose names are never rewritten (a dictionary would be camelCased by
the LSP serializer), and the hosts forward them verbatim (#844).
Names are `"<PascalCaseWord> <action>"` in the legacy style for command events
(`"FindStepUsages command executed"`) or a single PascalCase word for the two events renamed in
#627 (`ReqnrollDiscoveryExecuted`, `UnhandledException`).

**Renaming policy.** A rename shows up as a compile error at every call site (all names flow
through the catalog), so it is *safe* to do — but historical Application Insights data is not
back-renamed, so a rename splits the metric series. Treat `ReqnrollDiscoveryExecuted` and
`UnhandledException` (#627) as the precedent: confirm with the team before renaming any event.

**Privacy.** No file paths, test names, step text, source content, or user-identifiable strings
are transmitted. Every exception-derived string — the `UnhandledException` `Message` and the
`ReqnrollDiscoveryExecuted` `ErrorMessage` — is scrubbed for filesystem paths (`<path>`) at the
last server hop before the event goes to the client (`LspTelemetryService` →
`TelemetryScrubber.ScrubProperties`, #843), so emit sites and the local debug-log mirror keep the raw text for
debugging. Other string properties are fixed literals
(`Reason`, `TriggerContext`, `DiscoverySource`) or TFMs. `AffectedFile` on the Roslyn discovery event carries the file
*name* only. Discovery and command events carry counts and flags. (The local `PERF` log line may
include URIs; the `PerfSample` telemetry payload never does.)

---

## 2. Discovery events

### `ReqnrollDiscoveryExecuted` — binding discovery outcome

| | |
|---|---|
| **Constant** | `TelemetryEvents.ReqnrollDiscoveryExecuted` |
| **Emitters** | LSP server: `CSharpBindingDiscoveryService` (Roslyn, in-proc source parse) and `ConnectorBindingRegistryProvider` (out-of-proc connector reflection), discriminated by `DiscoverySource` |
| **When** | Each completed binding-discovery run: Roslyn on a `.cs` binding file open/`csEdit` per owning project; Connector on the first `projectLoad` after a project registers, then on every post-build refresh (debounced) |

**Properties**

| Property | Type | Meaning |
|---|---|---|
| `DiscoverySource` | `"Connector"` \| `"Roslyn"` | Which discovery path ran |
| `TriggerContext` | Connector: `"projectLoad"` \| `"build"`; Roslyn: `"csOpen"` \| `"csEdit"` | What triggered the run |
| `IsFailed` | bool | false (success, or the connector hash-noop) or true (failure) |
| `HashMatched` | bool | Connector-only: true when the assembly hash was unchanged and the registry was kept (no-op run) |
| `StepDefinitionCount` / `HookCount` | int | Connector success: counts in the swapped-in registry. (Step Argument Transformations are surfaced by the connector but not modeled by `ProjectBindingRegistry`, so deliberately not reported.) |
| `ErrorMessage` | string | Connector failure: the exception message, filesystem-path-scrubbed (`<path>`) |
| `AffectedFile` | string | Roslyn: the file *name* (no path) that triggered re-discovery |
| `ProjectCount` | int | Roslyn: how many owning projects the file was applied to |
| `ProjectTargetFramework` | string? | Roslyn: first owner's TFM; Connector (all three outcomes): the project's TFM |
| `DurationMs` / `DurationBucket` | long / string | Connector (all three outcomes): wall time of the discovery run (excluding the debounce), and the same coarse bucket `PerfSample` uses (`<=10` … `>5000`) |
| `ReqnrollVersion` | string | Connector success only: the project's Reqnroll version reduced to `major.minor`; omitted when unknown |
| `ConnectorType` | string | Connector success only: which connector flavour ran |
| `ConnectorExitCode` | int | Connector success only: the connector process exit code |

The last four come from the connector's `DiscoveryResult.TelemetryProperties` through an explicit
whitelist (`ConnectorRunTelemetry`, issue #846). The connector's `ConnectorArguments` (command
line, contains paths) and raw `Error` text are deliberately **not** forwarded, and the connector's
`ProjectReqnrollVersion` is dropped in favour of the normalised `ReqnrollVersion`.

**Analytics use.** (a) *Discovery reliability* — failure rate and error-message histogram;
(b) *build churn* — the connector `HashMatched=true` rate measures how often a build or
projectLoad produced no binding change (a cheap "rebuilds that change nothing" proxy);
(c) *path mix* — Connector vs Roslyn share, `projectLoad` vs `build` vs `csOpen`/`csEdit`
triggers; (d) *solution size curve* — step/hook counts per project, per TFM.

---

## 3. Command & interaction events (server)

All fired by the named handler at its success exit point (or, where noted, every terminal
path). All are emitted once per request; none contain file content.

### `ResolveTestTargetsCommandExecuted` / `ResolveContainerTestTargetsCommandExecuted`
| | |
|---|---|
| **Emitters** | `ResolveTestTargetsHandler` / `ResolveContainerTestTargetsHandler` |
| **When** | A Run CodeLens resolves a scenario's (or Feature/Rule container's) test targets (`reqnroll/resolveTestTargets` / `reqnroll/resolveContainerTestTargets`) |
| **Properties** | `TargetCount` (int): targets resolved (0 = project not built yet / no generated method); `Kind` (string): single-scenario event — `Scenario` \| `Outline` \| `ExampleRow` (omitted when the range intersects no scenario); container event — `Feature` \| `Rule` |

**Analytics use.** Run CodeLens adoption: how often Run/Debug targets are resolved, container
("Run Scenarios") vs single-scenario split, which kind of block is being run, and how often a
resolution comes back empty (`TargetCount = 0`). Fire rate is high (resolves on lens render) — read
as *lookups*, not *runs*.

### `GoToStepDefinitionCommandExecuted`
| | |
|---|---|
| **Emitters** | `DefinitionHandler` (`textDocument/definition`) and `FindStepDefinitionsHandler` (`reqnroll/findStepDefinitions`, Visual Studio's path — issue #757) — one event for the same user command across both protocols |
| **When** | After a step is resolved at the cursor (guard-clause rejections don't emit) |
| **Properties** | `LocationCount` (int): navigable rows offered; `Status` (string): `Bound` (defined, at least one navigable row) \| `Ambiguous` \| `Undefined` (no defined binding) \| `Unresolved` (defined, but no binding source exists on this machine — issue #540); `Protocol` (`"textDocument/definition"` \| `"reqnroll/findStepDefinitions"`) |

**Analytics use.** Go to Step Definition usage, including the ambiguous-match frequency
(`Status = Ambiguous`), how often the command lands on an undefined step, how often navigation
is impossible because the binding source is not local (`Unresolved`), and the per-IDE path via
`Protocol`.

### `FindHooksCommandExecuted` (server) vs `GoToHookCommandExecuted` (clients)
| | |
|---|---|
| **Emitter (server)** | `FindHooksHandler` |
| **When (server)** | Every `reqnroll/findHooks` request — including the classic VS CodeLens Details-popup prefetch, which hits the same handler on every lens render |
| **Properties** | `HookCount` (int): hook locations returned (navigable only; hooks with no local source are not counted) |

**Analytics use.** *Lookup* volume only (issue #698). The server cannot tell a navigation from a
prefetch, so genuine-navigation telemetry is emitted client-side as
[`GoToHookCommandExecuted`](#gotohookcommandexecuted) at each IDE's navigation command.

### `DefineStepsCommandOffered`
| | |
|---|---|
| **Emitter** | `CodeActionHandler` |
| **When** | The "Define step(s)" lightbulb action is *offered* (undefined steps present); not when the user accepts it — the `WorkspaceEdit` is applied client-side, so the server can't observe acceptance |
| **Properties** | `UndefinedStepCount` (int), `ActionsOffered` (int, counted from the final post-filter/post-cap list) |

**Analytics use.** Undefined-step pressure (how often the quick fix is needed) and offer rate.
Acceptance rate is not measurable server-side by design.

### `FindUnusedStepDefinitionsCommandExecuted`
| | |
|---|---|
| **Emitter** | `FindUnusedStepDefinitionsHandler` |
| **When** | After a Find Unused Step Definitions request completes |
| **Properties** | `UnusedStepDefinitions` (int), `ScannedFeatureFiles` (int — in practice the number of project registries scanned), `IsCancellationRequested` (bool), `TotalStepDefinitions` (int — distinct valid step definitions scanned, counted the way the unused count is, so a binding reported by several projects counts once), `DurationBucket` (string, see "Bucket schemes" below) |

**Analytics use.** Feature hygiene: unused-step counts, the unused *ratio*
(`UnusedStepDefinitions / TotalStepDefinitions`) and scan cost (duration bucket); the
cancellation flag captures user impatience on large solutions.

### `RenameStepCommandExecuted`
| | |
|---|---|
| **Emitter** | `RenameHandler` |
| **When** | *Every* terminal path of a Step Rename — success **and** each validation/rejection branch (issue #581 finding 4: `Erroneous` used to be hardcoded `false`) |
| **Properties** | `Erroneous` (bool); `Reason` (string, rejection path, omitted on success); `ChangeAnnotationsUsed` (bool?, success only); `EditedFileCount` (int?, success only); `Origin` (`"Feature"` \| `"CSharpBinding"` — the file type the rename was invoked in; every path); `OccurrenceCount` (int?, success only — step occurrences in `.feature` files the rename covers); `DurationBucket` (string, every path; see "Bucket schemes" below) |

**Analytics use.** Step Rename usage and failure modes: the `Reason` histogram tells you *which*
validation is rejecting users, `ChangeAnnotationsUsed` tracks atomic-edit adoption (needed for
multi-file rename in clients without LSP change-annotation support), `EditedFileCount` the
typical rename blast radius, `Origin` whether users rename from the feature step or from the
binding, `OccurrenceCount` how many steps a rename rewrites, and `DurationBucket` its latency.

### `FindStepDefinitionUsagesCommandExecuted`
| | |
|---|---|
| **Emitters** | `FindStepUsagesHandler` (`reqnroll/findStepUsages`, VS) and `ReferencesHandler` (`textDocument/references`, VS Code/Rider — issue #581 finding 3) — one event so the metric doesn't undercount by excluding two of three IDEs |
| **When** | After a usage lookup on a binding ("is a binding" gate passed) |
| **Properties** | `UsagesCount` (int), `IsCancelled` (bool), `Protocol` (`"reqnroll/findStepUsages"` \| `"textDocument/references"`), `FileCount` (int — distinct `.feature` files in the result), `DurationBucket` (string, see "Bucket schemes" below) |

**Analytics use.** Find Step Definition Usages frequency and result distribution (usages vs
files spread); `Protocol` breaks out the per-IDE path so a client-specific regression is
visible; `DurationBucket` the search cost.

### `CommentUncommentCommandExecuted`
| | |
|---|---|
| **Emitter** | `CommentToggleHandler` |
| **When** | After a comment/uncomment `workspace/executeCommand` round trip |
| **Properties** | `Mode` (`"Toggle"` \| `"Comment"` \| `"Uncomment"` — the mode the client *requested*; a `Toggle` is not resolved to Comment/Uncomment server-side), `LineCountBucket` (string — lines the command covered; see "Bucket schemes" below) |

**Analytics use.** Comment-toggle usage (a proxy for "users authoring Gherkin interactively"),
which command flavor is used, and whether it is applied to single lines or blocks.

### `AutoFormatDocumentCommandExecuted`
| | |
|---|---|
| **Emitter** | `FormattingHandler` |
| **When** | After `textDocument/formatting` (whole document) or `textDocument/rangeFormatting` (selection) |
| **Properties** | `IsSelectionFormatting` (bool), `EditCount` (int — edits returned that change the text; the handler returns one whole-range replacement, so this is 0 or 1, and 0 = already formatted), `DocumentLineBucket` (string — document length in lines; see "Bucket schemes" below) |

**Analytics use.** Format-on-demand usage, whole-document vs selection split, how often the
command is a no-op (`EditCount = 0`), and the document sizes it runs on. (On-type table
formatting is deliberately *not* telemetried — see the retired-event table below.)

### `GoToMatchingScenariosCommandExecuted`
| | |
|---|---|
| **Emitter** | `FindMatchingScenariosHandler` |
| **When** | After a Go To Matching Scenarios (`reqnroll/findMatchingScenarios`) request |
| **Properties** | `MatchCount` (int): scenarios returned |

**Analytics use.** Hook CodeLens clicks / matching-scenario lookups on `.cs` hook bindings, and
how many scenarios a hook typically applies to.

### `OpenProjectCommandExecuted`
| | |
|---|---|
| **Emitter** | `LspWorkspaceScopeManager` (server — issue #581 finding 2) |
| **When** | The *first* time a Reqnroll project is discovered in a workspace (not on VS's post-build `projectLoaded` re-sends); covers all three IDEs from one place, unlike the per-client `Feature file opened` |
| **Properties** | `FeatureFileCount` (int?, `null` = membership baseline not yet arrived, not zero); `ProjectTargetFramework` (string, the full TFM moniker); `ProgrammingLanguage` (`CSharp`/`VB`/`FSharp`/`Other`, from the project-file extension — never the path). The Reqnroll version is *not* here: they are reported by the first `ReqnrollDiscoveryExecuted` (issue #846) |

**Analytics use.** Active-session project counts; per-solution feature-file scale. A *sessions*
proxy: one event per project per server lifetime.

### `RenameTargetsResolved`
| | |
|---|---|
| **Emitter** | `RenameTargetsHandler` |
| **When** | Every `reqnroll/renameTargets` resolution (the Step Rename picker's candidate enumeration, including when a rename resolves unambiguously) |
| **Properties** | `TargetCount` (int) |

**Analytics use.** The only signal for how often the multi-attribute picker actually appears
(`TargetCount > 1`) versus an unambiguous rename — independent of whether the rename itself
succeeds.

### `TestOutcomesRunCompleted`
| | |
|---|---|
| **Emitter** | `TestOutcomeTcpListener` |
| **When** | A test run's socket connection ends: the bundled VSTest logger / MTP reporter delivers `runComplete`, **or** the connection closes after `runStart`/results without one (killed test host, crashed MTP process — sent with `Aborted=true`, `CompletedNormally=false`). Idle spare connections that never started a run send nothing |
| **Properties** | `ResultCount` (int — results stored), `PassedCount` / `FailedCount` / `SkippedCount` (int — stored results by outcome), `ExecutedCount` (int — the runner's `executed` from `runComplete`; results seen so far on a dropped connection), `Aborted` (bool), `Canceled` (bool), `CompletedNormally` (bool — `false` when aborted, canceled or dropped), `ReporterKind` (string — which reporter flavor, incl. the MTP source-compiled one, sent the run), `TargetFramework` (string — the hello line's TFM moniker, e.g. `.NETCoreApp,Version=v8.0`; empty if not sent), `DurationMs` (long — hello to runComplete/drop), `DurationBucket` (string — same buckets as `PerfSample`) |

**Analytics use.** Run CodeLens actual-run volume (the completion side of the
`ResolveTestTargets…` lookup events), abort/cancel/crash rates (dropped runs are now counted rather
than only logged), pass/fail mix, run duration, TFM distribution, and MTP source-compiled reporter
adoption in the wild (issue #722). Counts/flags/durations/TFM only — no paths, no test names.

**Not sent (issue #848).** Run vs Debug and Scenario/Feature/Rule/Project scope: `reqnroll/registerTestRun`
takes no parameters and is called once per client session, not per Run click, so the server cannot
learn them. A user-click event would have to be emitted by each client.

### `TagIndexFirstScanCompleted`
| | |
|---|---|
| **Emitter** | `FeatureTagIndex` (tag completion's per-project index, issue #828) |
| **When** | Once per project per server session — the first tag-completion request that finds the project owns at least one `.feature` file, after the index has been built |
| **Properties** | `FileCount` (int — `.feature` files in the project scan), `FilesParsedFromDisk` (int — how many of them were closed files read and parsed rather than served from an open buffer), `DistinctTagCount` (int), `DurationMs` (long) |

**Analytics use.** The one-time cost of the first `@` completion in a project: how big real
projects are and what the cold scan costs, which is what decides whether per-request write-time
checks (current design) are enough or watched-file invalidation is needed. Counts and a duration
only — no project, file or tag names. There is deliberately no per-completion event (completion
fires per keystroke; see `Completion inserted` in §7); steady-state tag-completion latency is
`PerfSample` with `Operation = textDocument/completion#tag`.

### Bucket schemes (issue #849)

Characteristic properties are counts, flags, small closed enums or *buckets* — never paths or text.
Key names live in `TelemetryProperties` (LSP.Server); bucketing in `TelemetryBuckets`.

| Property | Scheme |
|---|---|
| `DurationBucket` | Same scheme as `PerfSample`'s: `<=10`, `<=25`, `<=50`, `<=100`, `<=250`, `<=500`, `<=1000`, `<=5000`, `>5000` (ms of handler wall-clock time) |
| `LineCountBucket`, `DocumentLineBucket` | `0`, `1`, `2-10`, `11-50`, `51-200`, `201-1000`, `1000+` |

**Not yet implemented from issue #849** (needs client work or a design decision): the client-side
`Source` (`Command` \| `ContextMenu` \| `CodeLens`) on `GoToHookCommandExecuted` (three IDE
clients, three languages); the `Mode` on `CommentUncomment` is the requested mode, not a resolved
Comment/Uncomment; `DefineSteps` properties are tracked separately in #847.

---

## 4. Lifecycle & project events (Visual Studio host)

All emitted by `VSSDKIntegration/Telemetry/TelemetryService.cs` (`Monitor*` methods) through
`ITelemetryTransmitter`. Events that fire before the LSP server starts (install, upgrade) can
only originate here.

| Event | Emitter / when | Properties |
|---|---|---|
| `ExtensionLoaded` | `MonitorOpenProjectSystem` — on extension activation in an IDE scope | — |
| `Project loaded` | `MonitorOpenProject` — a Reqnroll project opens (VS host path) | project settings¹ + `FeatureFileCount` |
| `Feature file opened` | `MonitorOpenFeatureFile` — a `.feature` file opens (once per open-lifetime, same transition as `reqnroll/documentActivated`) | project settings¹ |
| `Extension installed` | `MonitorExtensionInstalled` — first activation after installation | — |
| `Extension upgraded` | `MonitorExtensionUpgraded` — first activation after version change | `OldExtensionVersion` |
| `"{N} day usage"` | `MonitorExtensionDaysOfUsage` — daily-active-use heartbeat (`WelcomeService`, right after incrementing `UsageDays`); the day count is part of the name, so each count is its own event (constant: `DaysOfUsageEventNameFormat` = `"{0} day usage"`) | — |
| `Feature file added` | `MonitorCommandAddFeatureFile` — feature file added via command/wizard | project settings¹ |
| `Reqnroll config added` | `MonitorCommandAddReqnrollConfigFile` — `reqnroll.json` added via command/wizard | project settings¹ |
| `Project Template Wizard Started` | `MonitorProjectTemplateWizardStarted` — "add new Reqnroll project" wizard opens | — |
| `Project Template Wizard Completed` | `MonitorProjectTemplateWizardCompleted` — wizard finishes | `SelectedDotNetFramework`, `SelectedUnitTestFramework`, `AddFluentAssertions` |
| `Link clicked` | `MonitorLinkClicked` — a link in extension UI (welcome/upgrade dialogs) is opened | `Source` (dialog/VM name), `URL` |
| `Welcome dialog dismissed` / `Upgrade dialog dismissed` | `MonitorWelcomeDialogDismissed` / `MonitorUpgradeDialogDismissed` — dialog closes | additional props (e.g. visited pages) |

¹ **Project settings props** (`GetProjectSettingsProps`): `ReqnrollVersion`,
`ProjectTargetFramework`, `SingleFileGeneratorUsed` (design-time code-behind generation on),
`ProgrammingLanguage`, and `LegacySpecFlow` (true only for a SpecFlow project).

**Analytics use.** Adoption lifecycle: install→upgrade funnel, daily-active heartbeat (rollup
`* day usage` by count), project-scale signals (ReqnrollVersion/TFM distribution,
SpecFlow migration), wizard abandonment (Started vs Completed) and framework-choice split,
link/content engagement on the welcome/upgrade surfaces.

---

## 5. Client-originated navigation event

### `GoToHookCommandExecuted`
| | |
|---|---|
| **Emitter** | VS `GoToHooksCommand`; VS Code `doGoToHooks` (`src/VSCode/src/commands/goToHooks.ts`); Rider `GoToHooksRunner` — three client copies of the same constant, per the catalog's mirror rule |
| **When** | A *genuine* "Go to Hooks" navigation command fires. Unlike the server's `FindHooksCommandExecuted` (every `reqnroll/findHooks` lookup, including CodeLens prefetch), only the client knows the command actually ran (issue #698) |
| **Properties** | — |

**Analytics use.** True Go-to-Hooks navigation rate per IDE — the honest counterpart to the
server's lookup volume.

---

## 6. Error & perf events

### `UnhandledException` (server)
| | |
|---|---|
| **Emitter** | `LspErrorTelemetryService.MonitorError` — the LSP server's `ITelemetryService` implementation; every other `Monitor*` member is a no-op there |
| **When** | Any exception reported through `IErrorTelemetryService` (e.g. `IdeSupportGherkinParser`, `IdeSupportTagParser`, `CompletionContextResolver`, `WatchedFilesHandler` config loads), driven by `IdeSupportLoggerExtensions.LogException` |
| **Properties** | `ExceptionType` (full type name), `Message` (filesystem-path-scrubbed → `<path>`), `IsFatal` (bool, only when the caller classified it), `Source` (string, omitted when unknown — simple class name, no namespace, of the topmost stack frame inside a `Reqnroll.IdeSupport*` type, async/lambda helper types folded into their declaring class; the stack itself is never sent) |

### VS host exception transmission (not an event name)
The VS host transmits exceptions with Application Insights' `ExceptionTelemetry` (fatal when
classified so via `TransmitFatalExceptionEvent`); the debug mirror records them as
`"(exception) {Type}"` with `ExceptionType`/`Message`/`IsFatal` props. `UnhandledException` is
the server-side counterpart for LSP.Core exceptions.

**Analytics use.** Error-rate by component: exception-type histogram, fatal vs normal split,
“[normal error]” classification (`IsNormalError` — expected exception families excluded from
fatal alerts). The scrubbed message histogram ranks the most frequent *kinds* of failure
without leaking paths.

### `PerfSample` (sampled, opt-in)
| | |
|---|---|
| **Emitter** | `OperationDurationRecorder` — wired into nearly every interactive LSP handler |
| **When** | Each instrumented operation completes; emission gated by `IPerfTelemetrySampler` (`REQNROLL_PERF_TELEMETRY_SAMPLE`, fraction in `[0,1]`, default `0` = off) |
| **Properties** | `Operation` (label, e.g. `textDocument/completion#step`; completion is also split into `#keyword` and, nested inside it whenever the tag branch runs, `#tag`), `DurationMs` (rounded ms), `DurationBucket` (`<=50`, `51-100`, …); per-IDE breakdown uses the canonical `IdeClient` stamped on every event (§1; formerly a `PerfSample`-only `IDEClient`) |

**Analytics use.** Real-world P95/P99 per operation per IDE — the field half of the performance
verification program (Layer 4). No URIs or content, ever.

---

## 7. Retired / deliberately absent events

| Event | Status | Why |
|---|---|---|
| `Feature file parsed` | Removed (`MonitorParserParse` deleted) | VS no longer parses `.feature` files locally — parsing moved server-side, uniformly, where `PerfSample` timing replaces it (issue #255/#259) |
| `Reqnroll Generation executed` | Removed (`MonitorReqnrollGeneration` deleted) | The single-file code-behind generation feature it monitored has no implementation anywhere in the product; the event would never fire |
| `Notification shown` / `Notification dismissed` | Not carried over | No notification system in the current extension (commented out in the interface — do not resurrect without the feature) |
| `CommandAutoFormatTable` (a.k.a. on-type table formatting) | Never implemented | Fires on every keystroke inside a table — not a discrete user command; perf sampling covers it (`PerfTargets.OnTypeFormatting`) |
| `Completion inserted` | Deferred | Standard LSP gives the server no signal when a completion item is accepted; a future VS-client commit hook could emit it (see archived build plan §4.1) |

---

## 8. At a glance — event → metric map

| Metric | Events feeding it |
|---|---|
| Binding-discovery reliability / failure rate | `ReqnrollDiscoveryExecuted` (`IsFailed`, `ErrorMessage`) |
| Build churn (no-op rediscoveries) | `ReqnrollDiscoveryExecuted` (`HashMatched=true`) |
| Solution/project scale | `OpenProject command executed` (`FeatureFileCount`), `Project loaded`, discovery counts |
| Project profile, all IDEs (Reqnroll version, TFM, language, connector type) | `OpenProject command executed` (`ProjectTargetFramework`, `ProgrammingLanguage`) + `ReqnrollDiscoveryExecuted` (`ReqnrollVersion`, `ConnectorType`) |
| Command usage & adoption | all `* command executed` / `* command offered` events |
| Step Rename failure modes | `Rename step command executed` (`Erroneous`, `Reason`) |
| Picker UI trigger rate | `RenameTargetsResolved` (`TargetCount > 1` share) |
| Go-to-hooks: lookups vs navigations | `FindHooks command executed` (server) vs `GoToHook command executed` (clients) |
| Run CodeLens: resolves vs actual runs | `ResolveTestTargets…` (lookups) + `TestOutcomesRunCompleted` (completions) |
| Crash/error rates | `UnhandledException` (server) + VS `ExceptionTelemetry` |
| Adoption lifecycle | `Extension installed`, `Extension upgraded`, `"{N} day usage"`, wizard events |
| Field performance (P95/P99) | `PerfSample` |
| Tag-index cold-scan size and cost | `TagIndexFirstScanCompleted` (`FileCount`, `FilesParsedFromDisk`, `DurationMs`); steady state via `PerfSample` `textDocument/completion#tag` |

---

## 9. Related open questions (not decisions)

- **#583 — what should Reqnroll IDE telemetry collect?** The overall strategy thread this
  inventory feeds.
- **#620 — should `UnhandledException` include stack traces?** Currently it does not
  (type + scrubbed message only). Adding traces has privacy/volume implications.
- **#621 — VS Code/Rider have no telemetry path for exceptions in their own client-side code.**
  Server exceptions reach telemetry via `UnhandledException`; a client-side exception path is not
  yet built.
- **#258 — `ProjectCharacteristics` event** (step/binding/hook/transformation counts as a
  snapshot) — proposed, not implemented.
