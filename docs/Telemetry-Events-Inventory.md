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
   navigation). VS Code and Rider similarly originate `GoToHook command executed` and
   `TagLink command executed` themselves.

Connection override (issue #889): `REQNROLL_DEBUG_TELEMETRY_CONNECTION_STRING`, an Application Insights
connection string (`InstrumentationKey=...[;IngestionEndpoint=...]`), replaces the built-in
connection in all three hosts (VS via `TelemetryConnectionOverride`, VS Code via
`resolveConnectionString`, Rider via `resolveConnection`) so developers can send to their own
resource. Unset uses the built-in connection; a value with no `InstrumentationKey` is logged and
ignored. `REQNROLL_TELEMETRY_ENABLED` still takes precedence. The LSP server never transmits, so it
needs no change.

Development-build guard (issue #889): a development build with no usable override sends **nothing**
rather than falling back to the production resource: a Debug build of the VS extension
(`#if DEBUG`, `TelemetryTransmitter.ApplyDebugBuildGuard`), VS Code running from source
(`ExtensionMode.Development`, i.e. F5), and Rider's `runIde` sandbox (`reqnroll.devSandbox`). The
event is still mirrored to the local debug log as not transmitted, and a released build is never
affected. To exercise telemetry from a dev build, set `REQNROLL_DEBUG_TELEMETRY_CONNECTION_STRING`.

Gate: `REQNROLL_TELEMETRY_ENABLED` (unset or `1` = on, anything else = off) is the cross-IDE
kill switch, honored by all three hosts (VS via `EnableTelemetryChecker`, Rider in
`RiderTelemetryTransmitter.transmit`, VS Code in its transmitter). VS Code *additionally* honors
`telemetry.telemetryLevel`; Visual Studio and Rider do not currently consult their IDE's own
telemetry settings (tracked as a follow-up). Debugging: `REQNROLL_TELEMETRY_DEBUG_LOG` mirrors every
event (server- and host-side) to a local JSONL file — see the archived
`docs/Archive/build-plan-telemetry-capture.md` §8.

**Delivery policy: best-effort, bounded, silent (issue #859).** Telemetry must be invisible when it
cannot be delivered (offline, DNS failure, blocked or black-holed endpoint): no exception reaches the
user, nothing on the UI thread or in the shutdown path waits on the network, and no retry storm or
unbounded queue builds up. How a host *detects* the failure differs, because its client API does:

| Host | Failure detection | User-visible |
|---|---|---|
| **VS** | none during the session. `Microsoft.ApplicationInsights` hands the event to a background sender that swallows the outcome (`CoreEventSource`/ETW only); non-2xx and timeout responses are not surfaced either. Only the flush in `DisposeAsync` can be observed. | **No notice** — deliberate: a host that cannot detect the failure must not claim it can. Events are simply lost. |
| **VS Code** | `telemetryCircuitBreaker.ts`: guarded `sendTelemetryEvent` plus a bounded reporter dispose. | One line on the first failure: `Telemetry endpoint unreachable; telemetry for this session will be dropped`. |
| **Rider** | `TelemetryCircuitBreaker.kt`: 5 s connect/request timeouts and non-2xx (403/407) counted as failures — Rider observes every send. | Same single line. |

Common to all three:

- **Dropped, not queued.** VS uses the SDK's `InMemoryChannel` (which is already the default channel):
  no on-disk buffer, so nothing is replayed later. `MaxTelemetryBufferCapacity = 100` only sets how many
  items trigger an immediate send; the hard cap is the SDK's own `TelemetryBuffer.BacklogSize`
  (1,000,000 items), past which items are dropped rather than queued. VS Code and Rider drop on their
  own send paths.
- **No exception reaches the user, and no unobserved task exception is produced.** VS never reports a
  failed transmission as a new exception event (it would go to the same unreachable endpoint), and the
  flush it abandons at shutdown is observed explicitly.
- **Shutdown is never delayed by a flush that cannot succeed.** VS bounds the flush to 500 ms and no
  longer waits an unconditional extra second; VS Code bounds the reporter's dispose to 500 ms; Rider's
  send path is timeout-bounded.
- The `REQNROLL_TELEMETRY_ENABLED` kill switch and the local debug log
  (`REQNROLL_TELEMETRY_DEBUG_LOG`, #799) are unchanged. Caveat on the debug log: it records
  `transmitted:true` at *hand-off*, which is all VS can know — a `transmitted:false` with an error
  appears there only if the channel faults synchronously, not when the endpoint is merely unreachable.
  Rider and VS Code record the real outcome.

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
and absent before `initialize`, so the server does not use it as an IDE-version source: it records it in its startup log and, as `ClientVersion`, on `ServerSessionStarted` only (omitted when the client sent none).

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
| `StepDefinitionCount` / `HookCount` | int | Connector success: counts in the swapped-in registry. Roslyn (issue #845): counts in the first owning project's registry after the patch, so every variant that changes bindings carries them. (Step Argument Transformations are surfaced by the connector but not modeled by `ProjectBindingRegistry`, so deliberately not reported.) |
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
| **Emitters** | Client only (issues #898, #899): Visual Studio's `GoToStepDefinitionPresenter`; VS Code's `doGoToStepDefinition` (the "Reqnroll: Go to Step Definition" picker command). Rider has no such command and sends nothing |
| **When** | Visual Studio: each time the user runs Go To Definition (F12 or Ctrl+Click) in a `.feature` file, wherever the caret is. VS Code: each run of the picker command. VS Code's F12/Peek/Ctrl+click and Rider's go-to-declaration use `textDocument/definition`, which those IDEs also send on hover and which a client cannot tell apart from a navigation, so they are **not** reported as navigations (known gap) |
| **Properties** | `LocationCount` (int): navigable rows offered (0 when there was nowhere to go) |

**Analytics use.** Go to Step Definition navigation count for Visual Studio and VS Code's picker command;
the honest counterpart to `FindStepDefinitionsCommandExecuted`. The status mix (ambiguous, undefined,
unresolved) comes from `FindStepDefinitionsCommandExecuted`.

### `FindStepDefinitionsCommandExecuted`
| | |
|---|---|
| **Emitters** | `FindStepDefinitionsHandler` (`reqnroll/findStepDefinitions`, issue #757) and, for VS Code and Rider, `DefinitionHandler` (`textDocument/definition`, issue #899) |
| **When** | After a step is resolved at the cursor, for **every** request. Visual Studio sends the custom request for Go To Definition and for each Ctrl+hover that checks whether a word is navigable (issue #898); VS Code and Rider send `textDocument/definition` for F12/Ctrl+click and on hover. Read it as *lookups*, not navigations; the navigation is `GoToStepDefinitionCommandExecuted`. `DefinitionHandler` emits nothing for Visual Studio (`IdeBehaviours.NavigatesStepsViaFindStepDefinitions`): there `textDocument/definition` is mostly the editor's hover fall-through when our provider declined (Peek Definition also uses it and is not distinguishable, so it goes uncounted). Positions that are not on a step don't emit |
| **Properties** | `LocationCount` (int): navigable rows offered; `Status` (string): `Bound` (defined, at least one navigable row) \| `Ambiguous` \| `Undefined` (no defined binding) \| `Unresolved` (defined, but no binding source exists on this machine - issue #540); `Protocol` (string): `reqnroll/findStepDefinitions` or `textDocument/definition` |

**Analytics use.** Lookup volume and the step-status mix (all IDEs; split by `Protocol` or `IdeClient`): how often a hovered
or navigated step is ambiguous, undefined, or has no local binding source.

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
| **When** | The "Define step(s)" lightbulb action is *offered* (undefined steps present); not when the user picks it — see [`DefineStepsCommandExecuted`](#definestepscommandexecuted) |
| **Properties** | `UndefinedStepCount` (int), `ActionsOffered` (int, counted from the final post-filter/post-cap list) |

**Analytics use.** Undefined-step pressure (how often the quick fix is needed) and offer availability.
VS and Rider may request code actions on every caret move, so this is not a count of lightbulb opens.

### `DefineStepsCommandExecuted`
| | |
|---|---|
| **Emitter** | `DefineStepsTriggeredHandler` |
| **When** | A client runs the `reqnroll.defineStepsTriggered` command that every "Define step(s)" code action carries. Clients run a code action's command after applying its edit, so this is the user picking the quick fix (any of its variants). Not sent for hand-written definitions |
| **Properties** | none |

**Analytics use.** Acceptance rate = `DefineSteps command executed` / `DefineSteps command offered` (offers are inflated by caret-move polling; compare trends, not absolutes). The same handler reveals the edited file in VS Code via `window/showDocument`, replacing the former client-side `vscode.open` command (a code action has one command slot).

### `InsertKeywordCommandOffered`
| | |
|---|---|
| **Emitter** | `CodeActionHandler` |
| **When** | One or more "Insert '\<keyword\>'" lightbulb actions are *offered* for a Gherkin parser error under the cursor; not when the user picks one — see [`InsertKeywordCommandExecuted`](#insertkeywordcommandexecuted) |
| **Properties** | `ActionsOffered` (int, counted from the final post-filter/post-cap list) |

**Analytics use.** How often syntax-error quick fixes are available. Clients may request code actions on every caret move, so this is not a count of lightbulb opens.

### `InsertKeywordCommandExecuted`
| | |
|---|---|
| **Emitter** | `InsertKeywordTriggeredHandler` |
| **When** | A client runs the `reqnroll.insertKeywordTriggered` command that every "Insert '\<keyword\>'" code action carries. Clients run a code action's command after applying its edit, so this is the user picking the quick fix (any keyword) |
| **Properties** | none |

**Analytics use.** Acceptance rate = `InsertKeyword command executed` / `InsertKeyword command offered` (offers are inflated by caret-move polling; compare trends, not absolutes).

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
| **Properties** | `Mode` (`"Toggle"` \| `"Comment"` \| `"Uncomment"` — the mode the client *requested*), `ResolvedMode` (`"Comment"` \| `"Uncomment"` — the direction the request actually took, issue #861; equals `Mode` unless `Mode` is `Toggle`, where it records whether the toggle added or removed comments. It is the service's decision (`GherkinCommentToggleResult.Uncommented`), reported even when no edit was needed, e.g. an `Uncomment` over uncommented lines), `LineCountBucket` (string — lines the command covered; see "Bucket schemes" below) |

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

### `ServerSessionStarted` / `ServerSessionEnded` (issue #845)
| | |
|---|---|
| **Emitter** | `ServerSessionTelemetry` (server). Identical from VS, VS Code and Rider; no per-IDE code |
| **When** | `ServerSessionStarted`: once, from the server's `OnStarted` (after the LSP `initialized` notification). `ServerSessionEnded`: once, best-effort, when the LSP `shutdown` request arrives — subscribed after `FeatureUsageSummary`'s final flush, so the final summary precedes it — or, when the client sends `exit` without a preceding `shutdown`, from a post-exit fallback (which may be lost if the transport is already closed) |
| **Properties** | Started: `ClientVersion` (the client's self-reported `ClientInfo.Version`; client-controlled text, so sent only when it matches `^[A-Za-z0-9_.\-+]{1,32}$`, otherwise omitted), `OperatingSystem` (`Windows`/`macOS`/`Linux`/`Other`), `Architecture` (`X64`/`Arm64`/...), `Runtime` (.NET framework description), `StartupMs` (server process launch to LSP handshake complete: it includes time spent waiting for the client's `initialize`/`initialized`, so it is an upper bound on the server's own startup cost). Ended: `SessionSeconds`. Both also carry the stamped `IdeClient`, `ServerVersion` and `SessionId` |

**Analytics use.** Per-IDE active-session counts (`IdeClient`), startup-time distribution by IDE and
OS/arch (field data for the ~14.6 s cold-start finding), and session length. `ServerSessionEnded` is
lost when the process dies abruptly, so a `SessionId` with a Started but no Ended event is a *hint* of an
abnormal end (crash, force-quit, IDE kill), not proof: an `exit` without `shutdown` or a closed transport
can also drop the Ended event, so treat it as an upper bound and corroborate with the client-side failure events.

### `ProjectCharacteristics` (issue #845, from #258)
| | |
|---|---|
| **Emitter** | `ConnectorBindingRegistryProvider` (server) |
| **When** | After each successful connector discovery run that changed the bindings — the same trigger point as `ReqnrollDiscoveryExecuted`, so it re-fires on every such build with no "already sent" state. A hash-no-op run sends none (nothing changed). A separate event so the discovery event's schema stays stable |
| **Properties** | `StepDefinitionCount`, `HookCount` (int); `StepBindingClassCount` (int: distinct declaring classes across step definitions and hooks — the method identity cut at the first `(` and then minus its last `.`-segment; class names are never sent. The connector builds identities as namespace-less `{ShortTypeName}.{Signature}` (e.g. `Steps.SetFirstNumber(Int32)`), so on that path same-named classes in different namespaces merge — an accepted undercount; Roslyn identities are `Namespace.Class.Method` and are not merged); `HookCount_<HookType>` (int, flat per-type keys such as `HookCount_BeforeScenario`, only for types present); `FeatureFileCount` (int, from the link-aware membership index — linked files count, `bin`/`obj`/`node_modules` do not; **omitted** until the project's `reqnroll/projectFiles` baseline has arrived, never sent as zero); `ProjectTargetFramework` (string); an undefined hook type is folded into `HookCount_Unknown`; `UnitTestFramework` (`MSTest`/`xUnit`/`NUnit`/`TUnit`/`Multiple`) and `TestPlatform` (`VSTest`/`MTP`), inferred from the project's package references (issue #874; both **omitted** when unknown, e.g. Rider sends no package references — see below) |

**Volume.** One event per project per successful, binding-changing connector run, so a full rebuild of an
N-project solution emits about N extra events (no per-project rate limit; the payload is a handful of ints).
If this proves noisy, sample or debounce per project.

Excluded on purpose (maintainer decision on #258): step-occurrence count (needs a full scan),
step-argument-transformation count (the connector reports it but `RunDiscovery` drops it — add once
transformations are discovered for other reasons) and step-definition reuse ratios (would force a
full scan just for telemetry; consider a separate event emitted only when a usages search runs anyway).

**Analytics use.** Project-size distribution (steps, hooks, binding classes, feature files) per TFM,
and how hook usage splits by type.

### Bucket schemes (issue #849)

Characteristic properties are counts, flags, small closed enums or *buckets* — never paths or text.
Key names live in `TelemetryProperties` (LSP.Server); bucketing in `TelemetryBuckets`.

| Property | Scheme |
|---|---|
| `DurationBucket` | Same scheme as `PerfSample`'s: `<=10`, `<=25`, `<=50`, `<=100`, `<=250`, `<=500`, `<=1000`, `<=5000`, `>5000` (ms of handler wall-clock time) |
| `LineCountBucket`, `DocumentLineBucket` | `0`, `1`, `2-10`, `11-50`, `51-200`, `201-1000`, `1000+` |

**Not yet implemented from issue #849**: `DefineSteps` properties are tracked separately in #847.
(The client-side `Source` on `GoToHookCommandExecuted` and the resolved comment mode landed in #861.)

---

## 4. Lifecycle & project events (Visual Studio host)

`Extension loaded`, `Extension installed`, `Extension upgraded` and `"{N} day usage"` are emitted by all
three IDEs (see the decision below, issue #875); the remaining events in this section are VS-only. The VS
events are all emitted by `VSSDKIntegration/Telemetry/TelemetryService.cs` (`Monitor*` methods) through
`ITelemetryTransmitter`. Events that fire before the LSP server starts (install, upgrade) can
only originate here.

| Event | Emitter / when | Properties |
|---|---|---|
| `ExtensionLoaded` | `MonitorOpenProjectSystem` — on extension activation in an IDE scope | — |
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

**Decision (issue #875, superseding the #845 "derive, do not port" call): port.** VS Code and Rider
emit the same four lifecycle events as Visual Studio, with identical names and semantics, so all three
IDEs expose the same install -> upgrade funnel and daily-use retention signal directly:

| Event | Fires | Properties |
|---|---|---|
| `Extension loaded` | every activation (VS Code: extension activation; Rider: first project startup of an IDE process) | — |
| `Extension installed` | first activation with no stored lifecycle state | — |
| `Extension upgraded` | stored version is lower than the running version (a downgrade sends nothing) | `OldExtensionVersion` |
| `"{N} day usage"` | first activation of each new local calendar day after install (not on the install day), `N` = running count of such days | — |

Client state: VS Code keeps `{ installedVersion, lastUsedDate, usageDays }` under the `globalState` key
`reqnroll.telemetry.lifecycle` (`src/VSCode/src/extensionLifecycleTelemetry.ts`); Rider keeps the same
three values in the application-level `PropertiesComponent` under `net.reqnroll.idesupport.telemetry.*`
(`ExtensionLifecycleTelemetry.kt`). The decision logic is a pure function/class per IDE, ported from VS's
`WelcomeService`, and unit-tested. Because state is new, installs that predate this release report
`Extension installed` once on first activation after upgrading to it; filter by `ExtensionVersion` when
counting true new installs. `ai.user.id` remains per IDE, so one person using two IDEs counts as two users.
`ServerSessionStarted` (from all IDEs) stays available as a cross-check, and the VS-only events are no
longer VS-only.

**Retired: the VS-host `Project loaded` event** (issue #873). The server's `OpenProject command
executed` + `ReqnrollDiscoveryExecuted` + `ProjectCharacteristics` cover Reqnroll version, TFM,
language and counts, so the VS-only duplicate (and its double-count of project opens) is gone.
Decision (#873): reporting the Reqnroll version on discovery is sufficient; nothing was added to
`OpenProject command executed`. `LegacySpecFlow` and `SingleFileGeneratorUsed` are not available
server-side (the connector does not expose them); they remain on the VS `Feature file opened`,
`Feature file added` and `Reqnroll config added` events (see ¹), so they are now per-event rather than
per-project-open signals.

**Analytics use.** Adoption lifecycle: install→upgrade funnel, daily-active heartbeat (rollup
`* day usage` by count), project-scale signals (ReqnrollVersion/TFM distribution,
SpecFlow migration), wizard abandonment (Started vs Completed) and framework-choice split,
link/content engagement on the welcome/upgrade surfaces.

---

## 5. Client-originated navigation events

`GoToStepDefinitionCommandExecuted` (§ above) is also client-originated, in Visual Studio (issue #898) and, for its picker command, VS Code (issue #899; constant mirrored in `telemetryEvents.ts`). Rider has no Go to Step Definition command, so it sends none.

### `GoToHookCommandExecuted`
| | |
|---|---|
| **Emitter** | VS `GoToHooksCommand`; VS Code `doGoToHooks` (`src/VSCode/src/commands/goToHooks.ts`); Rider `GoToHooksRunner` — three client copies of the same constant, per the catalog's mirror rule |
| **When** | A *genuine* "Go to Hooks" navigation command fires. Unlike the server's `FindHooksCommandExecuted` (every `reqnroll/findHooks` lookup, including CodeLens prefetch), only the client knows the command actually ran (issue #698) |
| **Properties** | `Source` (`"Command"` \| `"ContextMenu"` \| `"CodeLens"` — how the navigation was started, issue #861; same closed set and PascalCase key in all three IDEs) |

`Source` per IDE (constants: `GoToHookSources` in `Reqnroll.IdeSupport.Common`, `GoToHookSource` in `telemetryEvents.ts`, `GO_TO_HOOK_SOURCE_*` in `RiderTelemetryTransmitter`):

| IDE | `Command` | `ContextMenu` | `CodeLens` |
|---|---|---|---|
| VS Code | command palette, keybinding (no editor-menu argument) | editor right-click menu (VS Code passes the document `Uri` to `editor/context` commands) | hook-count CodeLens click |
| Rider | any action place other than the editor popup (shortcut, action search, main menu) | `ActionPlaces.EDITOR_POPUP` | hook-count CodeVision click |
| Visual Studio | never emitted | always, best-effort: the command's only placement is the editor context menu, but invocations from the Command Window, Tools > Customize and user-assigned keybindings also report `ContextMenu` | never emitted: CodeLens clicks share the Details-popup prefetch path (#698), so VS emits no `GoToHook` event for them. The shared enum therefore has a value VS never produces (a deliberate gap) |

`Source` is event-scoped: on `UnhandledException` the same key holds a class name (§6), on `GoToHookCommandExecuted` it holds the enum above.

**Analytics use.** True Go-to-Hooks navigation rate per IDE — the honest counterpart to the
server's lookup volume.

### `ServerStartFailed` / `ServerExitedUnexpectedly` / `ServerRestarted` (issue #845)
| | |
|---|---|
| **Emitter** | Each IDE client, because a dead server cannot report itself. VS: `LspServerConnectionService` (reported through `ServerLifecycleReporter`, which holds events until the host's transmitter is resolved); VS Code: `ServerLifecycleTelemetry` (`src/VSCode/src/lsp/serverLifecycleTelemetry.ts`, from the language client's state changes and a rejected `start()`); Rider: `ReqnrollServerLifecycleTelemetry` (from `LspServerManagerListener` state changes). Three copies of the same names, per the catalog's mirror rule |
| **When** | `ServerStartFailed`: the server could not be launched, or ended/failed its handshake before ever running. `ServerExitedUnexpectedly`: a running server stopped without the client asking it to (VS: process exited with no `shutdown`/`exit` handshake and the client not disposing). `ServerRestarted`: the client starts the server again after one of those, or after a clean end (VS: a new session, e.g. a solution swap; Rider: a restart after a normal shutdown) |
| **Properties** | `Reason` — a closed enum, never free text: `ExecutableNotFound` (VS only), `StartFailed`, `ProcessExited`, `SessionEnded` (a new start after the previous session ended cleanly: VS relaunch, Rider/VS Code restart after a normal shutdown; whether the user or the IDE initiated it is not observable, so they are not distinguished). `AttemptNumber` — 1-based start attempt in this IDE session (1 = the initial start) |

Client-side exception capture is separate (#621). A crash in the VS host process itself, or an IDE
killed outright, cannot be reported by anything and shows up only as a `ServerSessionStarted` with no
`ServerSessionEnded`.

**Analytics use.** Server start-failure and crash rate per IDE/OS (`Reason`), restart-loop detection
(`AttemptNumber`), and a count of users with no working server at all (failure events with no
`ServerSessionStarted` for the same user).

---

### `TagLinkCommandExecuted` (issue #755)
| | |
|---|---|
| **Emitter** | VS `TagLinkNavigableSymbolProvider` (via `TagLinkRedirect.LinkOpened`, wired in `ReqnrollLanguageClient`); VS Code `openTagLink` (`src/VSCode/src/lsp/tagLinks.ts`); Rider `ReqnrollFeatureTagLinkController` - three client copies of the same constant, per the catalog's mirror rule |
| **When** | The user follows a clickable Gherkin tag's link (Ctrl/Cmd+click) and the URL is handed to the browser. Only http(s) targets are opened, so a refused target emits nothing |
| **Properties** | none - the target URL and tag text come from repository configuration and are never sent |

The server only answers `textDocument/documentLink` (counted in `PassiveCounts` as `DocumentLink`), and
cannot tell a link being rendered from one being followed, so this event is client-originated. In VS Code
the middleware re-targets each link at the internal `reqnroll.openTagLink` command so the click reaches the
extension; VS and Rider request the links themselves because their generic LSP clients never send
`textDocument/documentLink`.

**Analytics use.** Tag-link adoption per IDE: `TagLink command executed` per session against
`PassiveCounts.DocumentLink` (links offered). Compare `DocumentLink` per IDE, not across IDEs: VS asks on every
Ctrl+hover, VS Code after each `workspace/codeLens/refresh` as well as on open/edit, and Rider on open/edit and
each inlay-hint refresh.

## 6. Error & perf events

### `UnhandledException` (server)
| | |
|---|---|
| **Emitter** | `LspErrorTelemetryService.MonitorError` — the LSP server's `ITelemetryService` implementation; every other `Monitor*` member is a no-op there |
| **When** | Any exception reported through `IErrorTelemetryService` (e.g. `IdeSupportGherkinParser`, `IdeSupportTagParser`, `CompletionContextResolver`, `WatchedFilesHandler` config loads), driven by `IdeSupportLoggerExtensions.LogException` |
| **Properties** | `ExceptionType` (full type name), `Message` (filesystem-path-scrubbed → `<path>`), `IsFatal` (bool, only when the caller classified it), `Source` (string, omitted when unknown — simple class name, no namespace, of the topmost stack frame inside a `Reqnroll.IdeSupport*` type, async/lambda helper types folded into their declaring class; the stack itself is not sent under this property), `StackFrames` (string, **proposed in #620, awaiting maintainer decision**; omitted when unknown — up to 8 newline-separated, innermost-first entries of `Namespace.Type.Method:line` for product frames only (`:line` is the integer source line from the shipped PDB, omitted when none resolves) (declaring type's namespace **and** assembly are both `Reqnroll.IdeSupport` or `Reqnroll.IdeSupport.*`; a foreign assembly reusing the namespace, or `Reqnroll.IdeSupportLookalike`, does not qualify; assembly names are never emitted); runs of frames from any other assembly collapse to `[external]`; no file paths, column numbers, parameter lists or generic arguments; compiler-generated lambda/async/local members normalised to `Method{lambda}`/`Method`/`Method{local}`; ≤1024 chars; sanitized by `ExceptionStackSanitizer`, also passed through `TelemetryScrubber`; attached only to the first occurrence of each distinct exception-type+stack per server session and to at most 25 distinct stacks per session — the event itself is still sent every time; the cap is exact under concurrency). Caveats: frames the JIT inlined are absent, and the `[external]` collapse plus the 8-entry cap can push deeper product frames out. Only the exception's own stack is read — never inner exceptions or the exception type name beyond the unchanged `ExceptionType` property. The surrounding event (`ExceptionType`, path-scrubbed `Message`, `IsFatal`, `Source`) is unchanged |

### `UnhandledException` (VS Code and Rider client code, #621)
| | |
|---|---|
| **Emitters** | VS Code: `clientExceptionTelemetry.ts` (`reportClientException`, `guardCommand`, `createReportingErrorHandler`); Rider: `ClientExceptionTelemetry.report`, called from `ReqnrollDebugLogger.warn`/`error` whenever they are given a throwable |
| **When** | VS Code: an exception escapes `activate` (`Source = Activation`), a Reqnroll command handler throws or rejects (`Source = Command:<commandId>`, e.g. `Command:reqnroll.goToHooks`; rethrown unchanged afterwards), or the language client's transport error callback fires (`Source = LanguageClientErrorHandler`; the client's default handler still decides continue/stop). Rider: every exception the plugin catches and logs at Warning or Error (LSP request failures in `ReqnrollRequestSender`, test-runner and MTP-stub failures, server-path resolution, gutter marks) |
| **Properties** | The server's schema, reused so one query covers every origin: `ExceptionType` (VS Code: JS error `name`/class name when it is a plain identifier, else `Error`, `NonError` for a thrown non-Error; Rider: full JVM class name), `Message` (scrubbed harder than the server's, see **Client message scrub** below, and capped at 128 chars), `Source` (VS Code: the closed set above; Rider: simple name of the topmost plugin class on the stack, nested/lambda types folded, omitted when none), plus **`ExceptionOrigin = "Client"`**, which server-originated events do not carry. `IdeClient` (`vscode`/`rider`) and the `Ide*`/`ExtensionVersion` identity keys are stamped by each host's transmitter like on every event (#844). `IsFatal` is never sent. `SuppressedCount` (plain integer, only when > 0) is the number of events dropped as duplicates or by the session cap since the last one sent; it rides on the next event that is sent, so events dropped after the cap is reached are not visible |
| **Limits** | Per session: each distinct (source, type, message) is sent once, at most 20 events in total (Rider also guards against re-entrancy); cancellation exceptions are never reported (this includes Rider's request-timeout cancellations, e.g. a rename timeout: a timeout is not a plugin failure); an LSP error response (`ResponseError` in VS Code, `ResponseErrorException` in Rider, also as a cause) is never reported, because the server already reports its own failures and the text can embed step or expression text; in VS Code an error raised by the `editor.action.rename` command that the F2/rename commands fall through to is rethrown but not reported (it is VS Code's, not ours); every reporting step is wrapped so telemetry cannot throw into, or block, the code it observes (Rider hands the send to a pooled thread). Gating: `REQNROLL_TELEMETRY_ENABLED` in both clients, plus VS Code's own `telemetry.telemetryLevel`; **Rider (like VS) has no IDE-level telemetry opt-out**, only the env var |
| **Not sent** | Stack traces (#620 is open: only the `Source` attribution derived from the stack is sent), paths, and anything beyond the scrubbed message |

**Client message scrub.** Client exception text is arbitrary (Node, VS Code and JVM errors quote file names, user directories,
URLs with tokens and parser snippets), so `Message` is scrubbed more strictly than the server's: every double-quoted span (first to last quote
on the line, because V8 embeds JSON snippets unescaped) and every backtick or multi-character single-quoted span becomes
`<text>`, so a `JSON.parse` snippet cannot leak; URLs become `<url>` (query string and fragment included); absolute drive-letter, UNC and POSIX
paths (directory names may contain interior spaces, so `C:\Users\John Smith\x` is one path), `~`/`%VAR%`/`$VAR` paths, `./` and `../` paths and bare file
names or relative paths ending in a source/config extension (`.feature`, `.cs`, `.csproj`, `.json`, `.md`, ...) become `<path>`; and the result is capped at
128 characters, which also bounds anything the rules miss. The server's `TelemetryScrubber.RedactPaths` got the same URL/path hardening (not
the quote drop or cap, which would change its `ErrorMessage` semantics). Known limits: a file name with spaces and no
directory (`My File.feature`) keeps its first word; a bare name with an extension outside the list is not recognized.

**Decision: reuse the `UnhandledException` event rather than add one.** The property schema is identical, so
existing error-rate and exception-type queries keep working; `ExceptionOrigin` (absent = server) separates the layers
and `IdeClient` the IDE. **Coverage:** VS Code covers activation, Reqnroll commands and the language-client transport
error callback; it does *not* cover event handlers/providers outside those commands (e.g. CodeLens providers, the
test-outcomes service, fire-and-forget promises), nor exceptions other extensions throw. Rider covers exceptions the plugin
catches and logs; it does *not* cover exceptions the plugin never catches (those reach the IntelliJ platform's own error
reporting, not ours). A failed server start/handshake and an unexpected server exit are server lifecycle (#845), not client
exceptions, and are deliberately not duplicated here. The VS host already reports its own exceptions (next section).

### VS host exception transmission (not an event name)
The VS host transmits exceptions with Application Insights' `ExceptionTelemetry` (fatal when
classified so via `TransmitFatalExceptionEvent`), built from sanitized parts only, never from the raw
exception (#1027): the full type name, the message path/URL-redacted by `TelemetryPathRedactor` (Common;
the rules behind the server's `TelemetryScrubber.RedactPaths`), and a stack reduced by
`ExceptionStackSanitizer` (product frames as `Namespace.Type.Method:line`, everything else `[external]`,
empty when no product frame is on it; no file paths); inner exceptions are not sent. The problem id is
`{Type} at {Source}` (simple product class name) or just `{Type}`. The debug mirror records them as
`"(exception) {Type}"` with `ExceptionType`/`Message`/`IsFatal` props (raw message, local only). `UnhandledException` is
the server-side counterpart for LSP.Core exceptions.

**Analytics use.** Error-rate by component: exception-type histogram, fatal vs normal split,
“[normal error]” classification (`IsNormalError` — expected exception families excluded from
fatal alerts). The scrubbed message histogram ranks the most frequent *kinds* of failure
without leaking paths.

### `PerfSample` (sampled, on by default at 5%)
| | |
|---|---|
| **Emitter** | `OperationDurationRecorder` — wired into nearly every interactive LSP handler |
| **When** | Each instrumented operation completes; emission gated by `IPerfTelemetrySampler` (`REQNROLL_PERF_TELEMETRY_SAMPLE`, fraction in `[0,1]`, default `0.05` = 5%; `0` disables sampling). `REQNROLL_PERF_TELEMETRY_SAMPLE` accepts a fraction in `[0,1]`; `0`/`off`/`false`/`no` disable it, unset/empty/`on` use the default, and an invalid value (not a number, NaN, infinite, or > 1) falls back to the default with a Warning in the server log. Housekeeping labels (`internal/featureRescan`, `workspace/*/refresh`) are never sent. The `REQNROLL_TELEMETRY_ENABLED` kill switch applies in every IDE; `telemetry.telemetryLevel` additionally in VS Code |
| **Properties** | `Operation` (label, e.g. `textDocument/completion#step`; completion is also split into `#keyword` and, nested inside it whenever the tag branch runs, `#tag`), `DurationMs` (rounded ms), `DurationBucket` (`<=50`, `51-100`, …); per-IDE breakdown uses the canonical `IdeClient` stamped on every event (§1; formerly a `PerfSample`-only `IDEClient`) |

**Analytics use.** Real-world P95/P99 per operation per IDE — the field half of the performance
verification program (Layer 4), collected by default at a 5% sample rate. No URIs or content, ever.

**Volume (estimate, not measured).** Roughly 100-300 `PerfSample` events per hour per actively
editing user at 5%, from the interactive handlers (completion, semantic tokens, text sync, ...).
Monitor the daily event count and per-`Operation` share after release; if volume or cost is a
problem, lower `DefaultSampleRate` or drop chatty labels in `OperationDurationRecorder.IsSampledForTelemetry`.

### `FeatureUsageSummary` (aggregated)
| | |
|---|---|
| **Emitter** | `FeatureUsageFlushService`, draining `FeatureUsageCounters`; counters are incremented from `OperationDurationRecorder.Record` for operations in `FeatureUsageCatalog` (issue #582) |
| **When** | Every flush interval, plus once on the LSP `shutdown` request (`IsFinal=true`); a window in which nothing was counted sends nothing. **On by default**, every 10 minutes (`FeatureUsageFlushService.EnabledByDefault`/`DefaultInterval`); `REQNROLL_FEATURE_USAGE_FLUSH_INTERVAL_SECONDS` overrides the interval (a non-positive value disables the event). The `REQNROLL_TELEMETRY_ENABLED` kill switch applies as for every event |
| **Properties** | `LookupCounts` and `PassiveCounts` (string - compact, key-sorted JSON object of feature key to count, e.g. `{"CodeAction":3,"Completion.Step":41}`; a kind with no counts is omitted), `WindowSeconds` (seconds covered by this window), `SessionSeconds` (seconds since the flush service started, approximately server uptime), `Sequence` (long - 1, 2, 3... per `SessionId`, advanced only by emitted events, so a gap marks a lost flush), `IsFinal` (bool - the shutdown flush); identity (`IdeClient`, `ServerVersion`, `SessionId`) is stamped like every server event (section 1) |

Counts are sent as a JSON *string*, not a nested object, because the three IDE forwarders
stringify values differently (VS Code `String(value)` gives `[object Object]`, Rider `toString()`
gives `{k=1.0}`, VS `JToken.ToString()` gives multi-line JSON); a plain string passes through all of
them unchanged. Parse with `parse_json(tostring(customDimensions.LookupCounts))` in Application Insights.

**Counted features** (`FeatureUsageCatalog`; keys are fixed identifiers, the closed set is the privacy guarantee):

| Kind | Keys | Recorded operation |
|---|---|---|
| `Lookup` - requested by the editor on a gesture or typing | `Completion.Step`, `Completion.Keyword`, `Completion.Tag` (a *subset* of `Completion.Keyword`), `Completion.Other`, `CodeAction` (also fires on cursor moves in VS/VS Code, not only on click) | `textDocument/completion#step`/`#keyword`/`#tag`/bare, `textDocument/codeAction` |
| `Passive` - requested by the editor on its own schedule | `CodeLens`, `InlayHint`, `FoldingRange`, `DocumentLink`, `DocumentSymbol`, `OnTypeFormatting` (the aggregate stand-in for the never-implemented `CommandAutoFormatTable`) | `textDocument/codeLens`/`inlayHint`/`foldingRange`/`documentLink`/`documentSymbol` (+ `reqnroll/documentSymbolHierarchical`)/`onTypeFormatting` |

**Not counted here, by design:** every discrete command (Go to Step Definition, Find Usages, Rename,
Find Unused, Comment/Uncomment, Format, Run lens lookups, Find Hooks, Go to Matching Scenarios).
They send their own per-call events with characteristic properties (#849), so counting them again
would double-count each invocation; their adoption comes from those events. Plumbing (document sync,
semantic tokens, diagnostics publication, refresh notifications) says nothing about feature use.
The membership is a strawman pending issue #583 / #850.

**Analytics use.** How often the volume features are exercised per session, per IDE, per version;
`LookupCounts`/`PassiveCounts` divided by `SessionSeconds` gives a rate comparable across sessions of
different length. Idle sessions send nothing, so pair with `SessionId`-bearing events (or
`ServerSessionStarted`, once #845 lands) when a session denominator is needed. Window loss on an
abrupt process death is accepted but detectable via `Sequence`.

---

## 7. Retired / deliberately absent events

| Event | Status | Why |
|---|---|---|
| `Feature file parsed` | Removed (`MonitorParserParse` deleted) | VS no longer parses `.feature` files locally — parsing moved server-side, uniformly, where `PerfSample` timing replaces it (issue #255/#259) |
| `Reqnroll Generation executed` | Removed (`MonitorReqnrollGeneration` deleted) | The single-file code-behind generation feature it monitored has no implementation anywhere in the product; the event would never fire |
| `Notification shown` / `Notification dismissed` | Not carried over | No notification system in the current extension (commented out in the interface — do not resurrect without the feature) |
| `CommandAutoFormatTable` (a.k.a. on-type table formatting) | Never implemented as an event | Fires on every keystroke inside a table — not a discrete user command; perf sampling covers latency (`PerfTargets.OnTypeFormatting`) and `FeatureUsageSummary`'s `OnTypeFormatting` count covers volume |
| `Completion inserted` | Deferred | Standard LSP gives the server no signal when a completion item is accepted; a future VS-client commit hook could emit it (see archived build plan §4.1). Spike #883 prototype: the server attaches `CompletionItem.Command` `reqnroll.completionAccepted` to *step* items and counts each ack as `Completion.Step.Accepted` in `FeatureUsageSummary`; per-IDE behaviour (invoked on accept, dropped/duplicated, latency) still to be verified live. Completion *requests* are already counted via `FeatureUsageSummary` (`Completion.*`), so there is no separate per-request completion event |

---

## 8. At a glance — event → metric map

| Metric | Events feeding it |
|---|---|
| Binding-discovery reliability / failure rate | `ReqnrollDiscoveryExecuted` (`IsFailed`, `ErrorMessage`) |
| Build churn (no-op rediscoveries) | `ReqnrollDiscoveryExecuted` (`HashMatched=true`) |
| Solution/project scale | `OpenProject command executed` (`FeatureFileCount`), discovery counts |
| Project profile, all IDEs (Reqnroll version, TFM, language, connector type) | `OpenProject command executed` (`ProjectTargetFramework`, `ProgrammingLanguage`) + `ReqnrollDiscoveryExecuted` (`ReqnrollVersion`, `ConnectorType`) |
| Command usage & adoption | all `* command executed` / `* command offered` events |
| Step Rename failure modes | `Rename step command executed` (`Erroneous`, `Reason`) |
| Picker UI trigger rate | `RenameTargetsResolved` (`TargetCount > 1` share) |
| Go-to-hooks: lookups vs navigations | `FindHooks command executed` (server) vs `GoToHook command executed` (clients) |
| Go-to-step-definition: lookups vs navigations | `FindStepDefinitions command executed` (server; all IDEs) vs `GoToStepDefinition command executed` (client; Visual Studio and VS Code's picker command only - F12/Ctrl+click in VS Code and Rider are not distinguishable from hover) |
| Run CodeLens: resolves vs actual runs | `ResolveTestTargets…` (lookups) + `TestOutcomesRunCompleted` (completions) |
| Crash/error rates | `UnhandledException` (server; VS Code/Rider client code with `ExceptionOrigin = "Client"`) + VS `ExceptionTelemetry` |
| Adoption lifecycle | All IDEs (#875): `Extension loaded`, `Extension installed`, `Extension upgraded`, `"{N} day usage"`; VS only: wizard events; `ServerSessionStarted` by distinct `ai.user.id` as a cross-check |
| Server health: start failures, crashes, restarts, time-to-ready | `ServerStartFailed`, `ServerExitedUnexpectedly`, `ServerRestarted` (clients); `ServerSessionStarted` (`StartupMs`); Started without Ended per `SessionId` |
| Project size snapshot (steps, hooks per type, binding classes, feature files) | `ProjectCharacteristics` |
| Field performance (P95/P99) | `PerfSample` |
| Volume/passive feature usage (completion, code actions, CodeLens, inlay hints, ...) | `FeatureUsageSummary` |
| Tag-index cold-scan size and cost | `TagIndexFirstScanCompleted` (`FileCount`, `FilesParsedFromDisk`, `DurationMs`); steady state via `PerfSample` `textDocument/completion#tag` |

---

## 9. Related open questions (not decisions)

- **#583 — what should Reqnroll IDE telemetry collect?** The overall strategy thread this
  inventory feeds.
- **#620 — should `UnhandledException` include stack traces?** **Proposal implemented, not yet
  decided:** a bounded, sanitized `StackFrames` property (see §6) — product frames reduced to
  `Namespace.Type.Method:line` (line from the shipped PDB), everything else `[external]`, first occurrence per stack per session
  only. Open for the maintainer: (a) accept/reject/narrow the property; (b) the issue discussion
  prefers fuller data (inner exceptions, unscrubbed paths since exceptions come only
  from our code); line numbers on product frames are included, the rest is deliberately *not* done here because the opt-out event stays counts/names-only;
  (c) the VS host `ExceptionTelemetry` path (`TransmitException`) previously let Application
  Insights serialize the full exception (stack with file paths, inner exceptions); since #1027 it sends
  the same sanitized stack and path-redacted message as the server path (see §6, VS host exception
  transmission), so whatever #620 decides applies to both.
- **#621 — client-side exception path for VS Code/Rider** is built (see `UnhandledException` (VS Code and
  Rider client code) in §6): activation, commands and the language-client error callback in VS Code, caught-and-logged
  exceptions in Rider. Stack traces stay out of the client events until #620 is decided (its `StackFrames` proposal currently covers the server path only).
- **#258 — `ProjectCharacteristics` event** — implemented in #845 (step/hook/binding-class/feature-file
  counts, hooks per type). Transformation counts and reuse ratios remain excluded (see its section).
- **`UnitTestFramework` / `TestPlatform`** (MSTest/xUnit/NUnit/TUnit, VSTest vs MTP) — implemented in #874 on
  `ProjectCharacteristics` by `UnitTestFrameworkDetector`, from the package references the client sends (the
  connector does not report them). The Reqnroll adapter package (`Reqnroll.MsTest`, ...) decides the framework;
  without one the framework packages themselves do; differing frameworks give `Multiple`. Platform is best effort:
  TUnit is MTP, `Microsoft.NET.Test.Sdk` means VSTest, anything else is omitted — package references cannot see the
  MSBuild switches (e.g. `EnableMSTestRunner`) that opt MSTest/xUnit/NUnit into MTP, so a project on MTP that still
  references the VSTest SDK reads as `VSTest`. Both properties are omitted for clients that send no package
  references (Rider today).
- **`ReqnrollVersion` / `LegacySpecFlow` on `OpenProject command executed`** — decision (#845): the
  first `ReqnrollDiscoveryExecuted` is the authoritative source for the Reqnroll version (the connector
  resolves it; the server's project model does not), so it is not duplicated on `OpenProject`.
  `LegacySpecFlow` is not available server-side; per #873 it stays on the VS feature-file/config events
  (see the retired `Project loaded` note in section 4).
