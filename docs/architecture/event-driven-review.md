# Event-Driven Architecture Review — Reqnroll.IdeSupport LSP Server

**Date:** 2026-09-03
**Scope:** `src/LSP/Reqnroll.IdeSupport.LSP.Server` (in-process event bus), with its producers in
`src/LSP/.../Discovery`, `src/LSP/.../Workspace`, `src/LSP/.../Registry`, and its wire-level
producers in `src/VisualStudio`, `src/VSCode`, `src/Rider`.
**Status:** Analysis only. No code changed. Recommendations require approval before implementation.

---

## 0. Method and confidence

Evidence was gathered by exhaustive enumeration rather than sampling:

- every `INotification` declaration and every `INotificationHandler<T>` implementation in `src/`;
- every `IMediator.Publish` call site;
- every C# `event` declaration in `src/` and a subscriber search for each;
- every `reqnroll/*` custom protocol method (`Protocol/LspMethodNames.cs`) and its client-side
  producers;
- every call site of `IGherkinDocumentTaggerService.ParseAsync` and `IParseCoordinator`.

Navigation used the Serena MCP C# language server for symbol/reference work and `rg`/`find` for
literal enumeration (declaration keywords, method-name strings) that is cheaper to do textually.
Serena connected mid-session; the earlier enumeration passes were textual and were not re-run
symbolically, because the queries were literal-match by nature.

Where a claim below is inference rather than observation, it is marked **(inferred)**.

---

## 1. Event inventory and classification

Six distinct mechanisms carry "something happened" in this system. They are not one bus; treating
them as one is the first thing this review corrects.

### 1.1 MediatR in-process notifications — the actual event bus

These are the only three types published through `IMediator.Publish`.

| Event | Definition | Classification |
|---|---|---|
| `MatchCacheChangedNotification(DocumentUri Uri, int Version)` | `Pipeline/MatchCacheChangedNotification.cs:21` | **Fact.** Past-tense, immutable, carries an identity + version and no instruction. Consumers re-read state rather than receiving it. The cleanest contract in the system. |
| `BindingRegistryChangedNotification(LspReqnrollProject Project, bool IsFullReplacement, IReadOnlyCollection<string>? RemovedBindingFilePaths)` | `Pipeline/BindingRegistryChangedNotification.cs:27` | **Fact envelope carrying a command and a mode flag.** See §1.2. |
| `ReqnrollConfigChangedNotification(string WorkspaceRootPath)` | `Pipeline/ReqnrollConfigChangedNotification.cs:9` | **Fact,** with a contract defect: the parameter is named `WorkspaceRootPath` but both producers pass a *project folder* (`WatchedFilesHandler.cs:154`, `WatchedFilesHandler.cs:190`). |

### 1.2 `BindingRegistryChangedNotification` is three messages wearing one name

This is the central finding of the inventory. The type is named as a fact, but carries two
non-fact payloads:

- **`RemovedBindingFilePaths` is a command.** It is not a description of registry state; it is an
  instruction to the consumer to purge those paths, executed by
  `BindingRegistryChangedHandler.RemoveBindingFilesAsync` (`Pipeline/BindingRegistryChangedHandler.cs:176`).
  Only one of the four producers ever sets it (`Workspace/MembershipIndex.cs:91`).
- **`IsFullReplacement` is a dispatch discriminator.** It selects between two materially different
  consumer behaviours: `true` runs Roslyn `.cs` rediscovery + a whole-project closed-file scan +
  a code-lens refresh; `false` schedules a debounced rescan instead
  (`Pipeline/BindingRegistryChangedHandler.cs:110-143`). Two events are being multiplexed over one
  type, and the consumer demultiplexes with an `if`.

The four producers publish semantically different things under this one name:

| Producer | Site | Actually means |
|---|---|---|
| `BindingRegistryProviderRouter.OnProviderChanged` | `Registry/BindingRegistryProviderRouter.cs:202` | "the connector swapped the registry" (`true`) *or* "a Roslyn per-file patch changed an expression" (`false`) |
| `LspWorkspaceScopeManager.HandleProjectLoadedAsync` | `Workspace/LspWorkspaceScopeManager.cs:148` | "a deferred baseline re-scan is now due" |
| `MembershipIndex.HandleProjectFilesAsync` (baseline) | `Workspace/MembershipIndex.cs:137` | "project membership was replaced wholesale" |
| `MembershipIndex.HandleProjectFilesAsync` (delta) | `Workspace/MembershipIndex.cs:91` | "these binding files left the project; purge them" |

Three of those four are not registry changes at all. They are membership/lifecycle facts that
*borrow* the registry-changed event because it happens to trigger the reconciliation they want.

### 1.3 Types marked `INotification` that are never published

`ReqnrollProjectLoadedParams`, `ReqnrollProjectUnloadedParams`, and `ReqnrollProjectFilesParams`
all declare `: INotification` (`Workspace/ReqnrollProjectLoadedParams.cs:9`,
`ReqnrollProjectUnloadedParams.cs:9`, `ReqnrollProjectFilesParams.cs:10`) — but no
`INotificationHandler<T>` exists for any of them, and none is ever passed to `IMediator.Publish`.
They are routed directly to a method:

```
options.OnNotification<ReqnrollProjectLoadedParams>(
    LspMethodNames.ReqnrollProjectLoaded,
    (p, ct) => resolver!.Get<ILspWorkspaceScopeManager>().HandleProjectLoadedAsync(p, ct),
    serialOptions);                       // Hosting/LanguageServerOptionsExtensions.cs:119
```

**Classification: integration events (wire) + lifecycle signals; the `INotification` marker is
vestigial.** It advertises a pub/sub extension point that does not exist. A contributor who adds
an `INotificationHandler<ReqnrollProjectLoadedParams>` will get a class that compiles, registers
via the MediatR assembly scan, and is never invoked.

### 1.4 .NET `event` declarations (in-process, non-MediatR)

| Event | Site | Classification | Subscribers |
|---|---|---|---|
| `ConnectorBindingRegistryProvider.BindingRegistryChanged` | `Discovery/Connector/ConnectorBindingRegistryProvider.cs:133` | **Fact.** Raised at `:235` (Roslyn patch, `false`) and `:319` (connector run, `true`). | `BindingRegistryProviderRouter` (`Registry/BindingRegistryProviderRouter.cs:155`) |
| `ILspWorkspaceScopeManager.ProjectDiscovered` / `ProjectRemoved` | `Workspace/LspWorkspaceScopeManager.cs:84,86` | **Lifecycle signals.** | `BindingRegistryProviderRouter` ctor (`:78-79`) |
| `ILspWorkspaceScopeManager.ScopeOpened` / `ScopeClosed` | `Workspace/LspWorkspaceScopeManager.cs:43,45` | **Lifecycle signals — dead.** | **None.** Zero subscribers in `src/` or `tests/`. |
| `IIdeSupportConfigurationProvider.ConfigurationChanged` | `Common/ProjectSystem/Configuration/IIdeSupportConfigurationProvider.cs:10` | **Fact — orphaned.** Raised at `ProjectScopeIdeSupportConfigurationProvider.cs:97`. | **None live.** The only subscriber, `ProjectSettingsProvider.OnConfigurationChanged`, is commented out (`Common/ProjectSystem/Settings/ProjectSettingsProvider.cs:42,53,129`). |
| WPF/VS UI events (`PropertyChanged`, `TagsChanged`, `CanExecuteChanged`, …) | `src/VisualStudio/...Wizards.UI/*`, `...VSSDKIntegration/*` | **Implementation details.** Framework plumbing, out of scope. |

Two dead events with live raise paths is a small but real maintenance hazard: `CloseWorkspace`
invokes `ScopeClosed` on every workspace teardown, and `Reload()` invokes `ConfigurationChanged`
on every `reqnroll.json` edit, both to nobody.

### 1.5 Server → client push notifications (integration events, on the wire)

| Method | Producer | Notes |
|---|---|---|
| `textDocument/publishDiagnostics` | `Pipeline/DiagnosticsPublishHandler.cs:111` (.feature), `Pipeline/CSharpDiagnosticsPublisher.cs:58` (.cs), `Features/TextSync/TextDocumentSyncHandler.cs:192,217` (clear-on-close) | Full-set semantics: a partial push silently clears omitted diagnostics. |
| `reqnroll/semanticTokens` | `Pipeline/SemanticTokensPushHandler.cs:65` | VS-only push; every other client pulls. |
| `reqnroll/refreshCodeLens` | `CodeLensRefreshRequester` via `Pipeline/CodeLensRefreshHandler.cs` and `Pipeline/BindingRegistryChangedHandler.cs:140,152` | |

`workspace/semanticTokens/refresh`, `workspace/inlayHint/refresh`, and `workspace/codeLens/refresh`
are **requests**, not events — they are `SendRequest(...).ReturningVoid(ct)` round-trips
(`Pipeline/SemanticTokensRefreshHandler.cs:83`, `Pipeline/InlayHintRefreshHandler.cs:72`) and can
fail or be cancelled.

### 1.6 Client → server integration events

Produced by the three IDE clients, consumed by §1.3's direct routing:
`reqnroll/projectLoaded`, `reqnroll/projectUnloaded`, `reqnroll/projectFiles` (baseline | delta),
`reqnroll/documentActivated`. VS producers are `LspNotifications/VsProjectEventMonitor.cs:351,375,397,533`,
`LspNotifications/LspProjectPreloadPusher.cs:66,68`, and
`LspInterception/ScaffoldTrackingInterceptor.cs`.

### 1.7 Implementation details, correctly *not* events

`IParseCoordinator`, `IRefreshDebouncer`, `IFeatureRescanDebouncer` are scheduling primitives, not
message types. They are correctly modelled as services. Their documentation is unusually good and
should be preserved verbatim through any refactoring.

---

## 2. Producer → consumer map

### 2.1 `MatchCacheChangedNotification` — 4 producers, 4 consumers

**Producers** (all four are `ParseAsync` immediately followed by a publish):

| # | Site | Via `IParseCoordinator`? |
|---|---|---|
| P1 | `Features/TextSync/TextDocumentSyncHandler.cs:237` (didOpen/didChange) | **yes** (`:110`, `:159`) |
| P2 | `Pipeline/BindingRegistryChangedHandler.cs:363` (registry cascade) | **yes** (`:306`) |
| P3 | `Pipeline/ReqnrollConfigChangedHandler.cs:69` (reqnroll.json / .editorconfig) | **no** |
| P4 | `Features/DocumentActivated/DocumentActivatedHandler.cs:56` (VS tab activation) | **no** |

**Consumers** (fan-out, all `INotificationHandler<MatchCacheChangedNotification>`):

| # | Handler | Effect | Throws? |
|---|---|---|---|
| C1 | `Pipeline/DiagnosticsPublishHandler.cs:68` | aggregate + push `publishDiagnostics` | **yes** — no `try`/`catch` |
| C2 | `Pipeline/SemanticTokensPushHandler.cs:49` | encode + push `reqnroll/semanticTokens` (VS only) | **yes** — no `try`/`catch` |
| C3 | `Pipeline/SemanticTokensRefreshHandler.cs:66` | debounced `workspace/semanticTokens/refresh` | no (debouncer catches) |
| C4 | `Pipeline/InlayHintRefreshHandler.cs:57` | debounced `workspace/inlayHint/refresh` | no |
| C5 | `Pipeline/CodeLensRefreshHandler.cs:55` | debounced `reqnroll/refreshCodeLens` | no |

### 2.2 `BindingRegistryChangedNotification` — 4 producers, 2 consumers

Producers listed in §1.2. Consumers:

| # | Handler | Effect |
|---|---|---|
| C1 | `Pipeline/BindingRegistryChangedHandler.cs:90` | purge removed files → (full) Roslyn `.cs` rediscovery + closed-file scan → schedule open-file reparses → code-lens refresh |
| C2 | `Pipeline/CSharpDiagnosticsRegistryChangedHandler.cs:48` | re-push `.cs` binding diagnostics for every open owned `.cs` file |

### 2.3 `ReqnrollConfigChangedNotification` — 1 producer file, 1 consumer

Producer: `Workspace/WatchedFilesHandler.cs:154` (reqnroll.json) and `:190` (.editorconfig, once
**per project in scope**). Consumer: `Pipeline/ReqnrollConfigChangedHandler.cs:41`.

### 2.4 Full upstream chain

```
IDE client                     Server ingress                    Bus                         Egress
──────────                     ──────────────                    ───                         ──────
didOpen/didChange (.feature) → TextDocumentSyncHandler ────────→ MatchCacheChanged ────────→ publishDiagnostics
                               (via ParseCoordinator)             ├→ DiagnosticsPublish       reqnroll/semanticTokens
                                                                  ├→ SemanticTokensPush       workspace/semanticTokens/refresh
                                                                  ├→ SemanticTokensRefresh    workspace/inlayHint/refresh
                                                                  ├→ InlayHintRefresh         reqnroll/refreshCodeLens
                                                                  └→ CodeLensRefresh

didOpen/didChange (.cs) ─────→ TextDocumentSyncHandler
                               (via ParseCoordinator)
                                 → CSharpBindingDiscoveryService
                                   → ConnectorBindingRegistryProvider.ApplyRoslynFileUpdateAsync
                                     └[gated: HasExpressionChanges || HasHookChanges]
                                       → BindingRegistryChanged(false)  [.NET event]
                                         → Router.OnProviderChanged (FireAndForget)
                                           → BindingRegistryChanged ────┐
                                                                        │
bin/**/*.dll changed ────────→ WatchedFilesHandler                      │
                                 → provider.TriggerRefresh()            │
                                   → RunDiscoveryAsync (500ms debounce) │
                                     └[gated: assembly hash differs]    │
                                       → BindingRegistryChanged(true) ──┤
                                                                        │
reqnroll/projectLoaded ──────→ LspWorkspaceScopeManager                 │
                                 ├→ ProjectDiscovered → Router → new provider → TriggerRefresh
                                 ├→ TriggerBindingDiscovery (re-send / VS post-build)
                                 └→ deferred rescan → BindingRegistryChanged(true) ──┤
                                                                        │
reqnroll/projectFiles ───────→ MembershipIndex                          │
                                 ├→ baseline → BindingRegistryChanged(true) ─────────┤
                                 └→ delta    → BindingRegistryChanged(false, removed)┤
                                                                        │
                                                        ┌───────────────┘
                                                        ▼
                                          BindingRegistryChangedHandler
                                            ├→ RemoveBindingFilesAsync
                                            ├→ [full] RediscoverCsFilesAsync (notify:false)
                                            ├→ [full] ScanAllFeatureFilesAsync (closed files)
                                            ├→ [incr] FeatureRescanDebouncer (500ms)
                                            └→ ReparseOpenFilesAsync ──→ MatchCacheChanged (per file)
                                          CSharpDiagnosticsRegistryChangedHandler
                                            └→ CSharpDiagnosticsPublisher → publishDiagnostics

reqnroll.json changed ───────→ WatchedFilesHandler
                                 ├→ ReqnrollConfigChanged → reparse open files → MatchCacheChanged
                                 └→ TriggerBindingDiscovery (hash no-op unless rebuilt)

reqnroll/documentActivated ──→ DocumentActivatedHandler → ParseAsync → MatchCacheChanged
```

**There are no cycles.** The one place a cycle could form —
`BindingRegistryChangedHandler.RediscoverCsFilesAsync` calling back into the Roslyn discovery
service that raises `BindingRegistryChanged` — is explicitly broken by the `notify: false`
parameter (`Pipeline/BindingRegistryChangedHandler.cs:414`,
`Discovery/Connector/ConnectorBindingRegistryProvider.cs:180-224`). That gate is load-bearing and
its rationale is documented in the source; it must survive any refactoring.

---

## 3. Cross-cutting properties

### 3.1 Correlation and causation — absent

There is no correlation ID, causation ID, or trace context on any internal notification. The only
`traceparent` handling in the repo is in the VS-side wire inspector
(`src/VisualStudio/.../LspInterception/LspInspectorLogger.cs:125-141`), which reads a header off
JSON-RPC frames; it does not reach the internal bus.

Consequence, and it is the concrete one behind several past investigations: when a
`MatchCacheChangedNotification` for `Foo.feature v7` produces wrong diagnostics, the logs cannot
tell you whether that publish originated from a keystroke (P1), a build cascade (P2), a config
save (P3), or a tab switch (P4). Handlers log the URI and version
(`SemanticTokensRefreshHandler.cs:74`, `CodeLensRefreshHandler.cs:56`) but never the cause. The
`IOperationDurationRecorder` PERF lines are per-operation, not per-flow, so two concurrent
cascades interleave indistinguishably in one log file.

### 3.2 Persistence boundaries — none; everything is in-memory and process-lifetime

No database, no on-disk cache, no serialized state. State lives in:

- `ProjectBindingRegistry` behind `ConnectorBindingRegistryProvider._current` (`volatile`,
  guarded for read-modify-write by `_currentLock`, `ConnectorBindingRegistryProvider.cs:53`);
- `IBindingMatchService` (match sets, keyed by `(uri, ProjectOwner)`);
- `IDocumentBufferService` (.feature text + tags), `ICSharpFileTextCache` (.cs text);
- `MembershipIndex._membership` (`Dictionary` under `_membershipLock`, `Workspace/MembershipIndex.cs:26-28`).

**This is the right choice** and materially narrows the design space: there is no durable state to
reconcile, no outbox, no exactly-once concern, and process death is a total, acceptable reset (the
client re-sends `projectLoaded` + `projectFiles` and re-opens documents). Any recommendation that
implies durability is disproportionate here. Recorded explicitly because it is the single strongest
argument against a durable task framework in §5.

### 3.3 Idempotency — relied upon, never stated

Every consumer is in fact idempotent: they recompute from current state and push a full result.
`ScanAllFeatureFilesAsync` re-scans, `ReparseOpenFilesAsync` re-parses, `DiagnosticsPublishHandler`
re-aggregates, `CSharpDiagnosticsRegistryChangedHandler` re-pushes "every open `.cs` file
unconditionally on any change (not diffed first)" (`Pipeline/CSharpDiagnosticsRegistryChangedHandler.cs:28-32`).

This is a genuine architectural strength and it is what makes the choreography survivable. But it
is nowhere asserted. There is no test that pins "publishing X twice equals publishing it once", and
no comment on the notification types saying consumers must be idempotent. It is a load-bearing
invariant held only by consistent habit.

The nearest thing to a stated rule is the staleness short-circuit in
`Discovery/Roslyn/CSharpBindingDiscoveryService.cs:60-70`, which skips a superseded parse by
comparing against `ICSharpFileTextCache` — that is deduplication, not idempotency.

### 3.4 Retry — none, by design

No consumer retries. Failures are logged and dropped:

- `ParseCoordinator.RunSafelyAsync` catches everything and logs `LogWarning`
  (`Pipeline/ParseCoordinator.cs:126-129`) — deliberately, so a parse failure does not fault the
  task that `FoldingRangeHandler`/`DocumentSymbolHandler` await.
- `RefreshDebouncer.RunAfterDelayAsync` catches everything (`Pipeline/RefreshDebouncer.cs:43-50`).
- `FireAndForgetExtensions` logs `LogError` and drops (`Common/FireAndForgetExtensions.cs:32-42`).

Recovery is by *re-trigger*, not retry: the next keystroke, build, or tab activation produces a
fresh cascade. Given full-recompute idempotency this is coherent — but see §3.8 for the case where
it silently loses a user-visible result.

### 3.5 Ordering assumptions

Four distinct ordering mechanisms, each correct in isolation:

1. **Per-URI parse serialization** — `ParseCoordinator.Schedule` chains rather than cancels, under
   a plain `lock` (`Pipeline/ParseCoordinator.cs:34-104`). The `ConcurrentDictionary.AddOrUpdate`
   version of this was the #554 bug; the comment at `:14-22` records exactly why the lock replaced
   it. **Sound.**
2. **Project-lifecycle serialization** — `reqnroll/projectLoaded|projectUnloaded|projectFiles` are
   forced onto the Serial dispatch lane (`Hosting/LanguageServerOptionsExtensions.cs:104-118`) so
   client send-order is preserved. **Sound**, with the reasoning documented.
3. **Fan-out order within one notification** — MediatR 8 dispatches sequentially in DI registration
   order (assembly-scan order). `DiagnosticsPublishHandler`'s remarks state "No ordering guarantee
   between these handlers is required — they are independent" (`Pipeline/DiagnosticsPublishHandler.cs:24-25`).
   Verified: the five `MatchCacheChanged` consumers share no mutable state. **Sound**, but see §3.8.
4. **No global ordering between concurrent cascades.** Two `BindingRegistryChangedNotification`
   publishes for different projects run concurrently on thread-pool threads via `FireAndForget`.
   They converge on per-URI `ParseCoordinator` chains, so same-file work is serialized. **Sound.**

**The gap:** producers P3 (`ReqnrollConfigChangedHandler`) and P4 (`DocumentActivatedHandler`)
call `ParseAsync` directly, *not* through `IParseCoordinator` — see §4.1.

### 3.6 Timeout handling — none anywhere

No `IParseCoordinator` operation, no `_mediator.Publish`, no `ScanAllFeatureFilesAsync` has a
timeout. The only time-based control is debounce delay (500 ms in `RefreshDebouncer`,
`FeatureRescanDebouncer.cs:11`, `ConnectorBindingRegistryProvider.cs:31`), which delays work rather
than bounding it.

Concretely: `ScanAllFeatureFilesAsync` (`Pipeline/BindingRegistryChangedHandler.cs:208-262`) reads
and parses *every closed feature file in the project* with no cap on count, no cap on file size,
and no deadline. On the `VeryLargeFeature` corpus this is the known CPU-pegging path recorded in
issue #491. Its `CancellationToken` comes from `FireAndForget` → `CancellationToken.None`, so
nothing can interrupt it once started.

The outgoing refresh *requests* do honour cancellation — `SemanticTokensRefreshHandler.cs:83` and
`InlayHintRefreshHandler.cs:72` pass the debouncer's token into `SendRequest`, which is what issue
#471 fixed — but that is supersession, not a timeout.

### 3.7 Compensation / rollback — one instance, and it is a request, not an event

There is no compensating action for a failed cascade, and none is needed: state is in-memory and
recomputed, so there is nothing to roll back.

The single genuine compensation-shaped flow is rename:
`RenamePostApplyCoordinator.PushEditIfVisualStudioAsync` returns `false` when VS rejects the edit,
and the documented contract is that "callers must not touch server-side caches and must reject the
rename in that case, since the actual buffer/file content never changed"
(`Features/Rename/RenamePostApplyCoordinator.cs:59-65`). That is a real distributed-transaction
concern — but it is handled inside a **request/response** handler, synchronously, and never touches
the event bus. It is correctly *not* choreographed.

### 3.8 Error paths — the one real defect in the bus itself

MediatR 8's default publisher is a sequential `foreach { await handler(...); }` with **no exception
isolation**. The codebase already knows this — `BindingRegistryProviderRouter.cs:195-200` reasons
explicitly about "MediatR's default publisher awaits each handler in turn with no `Task.Run` in
between" to justify `FireAndForget`. The consequence for *faults*, however, is not addressed
anywhere:

> **If any handler throws, every handler after it in the fan-out is skipped.**

Two of the five `MatchCacheChanged` consumers can throw:

- `SemanticTokensPushHandler.Handle` (`Pipeline/SemanticTokensPushHandler.cs:48-74`) — `async`,
  awaits `GetSemanticTokensAsync`, calls `SendNotification`; no `try`/`catch`.
- `DiagnosticsPublishHandler.Handle` (`Pipeline/DiagnosticsPublishHandler.cs:68-124`) — calls
  `_scopeManager.ResolvePrimaryOwner`, `_registryLookup.GetRegistryForUri`, `_aggregator.Aggregate`,
  `SendNotification`; no `try`/`catch`.

The other three (`SemanticTokensRefresh`, `InlayHintRefresh`, `CodeLensRefresh`) are safe only
because their real work is deferred into `RefreshDebouncer`, which catches.

**Failure scenario.** A `.feature` file whose primary owner has just been removed
(`ProjectRemoved` → `_matchService.InvalidateAllForProject`, `Registry/BindingRegistryProviderRouter.cs:180`)
races a `MatchCacheChanged` publish. `DiagnosticsPublishHandler` throws. If it is ordered before
`SemanticTokensPushHandler` in the scan order, the user gets **no diagnostics *and* no semantic
tokens** — the file renders uncoloured with no squiggles. The exception surfaces as a single
`LogWarning` from `ParseCoordinator.RunSafelyAsync` ("Scheduled work for '...' failed"), attributed
to the parse, not to the handler that actually failed. Nothing retries. The file stays wrong until
the user types again.

The blast radius is set by assembly-scan order — i.e. by reflection ordering, not by any decision
anyone made.

---

## 4. Processes spanning multiple handlers or services

Five multi-step processes exist. Only one is genuinely stateful.

### 4.1 Feature-file reparse (4 entry points, 1 shared tail) — **inconsistently coordinated**

All four producers in §2.1 perform the identical pair:

```csharp
await _taggerService.ParseAsync(uri, version).ConfigureAwait(false);
await _mediator.Publish(new MatchCacheChangedNotification(uri, version ?? 0), ct);
```

duplicated verbatim at `TextDocumentSyncHandler.cs:233-241`, `BindingRegistryChangedHandler.cs:358-367`,
`ReqnrollConfigChangedHandler.cs:65-73`, and inline at `DocumentActivatedHandler.cs:56-68`.

The invariant "a `ParseAsync` on an open document must be followed by a `MatchCacheChanged`
publish" is real (the sibling methods `ScanClosedFileAsync` / `RescanClosedFileAsync` deliberately
must *not* publish, since closed files have no client-side view) — but it is enforced only by four
copies of the same two lines.

**And two of the four copies bypass `IParseCoordinator`.** `ReqnrollConfigChangedHandler` and
`DocumentActivatedHandler` do not inject it at all (verified: neither appears in the
`IParseCoordinator` injection-site list; their constructors are
`ReqnrollConfigChangedHandler.cs:26-40` and `DocumentActivatedHandler.cs:37-49`). This has two
consequences:

1. **A concurrent same-URI parse is possible again.** A `.editorconfig` save that lands while a
   `didChange` reparse is in flight for the same file produces exactly the two-concurrent-
   `ParseAsync`-on-one-URI shape that `ParseCoordinator` exists to prevent, and that issue #554
   attributed to non-atomic match-set stores. The window is narrow and requires a config save
   during typing, which is presumably why it has not been reported — but the primitive built to
   close it is simply not applied on these paths.
2. **`WaitForReadyAsync` sees nothing.** `FoldingRangeHandler.cs:62` and
   `DocumentSymbolHandler.cs:89,126` await `WaitForReadyAsync` before reading `buffer.Tags`,
   precisely because they have no LSP refresh capability to self-heal a stale answer. A
   config-driven or activation-driven reparse registers no pending entry, so a `foldingRange`
   request racing it gets the stale-answer-with-no-correction outcome that `IParseCoordinator`'s
   own remarks (`Pipeline/IParseCoordinator.cs:16-24`) identify as "a data-integrity regression,
   not just a missed optimization."

This is a **local defect against an existing, correct design** — not an argument for a new pattern.

### 4.2 Binding-registry reconciliation — the one genuinely stateful process

`BindingRegistryChangedHandler.Handle` (`Pipeline/BindingRegistryChangedHandler.cs:90-160`) is a
five-step sequence with branching, a debounced tail, and three distinct effect classes:

```
1. RemoveBindingFilesAsync          (if RemovedBindingFilePaths)
2. IsFullReplacement ? { RediscoverCsFilesAsync ; ScanAllFeatureFilesAsync }
                     : { FeatureRescanDebouncer.ScheduleRescan(…) }   ← runs after Handle returns
3. ReparseOpenFilesAsync            (schedules; does not await)
4. RequestCodeLensRefreshAsync      (if IsFullReplacement)
```

At 530 lines this is the largest file in the pipeline and the only place where "what happens next"
depends on flags, prior state (`HasBaselineForProject`), and filesystem timestamps
(`GetAssemblyWriteTimeUtc`, `CollectCsFilesToReconcile:496-513`). Steps 2 and 3 complete
*after* `Handle` returns, so the method's own duration measurement deliberately excludes them
(`:129-133`).

It is nonetheless **not** a saga: there is no persisted instance, no timeout, no compensation, no
resumption. It is a decision tree over in-memory state.

### 4.3 Startup convergence race (`projectLoaded` ⟷ `projectFiles`) — **has an explicit state machine already**

The two client notifications can arrive in either order. The system handles this with a
deferred-work flag:

- baseline arrives first, no project yet → `_pendingFullRescan[key] = true` (`Workspace/MembershipIndex.cs:148`);
- project arrives → `TryConsumePendingFullRescan` → fire the deferred publish
  (`Workspace/LspWorkspaceScopeManager.cs:139-152`).

Plus a three-state ownership machine — `MembershipState.Owned | Pending | Unowned` — whose
transitions are reasoned about carefully in `LspWorkspaceScopeManager.GetMembershipState:317-355`,
including the subtle "inside a known scope but no project yet ⇒ Pending, not Unowned" rule that
protects invariant I2.

**This is already the explicit state machine that a reviewer might otherwise recommend building.**
It is undersold: it is spelled as an enum plus a `ConcurrentDictionary<ProjectKey, bool>` plus
prose, rather than named as a lifecycle. But it exists and it works.

### 4.4 Rename — request/response, not choreography

`renameTargets` → `selectRenameTarget` → `rename` → `RenamePostApplyCoordinator`. Stateful across
messages, has a real rollback contract (§3.7), and is correctly kept off the event bus. **No
change recommended.**

### 4.5 `.editorconfig` fan-out — quadratic-ish, low severity

`HandleEditorConfigChangeAsync` publishes one `ReqnrollConfigChangedNotification` **per project in
the scope** (`Workspace/WatchedFilesHandler.cs:164-193`), and each consumer reparses every open
buffer under that project's folder. With nested project folders the same buffer is reparsed once
per containing project. Bounded by (projects × open buffers) and rare; noted for completeness.

---

## 5. Should this remain choreography?

**Yes. Keep the choreography.** The recommendation is to fix specific defects and formalize
existing contracts — not to introduce a new coordination pattern.

The evidence for that conclusion, taken from §3:

| Pattern | Verdict | Why, from this codebase |
|---|---|---|
| **Keep choreography** | ✅ **Recommended** | Fan-out consumers are genuinely independent (§3.5.3), idempotent (§3.3), and in-memory (§3.2). The bus has 3 event types, 8 handlers, and no cycles. This is small. |
| **Explicit state machine** | ⚠️ **Already present; name it, don't build one** | `MembershipState` + `_pendingFullRescan` (§4.3) *is* the startup lifecycle machine. A second one would duplicate it. The one place a small machine would help is per-project readiness (§6.3, R4), and that is a refactoring of existing flags, not a new pattern. |
| **Process manager / saga** | ❌ **Not justified** | Sagas earn their cost via persisted instances, timeouts, and compensating transactions. §3.2 shows no durable state; §3.7 shows the only compensation lives in a synchronous request handler. A saga here buys nothing and adds an instance store, correlation keys, and a lifecycle to debug. |
| **Workflow / orchestration engine** | ❌ **Strongly not justified** | The longest flow is 5 steps in one method (§4.2). An engine would add a dependency, a serialization format, and a scheduler to a process whose entire state fits in memory and is discarded on exit. |
| **Durable task framework** | ❌ **Contradicted by the design** | Durability is the one property this system deliberately does not want: the LSP server is a per-session child process whose correct recovery is a full re-initialize from the client. Persisting cascade state would create resume-into-a-stale-workspace bugs of exactly the kind issue #555 already produced from a *shared* server surviving a client teardown. |
| **Consolidate ownership + formalize contracts** | ✅ **Recommended** | This is where the real defects are: §1.2 (one type, three meanings), §1.3 (vestigial marker), §3.1 (no causation), §3.8 (fan-out aborts on first throw), §4.1 (two paths bypass the coordinator). |

The system's problems are **contract-clarity and error-isolation problems**, not coordination
problems. The coordination is, on the evidence, working.

---

## 6. Recommendations

Ordered by (severity × confidence) ÷ blast radius. R1–R3 are independent and can land in any order.

---

### R1 — Isolate faults in the MediatR fan-out ([#575](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/575))

**Type:** Local refactoring (one DI registration + one new class).
**Severity:** High — silent, user-visible loss of diagnostics and colouring.

**Evidence:** §3.8. `Pipeline/SemanticTokensPushHandler.cs:48-74` and
`Pipeline/DiagnosticsPublishHandler.cs:68-124` have no `try`/`catch`; MediatR 8's default publisher
is a sequential `foreach`/`await`; the codebase already reasons about this publisher's semantics at
`Registry/BindingRegistryProviderRouter.cs:195-200`.

**Current risk:** One throwing handler suppresses every later handler in the fan-out. Which
handlers are lost depends on assembly-scan order. The failure is logged as a parse failure, not a
handler failure, so the log actively misdirects diagnosis.

**Proposed change:** register a custom `INotificationHandler` dispatch strategy that runs each
handler inside a `try`/`catch`, logs `LogError` with the handler type name and the notification
identity, and continues. In MediatR 8 this is done by registering a `ServiceFactory`-based custom
`IMediator` or by overriding `Mediator.PublishCore`. **(inferred — the exact MediatR 8 extension
point should be confirmed against the pinned package before implementation; `MediatR.Extensions.Microsoft.DependencyInjection 8.*`,
`src/LSP/.../Reqnroll.IdeSupport.LSP.Server.csproj:13`.)**

**Migration:**
1. Add `ResilientMediator : Mediator` overriding `PublishCore` with per-handler `try`/`catch`.
2. Register it in `ServiceCollectionExtensions` after `AddMediatR`.
3. No handler changes — this is transparent to all 8 handlers.

**Tests:** a handler that throws must not prevent a second handler from running, for each of the
three notification types; the thrown exception must be logged with the failing handler's type name.

**Observability:** new `LogError` line `"[Bus] {HandlerType} failed for {NotificationType}({Identity}): {Message}"`.
This is the first log line in the system that names a failing handler.

**Rollback:** revert the single DI registration; behaviour returns to stock MediatR exactly.

---

### R2 — Route every open-document parse through `IParseCoordinator` ([#576](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/576))

**Type:** Local refactoring (two constructors, two call sites).
**Severity:** Medium-high — reopens a class of bug the codebase already paid to close.

**Evidence:** §4.1. `ReqnrollConfigChangedHandler.cs:26-40` and `DocumentActivatedHandler.cs:37-49`
do not inject `IParseCoordinator`; both call `ParseAsync` directly (`:69` and `:56`). Compare
`TextDocumentSyncHandler.cs:123,157` and `BindingRegistryChangedHandler.cs:306`, which do.

**Current risk:** (a) concurrent same-URI `ParseAsync` — the #554 shape — on a config save during
typing; (b) `FoldingRangeHandler.cs:62` / `DocumentSymbolHandler.cs:89,126` `WaitForReadyAsync`
calls see no pending entry, so these two refresh-incapable pull handlers can return stale results
with no correction path, the exact regression `Pipeline/IParseCoordinator.cs:16-24` was written to
prevent.

**Local, not systemic:** the primitive, its contract, and its tests already exist. This applies it
to the two paths that were missed.

**Migration:**
1. Inject `IParseCoordinator` into both handlers.
2. Replace `await ParseAndNotifyAsync(...)` with `_parseCoordinator.Schedule(uri, ct => ParseAndNotifyAsync(uri, version, ct))`.
3. Note the semantic change: `ReqnrollConfigChangedHandler.Handle` currently awaits each reparse
   sequentially; after the change it schedules and returns. Confirm no caller depends on
   completion — **(inferred: none does, since it is reached from a `Serial`-lane
   `didChangeWatchedFiles` whose result is discarded; verify before merging.)**

**Tests:** a config-change reparse must register a pending entry observable by `WaitForReadyAsync`;
a `didChange` and a config-change for the same URI must not run `ParseAsync` concurrently
(extend the existing `ParseCoordinatorTests`); `DocumentActivatedHandler` likewise.

**Observability:** none required — existing `ParseCoordinator` warnings now cover these paths too.

**Rollback:** revert both call sites; independently revertible per handler.

---

### R3 — Split `BindingRegistryChangedNotification` into three named events ([#577](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/577))

**Type:** Systemic (contract change across 4 producers and 2 consumers) — but mechanical.
**Severity:** Medium — the dominant *maintenance* risk in the system.

**Evidence:** §1.2. Four producers with different meanings publish one type
(`BindingRegistryProviderRouter.cs:202`, `LspWorkspaceScopeManager.cs:148`,
`MembershipIndex.cs:91`, `MembershipIndex.cs:137`); the consumer demultiplexes on
`IsFullReplacement` at `BindingRegistryChangedHandler.cs:110-143`; `RemovedBindingFilePaths` is a
command in a fact's clothing.

**Current risk:** every new producer must reason about a 3-way flag/payload matrix, and the
consumer's two branches are only distinguishable by reading 60 lines of comments. The `notify: false`
cycle-breaker (`ConnectorBindingRegistryProvider.cs:180-224`) exists because one producer's semantics
leaked into another's path. Adding a fifth producer means re-deriving all of it.

**Proposed contracts:**

| New event | Producers | Consumer behaviour |
|---|---|---|
| `BindingRegistryReplacedNotification(LspReqnrollProject Project)` | Router (connector run), ScopeManager (deferred rescan), MembershipIndex (baseline) | Roslyn rediscovery + closed-file scan + open-file reparse + code-lens refresh |
| `BindingRegistryPatchedNotification(LspReqnrollProject Project)` | Router (Roslyn per-file patch) | debounced rescan + open-file reparse |
| `ProjectBindingFilesRemovedNotification(LspReqnrollProject Project, IReadOnlyCollection<string> Paths)` | MembershipIndex (delta) | purge, then patch behaviour |

**Migration (incremental, each step independently shippable):**
1. Add the three types alongside the existing one; make `BindingRegistryChangedHandler` implement
   handlers for all four, with the existing method delegating to the new ones. No producer changes.
   Behaviour identical.
2. Move producers one at a time, starting with `MembershipIndex.cs:91` (the removal command — the
   clearest case and the only one with a payload).
3. Move `CSharpDiagnosticsRegistryChangedHandler` to subscribe to all three (it wants "anything
   changed" and is deliberately undiscriminating — `CSharpDiagnosticsRegistryChangedHandler.cs:28-32`).
4. Delete `BindingRegistryChangedNotification` once no producer remains.

**Risks:** the `notify: false` gate and the `HasExpressionChanges || HasHookChanges` gate
(`ConnectorBindingRegistryProvider.cs:231-233`) must be preserved exactly; step 1 keeps them
untouched by construction. The main hazard is losing the accumulated comment rationale — carry the
comments to the new types verbatim rather than rewriting them.

**Tests:** `BindingRegistryChangedHandlerTests` and `CSharpDiagnosticsRegistryChangedHandlerTests`
already exist and should be parameterized over the new types in step 1, before any producer moves;
a green run there is the gate for step 2.

**Observability:** producer-side log lines gain the event name, which partially addresses §3.1
without a full correlation-ID scheme.

**Rollback:** each step is a revert of one commit; after step 1 the system tolerates both old and
new producers indefinitely, so the migration can be paused at any point.

---

### R4 — Formalize the two invariants the bus relies on but never states ([#578](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/578))

**Type:** Local (documentation + tests, optionally one shared helper).
**Severity:** Low severity, high leverage.

**Evidence:** §3.3 (idempotency assumed everywhere, asserted nowhere); §4.1 (the parse→publish pair
duplicated at four sites, with two sibling methods that must *not* publish).

**Proposed change:**
1. State on each notification type that consumers must be idempotent, and add one test per
   notification type asserting double-publish ≡ single-publish in observable effect.
2. Extract the parse→publish pair into a single service method — e.g.
   `IGherkinDocumentTaggerService.ParseAndPublishAsync(uri, version, ct)`, or a small
   `FeatureDocumentReparser` — so the four copies become one, and the "closed-file scans must not
   publish" distinction becomes a type-level rather than convention-level fact. Best sequenced
   *after* R2, so all four sites already share the coordinator shape.

**Rollback:** trivial; pure refactoring with no behaviour change.

---

### R5 — Delete the vestigial and dead event surface ([#579](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/579))

**Type:** Local, mechanical.
**Severity:** Low.

**Evidence:** §1.3 (three `INotification` markers never published — `ReqnrollProjectLoadedParams.cs:9`,
`ReqnrollProjectUnloadedParams.cs:9`, `ReqnrollProjectFilesParams.cs:10`); §1.4
(`ScopeOpened`/`ScopeClosed` at `LspWorkspaceScopeManager.cs:43,45` with zero subscribers;
`ConfigurationChanged` raised at `ProjectScopeIdeSupportConfigurationProvider.cs:97` with its only
subscriber commented out at `ProjectSettingsProvider.cs:42,53,129`).

**Current risk:** the `INotification` markers advertise a pub/sub extension point that does not
exist — a handler added for them compiles, registers, and never runs. The dead events cost a raise
on every workspace close and every config reload.

**Proposed change:** drop `: INotification` from the three params types (they are DTOs for
`OnNotification` routing); either delete `ScopeOpened`/`ScopeClosed` and `ConfigurationChanged`, or
document them as intentional extension points with a comment saying so. Decide per event — do not
bulk-delete, since `ConfigurationChanged` may be a deliberate placeholder for the commented-out
`ProjectSettingsProvider` wiring.

**Rollback:** trivial.

---

### R6 — Bound `ScanAllFeatureFilesAsync` ([#580](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/580))

**Type:** Local (one handler), independent of R1–R5.
**Severity:** Medium in the large-corpus case only.

**Evidence:** §3.6. `Pipeline/BindingRegistryChangedHandler.cs:208-262` enumerates and parses every
closed feature file in a project with no count cap, size cap, or deadline, under
`CancellationToken.None` (supplied by `FireAndForget`).

**Correction:** an earlier draft of this review recorded this as "already tracked under issue
#491." That was wrong on two counts, and the error is worth stating rather than silently fixing,
because it would have caused a real defect to be dropped: #491 is **closed**, and its subject was
`RunTestCodeLensService.GetTargetsAsync` / `ScenarioTestTargetResolver.Resolve` — the unbatched
per-scenario `reqnroll/resolveTestTargets` path — which shares no code with
`ScanAllFeatureFilesAsync`. This finding is **untracked**.

**Recommendation:** file and fix separately from the event-architecture work (R1–R5). It is a
throughput problem inside one handler, not a bus problem, and bundling it would entangle two
independent changes. A count/deadline bound plus a real `CancellationToken` are the likely shape,
but the fix should be designed against a measurement, not guessed.

---

## 7. What was not verified

Stated plainly so the review is not over-trusted:

- **MediatR 8's exact `PublishCore` implementation was not read from the package source.** The
  sequential-await behaviour is asserted by this codebase's own comment
  (`Registry/BindingRegistryProviderRouter.cs:195-200`) and the no-isolation consequence follows
  from a `foreach`/`await` loop, but R1's chosen extension point should be confirmed against the
  pinned assembly before implementation.
- **No code was executed.** No build, no test run, no live IDE session. Every claim is from static
  reading.
- **Handler registration order was not empirically determined.** §3.8's blast radius depends on
  assembly-scan order, which was not observed at runtime. The defect does not depend on the order
  — only on which handlers are lost.
- **The Rider and VS Code client event producers were enumerated but not read in depth.** The VS
  client was read most closely because it has the most interception machinery. Client-side
  behaviour is not the subject of this review.
- **`ReqnrollConfigChangedHandler` has no unit tests** (verified: no test file references it;
  the only hits are in the benchmark harness and an unrelated Core concurrency test). R2 touches
  this handler, so tests should be added as part of that change rather than assumed to exist.
- **The `.editorconfig` per-project fan-out (§4.5) was not measured**, only read.
