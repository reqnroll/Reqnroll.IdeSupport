# Rider Client-Side Project & Document Lifecycle Sync — Implementation Plan

> **Status:** Phase 0 spike complete (2026-07-13) — see §5 and §7. Findings verified against
> the actual bytecode of Rider 2024.3.5's bundled `product.jar`/`intellij.rider.jar` inside the
> devcontainer's Gradle cache (`javap`/`unzip -l`), not just JetBrains' public docs or `main`
> branch source — those reflect a materially newer, not-yet-released API (`LspClientDescriptor`,
> `LspClient`, a distinct `Lsp4jServer` type) that **does not exist in 2024.3.5 at all**.
> **Phase 1 also complete** (2026-07-13) — `ReqnrollLanguageServer`, the Kotlin protocol DTOs,
> `ReqnrollNotificationSender`, and the `lsp4jServerClass` wiring all exist and compile/build
> successfully (`./gradlew compileKotlin`/`buildPlugin`, verified for real inside the
> devcontainer, not just eyeballed).
> **Phase 2 also complete** (2026-07-13) — `ReqnrollRunnableProjectsListener` sends
> `projectLoaded`/`projectUnloaded` via a single reactive-property subscription, simpler than
> originally planned (see §5). `./gradlew verifyPlugin` now passes for real against two actual
> Rider versions — fixed a genuine Marketplace-rule violation (plugin ID contained "rider")
> along the way.
> **Phases 3 and 4 also complete** (2026-07-13) — `ReqnrollProjectFilesSync` sends
> `projectFiles` baselines/deltas (folder-prefix attribution, not a full `ProjectModelEntity`
> traversal — deliberate scope reduction, see §5); `ReqnrollDocumentActivationSync` ports VS's
> `DocumentActivationState` state machine verbatim, driven by `FileEditorManagerListener` instead
> of raw LSP-pipe interception (which doesn't exist on Rider). All four `reqnroll/*` lifecycle
> notifications now have working client-side glue, verified via `compileKotlin`/`buildPlugin`/
> `verifyPlugin`/`test` inside the devcontainer.
> **Update (2026-07-20):** the "not yet verified end-to-end" caveat below is superseded — live
> `runIde` verification did happen in the interim, as a byproduct of the broader Rider
> feature-parity push (issues #157–#166: rename, document outline/structure view, go-to-hooks,
> code folding, comment toggle, code lens), all closed 2026-07-16–2026-07-18. Several of those
> fixes (e.g. the EDT-thread-violation bugs) are only reproducible against a live server
> connection, so the lifecycle sync this plan describes has been exercised end-to-end since.
> Original note follows: **Not yet verified end-to-end against a live
> server connection** — no `runIde` session driven this round; see §6.
> **Audience:** Core team contributors picking up Rider glue work after the skeleton (`src/Rider`).
> **Supersedes:** Risk **R1 · Rider Custom Notification Transport** in
> [Porting-to-VSCode-Rider-Analysis.md](Archive/Porting-to-VSCode-Rider-Analysis.md) (archived — its Rider section predates all Rider implementation work) — that risk asked
> "investigate whether Rider's `LspServer` API supports `sendNotification`"; this plan answers it
> (short answer: no generic method, but a supported typed-interface path exists — §3.1) and scopes
> the actual work.
> **Prior art referenced throughout:** `src/VisualStudio/.../LspNotifications/VsProjectEventMonitor.cs`
> + `VsProjectPayloadBuilder.cs` + `LspProjectPreloadPusher.cs` (VS), `src/VSCode/src/projectManager.ts`
> (VS Code).

---

## 1. Why this exists

The server defines four "client pushes lifecycle state" custom notifications
(`src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/LspMethodNames.cs`):

| Method | Purpose |
|---|---|
| `reqnroll/projectLoaded` | Project build properties (output assembly, TFM, package refs, default namespace) — needed for reflection-based binding discovery. |
| `reqnroll/projectUnloaded` | Signals project removal. |
| `reqnroll/projectFiles` | Authoritative file-membership index (which `.feature`/`.cs` files belong to which project), baseline + incremental deltas — needed for linked/excluded files the folder-prefix fallback can't handle. |
| `reqnroll/documentActivated` | Tab-activation signal (issue #85) — the server can't infer "this is now the visible tab" from `didOpen`/`didClose` alone; without it, stale diagnostics/semantic tokens can persist on a reactivated tab. |

VS and VS Code both implement client-side glue that watches project/file/tab lifecycle events and
sends these four notifications. Without it, Rider falls back to whatever degraded behavior the
server has for absent project data (folder-prefix membership, no reflection-based discovery,
stale-tab diagnostics) — the same fallback VS Code relies on today. This plan scopes building the
Rider equivalent.

**Out of scope:** the request-style custom methods (`reqnroll/findStepUsages`,
`reqnroll/goToStepDefinitions`, `reqnroll/goToHooks`, `reqnroll/findUnusedStepDefinitions`,
`reqnroll/renameTargets`, `reqnroll/selectRenameTarget`, `reqnroll/documentSymbolHierarchical`).
Those are user-action-driven (a context-menu item or explicit command), not lifecycle-driven, and
need their own UI-action wiring (Rider `AnAction`s) — a separate, later plan.

---

## 2. What we already confirmed (no further research needed)

- **No generic send-anything API.** `com.intellij.platform.lsp.api.LspServer`/`LspServerManager`
  expose no `sendNotification(method, params)` overload taking a raw method name. Confirmed against
  the platform source and — decisively — against Rider 2024.3.5's actual decompiled
  `LspServer`/`LspServerManager` classes (§3.1).
- **Typed custom-notification path exists, verified against the real 2024.3.5 bytecode, not just
  docs.** `LspServerDescriptor.lsp4jServerClass: Class<out LanguageServer>` is a genuine, overridable
  property in the exact bundled `product.jar`. `LspServer.sendNotification(lambda)` and
  `LspServer.getLsp4jServer(): LanguageServer` both exist as real methods. Full mechanism in §3.1 —
  no longer an "open question."
- **`LspServerDescriptor`/`ProjectWideLspServerDescriptor` are NOT deprecated at 2024.3.5** — despite
  JetBrains' current `main` branch marking them `@Deprecated` in favor of `LspClientDescriptor`.
  That replacement class, along with `LspClient` and a distinct `Lsp4jServer` type, **does not exist
  in 2024.3.5's bundled jars at all** (confirmed by extracting and listing every class under
  `com/intellij/platform/lsp/api/` in `product.jar` — only the classes this plan uses are present).
  No migration concern for the `sinceBuild`/`untilBuild` range this plugin currently targets.
- **No pipe/middleware interception** (established in prior work on this plugin) — so unlike VS's
  `LspInterceptingPipe`, this can't be layered in as a message-stream interceptor. It has to be
  independent Kotlin listener code calling the typed proxy directly, parallel to (not wrapping)
  whatever the platform's generic document-sync already does.
- **Wire format is camelCase** (`CamelCasePropertyNamesContractResolver`,
  `LanguageServerOptionsExtensions.cs:45`) — confirmed so the Kotlin DTOs below use exact matching
  property names.

---

## 3. Design

### 3.1 Transport: a custom LSP4J client interface

```kotlin
// src/main/kotlin/com/reqnroll/ide/rider/lsp/protocol/ReqnrollLanguageServer.kt
interface ReqnrollLanguageServer : LanguageServer {
    @JsonNotification("reqnroll/projectLoaded")
    fun projectLoaded(params: ReqnrollProjectLoadedParams)

    @JsonNotification("reqnroll/projectUnloaded")
    fun projectUnloaded(params: ReqnrollProjectUnloadedParams)

    @JsonNotification("reqnroll/projectFiles")
    fun projectFiles(params: ReqnrollProjectFilesParams)

    @JsonNotification("reqnroll/documentActivated")
    fun documentActivated(params: DocumentActivatedParams)
}
```

`ReqnrollLspServerDescriptor` overrides `lsp4jServerClass` to return
`ReqnrollLanguageServer::class.java` instead of the platform default.

**Sending, verified against Rider 2024.3.5's actual decompiled classes** (`javap -p` output, not
guessed):

```kotlin
val servers = LspServerManager.getInstance(project)
    .getServersForProvider(ReqnrollLspServerSupportProvider::class.java)

servers.forEach { server ->
    server.sendNotification { lsp4jServer ->
        (lsp4jServer as ReqnrollLanguageServer).projectLoaded(params)
    }
}
```

`LspServerManager.getInstance(project).getServersForProvider(...)` returns the
`Collection<LspServer>` started by our provider; `LspServer.sendNotification(lambda: (LanguageServer)
-> Unit)` is a real method on the real interface (confirmed: `public abstract void
sendNotification(kotlin.jvm.functions.Function1<...>)` in the decompiled
`com/intellij/platform/lsp/api/LspServer.class`). `LspServer` also exposes a direct
`getLsp4jServer(): LanguageServer` getter as an alternative to the lambda form. No JSON
string-building by hand, unlike VS's `JsonConvert.SerializeObject(paramsObj, ...)` + raw pipe write
(that pattern exists there only because VS intercepts at the raw-pipe level; here LSP4J's proxy
handles serialization for us).

### 3.2 Payload mapping

Kotlin data classes mirroring the server's DTOs exactly (camelCase, matching
`src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/*.cs` and
`Features/DocumentActivated/DocumentActivatedParams.cs` field-for-field):

| Server DTO | Rider-sourced fields | Source (§3.3) |
|---|---|---|
| `ReqnrollProjectLoadedParams` | `workspaceFolder`, `projectFile`, `projectFolder`, `outputAssemblyPath`, `targetFrameworkMoniker`, `defaultNamespace`, `packageReferences[]` | Rider project model — biggest unknown, see §3.3/§6 Phase 0 |
| `ReqnrollProjectUnloadedParams` | `projectFile` | Workspace model listener |
| `ReqnrollProjectFilesParams` | `projectFile`, `targetFrameworkMoniker`, `kind` (baseline/delta), `files[]` (`path`, `role`, `added`) | Workspace model + `VirtualFileListener`/`AsyncFileListener` |
| `DocumentActivatedParams` | `uri` | `FileEditorManagerListener` |

`role` classification (`Feature` vs `Binding`) is a pure extension check — port
`VsProjectEventMonitor.ClassifyRole` verbatim (`.feature` → `Feature`, `.cs` → `Binding`, else
untracked).

### 3.3 Event sources — mapping VS's event table to Rider/IntelliJ Platform APIs

VS's `VsProjectEventMonitor` subscribes to: `SolutionEvents` (opened/closed, project added/removed),
`BuildEvents` (build done → full resend), `WindowEvents` (tab activation), and
`IVsTrackProjectDocumentsEvents2` (per-file add/remove/rename — needed because Solution Explorer
single-file operations don't raise solution/build events at all, issue #32). Rider equivalents:

| VS source | Fires on | Confirmed Rider/IntelliJ equivalent |
|---|---|---|
| `SolutionEvents.Opened`/`AfterClosing` | Solution open/close | `ProjectManagerListener` (generic IntelliJ Platform, not Rider-specific) — confirmed to exist. |
| `SolutionEvents.ProjectAdded`/`ProjectRemoved` | Project add/remove | `com.jetbrains.rider.run.configurations.RunnableProjectListener` — confirmed to exist in `intellij.rider.jar` (Rider-specific, not generic IntelliJ Platform). Exact firing semantics (add/remove vs. any recompute) not yet empirically confirmed — verify in Phase 1 by logging every event it delivers against the sample project. |
| `BuildEvents.OnBuildDone` | Any build/rebuild completes | Same `RunnableProjectListener` is the leading candidate — `RunnableProject`/`ProjectOutput` (below) are recomputed data that plausibly refreshes post-build, same purpose as VS's full-resend-on-build-done. Not yet empirically confirmed; if it doesn't fire on rebuild specifically, fall back to VS Code's `**/bin/**/*.dll` watcher approach. |
| `IVsTrackProjectDocumentsEvents2` (add/remove/rename) | Single-file Solution Explorer ops | `VirtualFileListener`/`AsyncFileListener` (generic IntelliJ Platform, `VirtualFileManager.addAsyncFileListener`) — confirmed to exist and fire for individual file add/remove/rename, matching the "no solution/build event" gap issue #32 called out. |
| `WindowEvents.WindowActivated` (filtered to `.feature` docs) | Tab switch | `FileEditorManagerListener.selectionChanged` — confirmed to exist, standard IntelliJ Platform editor-tab API. Directly analogous; likely simpler than VS's DTE `Window`/`Document` COM traversal. |

**`ReqnrollProjectLoadedParams`'s rich fields — resolved, mostly good news.** Confirmed by
decompiling the actual bundled model classes (`com.jetbrains.rider.model.RunnableProject`,
`ProjectOutput`, `RdTargetFrameworkId` in `product.jar`) — these are **already-generated classes
shipped inside the Rider SDK itself**, consumable as ordinary Kotlin classes via the existing
`intellijPlatform { rider(...) }` Gradle dependency. **No custom RD protocol submodule needed** —
this was the plan's single biggest named risk (old R1) and it does not materialize:

- `RunnableProject`: `projectFilePath`, `name`, `fullName`, `kind: RunnableProjectKind`,
  `projectOutputs: List<ProjectOutput>` → covers `projectFile` directly.
- `ProjectOutput`: `tfm: RdTargetFrameworkId`, `exePath: String`, `workingDirectory`,
  `configuration` → covers `targetFrameworkMoniker` and `outputAssemblyPath` directly.
- **Open sub-item (small, not blocking):** whether a typical Reqnroll test project (an MSTest/NUnit/
  xUnit host) is categorized under a `RunnableProjectKind` that actually appears in Rider's runnable-
  project list — `RunnableProjectKind` wraps a plain `name: String` set dynamically by the backend,
  not a fixed compile-time enum, so this can't be confirmed by more static class inspection. Confirm
  empirically in Phase 1 by logging `RunnableProject.kind.name` for the sample project.
- **Package references (NuGet) — no dedicated Rider model class found** in `product.jar` or the
  `dotCommon`/`rider-nuget`-adjacent plugin jars searched. Recommendation: don't keep hunting for a
  Rider-specific API — read `obj/<project>.csproj.nuget.g.props` / `obj/project.assets.json` directly
  from disk instead (produced by `dotnet restore`, IDE-agnostic, reliable). This is arguably the
  better design regardless of editor, since it sidesteps the question entirely.
- **File membership** (`reqnroll/projectFiles`, all `.feature`/`.cs` files per project):
  `com.jetbrains.rider.projectView.workspace.ProjectModelEntity` confirmed to exist in the SDK;
  exact traversal API not fully mapped in this pass — reasonable to defer to Phase 3 implementation
  rather than block Phase 0 further, since the class's existence already answers "is this reachable
  without RD protocol authoring" (yes).

### 3.4 Reuse from `VsProjectEventMonitor`/`VsProjectPayloadBuilder`

Port verbatim (logic is IDE-agnostic):
- `ClassifyRole` (extension → `ProjectFileRole`)
- The delta-vs-baseline decision structure (`ProjectFilesKind`)
- `DocumentActivationState`'s state machine for issue #85 (avoid resending `documentActivated` for
  a tab that's already active/hasn't changed) — port the state machine, not the DTE-specific
  `Window`/`Document` plumbing around it.

---

## 4. Non-goals

- Not replicating VS's pipe-level `LspInspectorLogger`/interceptor architecture — established
  separately as impossible on Rider (no interception hook).
- Not building the request-style custom methods (§1 "Out of scope").
- Not attempting cross-project linked-file resolution beyond what `ReqnrollProjectFilesParams`
  already models — that's a server-side concern, unaffected by which client sends the notification.

---

## 5. Phased implementation plan

**Phase 0 — Research spike — COMPLETE (2026-07-13):**
1. ✅ **Resolved.** Transport confirmed by decompiling Rider 2024.3.5's actual `product.jar`:
   `LspServerManager.getInstance(project).getServersForProvider(...)` →
   `LspServer.sendNotification { (it as ReqnrollLanguageServer).projectLoaded(params) }`. See §3.1.
2. ✅ **Resolved, favorably.** Rider's frontend already ships generated project-model classes
   (`RunnableProject`/`ProjectOutput`/`RdTargetFrameworkId`) covering TFM + output assembly path —
   **no RD protocol submodule needed.** Package references need a disk-read fallback
   (`project.assets.json`) instead of a Rider API. See §3.3.
3. 🟡 **Partially resolved.** `RunnableProjectListener` is a strong, confirmed-to-exist candidate for
   the build-completion/project-add-remove signal, but exact firing semantics need empirical
   confirmation in Phase 1 (log every event against the sample project) rather than further static
   analysis — decompiling a listener interface doesn't reveal *when* the backend actually calls it.

Net effect: this plan is meaningfully smaller than originally estimated (no RD protocol work), and
Phase 1 can start directly instead of blocking on further research.

**Phase 1 — Transport plumbing — COMPLETE (2026-07-13)** (matches the original porting
analysis's R5 "~70 lines" estimate for this piece specifically):
- ✅ `ReqnrollLanguageServer` interface (§3.1) — `lsp/protocol/ReqnrollLanguageServer.kt`.
- ✅ Wired into `ReqnrollLspServerDescriptor` via `override val lsp4jServerClass`.
- ✅ Kotlin DTOs mirroring §3.2's table — `lsp/protocol/ReqnrollProtocolTypes.kt`. `kind`/`role`
  ended up as plain `Int` rather than Kotlin enums — LSP4J's Gson serializer defaults enums to
  name-as-string, which wouldn't match the server's integer-based wire format.
- ✅ A thin "send if connected, log+swallow if not" wrapper — `lsp/ReqnrollNotificationSender.kt`,
  using `ReqnrollDebugLogger` for failures.
- Verified for real via `./gradlew compileKotlin` and `buildPlugin` inside the devcontainer, not
  just eyeballed — caught a real bug this way: block comments nest in Kotlin, so literal
  `reqnroll/*` text inside a KDoc comment opened an unterminated nested comment, silently
  swallowing the rest of the file. Also fixed `gradlew`'s CRLF line-ending corruption
  (`.gitattributes` now pins it to LF) discovered while trying to run `./gradlew` in the
  container to verify this phase.
- Nothing calls `ReqnrollNotificationSender` yet — that's Phases 2-4 (the actual event sources).

**Phase 2 — Project lifecycle — COMPLETE (2026-07-13).** Turned out simpler than planned:
rather than separate `WorkspaceModel`/`ModuleListener` + a distinct build-completion listener,
`ReqnrollRunnableProjectsListener` (a `ProjectActivity`, `lsp/project/`) subscribes once to
`project.solution.runnableProjectsModel.projects` (an RD `IOptProperty`, confirmed via decompiling
Rider 2024.3.5's actual classes — `RunnableProjectsModel`/`SolutionHostExtensionsKt`). A single
`advise` call covers all three original bullet points: it fires immediately with the current
value on subscribe (initial flush — no separate `SendInitialProjectsAsync`-equivalent needed),
and again whenever Rider's backend recomputes the model, including after a build (no separate
build-completion listener needed either — this resolves Phase 0 finding #3 differently than
guessed: `RunnableProjectListener` turned out to be Rider's own *internal* gutter-icon-refresh
listener, not a public extension point). Project removal is detected by diffing full-list
snapshots against the previous one, since `advise` has no per-item add/remove callback.
`RunnableProject`/`ProjectOutput`/`RdTargetFrameworkId` supply `projectFile`/`outputAssemblyPath`
directly; `targetFrameworkMoniker` uses `RdTargetFrameworkId.shortName` (e.g. `net8.0`) since
Rider's model has no classic-MSBuild-moniker field — flagged as a possible follow-up if the
server needs exact format parity. `packageReferences` stays empty (§3.3's `project.assets.json`
follow-up, not yet implemented). Verified via `compileKotlin`/`buildPlugin`/`verifyPlugin`/`test`
inside the devcontainer.

**Phase 3 — File membership — COMPLETE (2026-07-13).** `ReqnrollProjectFilesSync`
(`lsp/project/`), a separate `ProjectActivity` from Phase 2's listener even though both
subscribe to the same `runnableProjectsModel.projects` property (kept self-contained rather
than sharing state). Sends a baseline on every `advise` firing (same "fires immediately +
on every recompute" property Phase 2 relies on) and tracks individual add/remove/rename via
`VirtualFileManager.addAsyncFileListener`, sending deltas.
**Deliberate scope reduction:** project attribution is longest-matching-folder-prefix
(mirrors VS's own `VsProjectEventMonitor.FindProjectContaining`), not a full
`ProjectModelEntity`/`WorkspaceModel` traversal — that class's traversal semantics (linked
files? `Compile Remove` exclusions?) were confirmed to exist but not fully verified, and
guessing wrong there risks sending *incorrect* data, worse than this honest approximation.
So Phase 3 delivers the "real-time file-event refresh" half of `projectFiles`'s value, not
yet the full linked/excluded-file correctness half (VS's issue #32 concern) — revisit with
`ProjectModelEntity` if that turns out to matter in practice.

**Phase 4 — Document activation — COMPLETE (2026-07-13).** `ReqnrollDocumentActivationSync`
ports `DocumentActivationState`'s four-phase dedup state machine verbatim. Driven by
`FileEditorManagerListener.fileOpened`/`fileClosed`/`selectionChanged` rather than raw
`textDocument/didOpen`/`didClose` pipe observation (VS's approach — no such hook exists on
Rider), on the assumption the platform's generic LSP client sends the real `didOpen`/
`didClose` in lockstep with these editor events. Notification URI built via
`VirtualFileManager.constructUrl("file", URLUtil.encodePath(path))`, confirmed by decompiling
`LspServerDescriptor.getFileUri`'s actual bytecode to be the platform's own construction —
not a hand-rolled string, so it matches whatever URI format the same file's `didOpen` used.

Two more real Kotlin/API mistakes caught by compiling rather than eyeballing (Phase 3):
`@Volatile` cannot annotate a local variable (used `AtomicReference` instead, shared between
the `advise` callback and the `AsyncFileListener` callback on a different thread);
`AsyncFileListener.ChangeApplier` has two default methods and zero abstract ones in the
decompiled bytecode, so it isn't SAM-convertible (needed a real anonymous `object`, not a
lambda). Phase 4 compiled successfully on the first try.

---

## 6. Testing

Extends the existing TODO list in `src/Rider/CONTRIBUTING.md`:
- Phase 1 DTOs/interface: plain JUnit round-trip serialization tests (Kotlin object → JSON →
  compare against a fixture captured from the equivalent VS/VS Code payload, to catch camelCase or
  field-name drift early).
- Phases 2-4 listeners: `BasePlatformTestCase`-based tests using IntelliJ's in-memory test project
  fixtures, asserting the right notification fires (via a fake `ReqnrollLanguageServer` proxy
  recording calls) for project open/close, file add/remove/rename, and tab switch — analogous to
  the already-planned `fileOpened` gating test.
- Explicitly deferred (per the existing CONTRIBUTING.md note): a real end-to-end test against the
  actual server, confirming e.g. `projectFiles` absence really does degrade to folder-prefix
  membership as expected — do this once Phase 0-4 land and there's something concrete to break.

---

## 7. Risks

| ID | Risk | Status / Mitigation |
|---|---|---|
| ~~R1~~ | ~~Rider project-model data access requires the RD protocol.~~ | **Resolved, did not materialize** — `RunnableProject`/`ProjectOutput` are already-bundled SDK classes (§3.3). |
| **R2** | `RunnableProjectListener`'s exact firing semantics (Phase 0 finding #3) unconfirmed — may not fire on rebuild specifically. | Confirm empirically in Phase 1 (log every event against the sample project). Fall back to VS Code's `**/bin/**/*.dll` watcher approach if it doesn't. |
| ~~R3~~ | ~~`lsp4jServerClass`/typed-proxy mechanism under-documented.~~ | **Resolved** — verified against decompiled 2024.3.5 bytecode, not just docs (§2, §3.1). Still worth a Phase 1 throwaway smoke test (send one notification, confirm the server receives it) before building the full design on top, since bytecode confirms the API shape but not runtime behavior. |
| **R4** | Degraded-but-functional fallback (folder-prefix membership, no reflection discovery) may already be "good enough" for common cases, making this lower priority than it looks. | Confirm via manual testing in the sample project (`/workspaces/rider-samples`) whether symptoms are actually visible before investing Phase 1-4 effort. |
| **R5** | No dedicated Rider model class found for NuGet package references (new, from Phase 0). | Read `obj/project.assets.json` from disk instead of hunting for a Rider API — see §3.3. |
| **R6** | Whether Reqnroll test projects actually appear in Rider's `RunnableProject` list (new, from Phase 0) — `RunnableProjectKind` is backend-supplied, not a static enum, so this can't be confirmed without running it. | Log `RunnableProject.kind.name` for the sample project in Phase 1; if test projects are excluded, will need a different/additional Rider API for non-runnable project enumeration. |
| **R7** | `verifyPlugin` (Phase 2, real run) flags every LSP API this plugin uses (`LspServerManager`, `LspServerDescriptor`, `LspServerSupportProvider`, and their methods) as `@Experimental` — "can be changed in a future release leading to incompatibilities." | Not actionable now (it's the whole LSP API surface, no stable alternative exists). Just means: re-run `verifyPlugin` after any Rider SDK version bump in `gradle.properties`, since an experimental API is exactly where breakage would show up first. |
| **R8** | `RdTargetFrameworkId` has no classic MSBuild moniker field (".NETCoreApp,Version=v8.0") — only `shortName` ("net8.0") and `presentableName` (".NET 8.0"). Currently sends `shortName` for `targetFrameworkMoniker`. | Confirm whether the server's reflection-based binding discovery actually needs the classic format or just uses this field as an opaque grouping key once there's a real server round-trip to test against (Phase 3/4 or later). |

---

## References

- [Porting-to-VSCode-Rider-Analysis.md](Archive/Porting-to-VSCode-Rider-Analysis.md) §5.3, §6 R1 (archived — superseded by this plan)
- [VSCode-Extension-Implementation-Plan.md](VSCode-Extension-Implementation-Plan.md) — Rider (Phase 2) task table, R5
- `src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/LspNotifications/VsProjectEventMonitor.cs`,
  `VsProjectPayloadBuilder.cs`, `LspProjectPreloadPusher.cs`
- `src/VSCode/src/projectManager.ts`, `src/VSCode/src/msbuildEvaluator.ts`
- `src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/LspMethodNames.cs`,
  `ReqnrollProjectLoadedParams.cs`, `ReqnrollProjectFilesParams.cs`, `ReqnrollProjectUnloadedParams.cs`,
  `Features/DocumentActivated/DocumentActivatedParams.cs`
- `src/Rider/CONTRIBUTING.md` (Testing TODO, Logging sections this plan extends)
