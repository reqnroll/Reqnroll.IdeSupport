# Getting the VS LSP client to activate before the user touches a `.feature` tab

**Status:** research note, **superseded in part by measurement**. See §0 before relying on
anything below.
**Date:** 2026-08-30 (findings added the same day)
**Problem as originally stated:** a `.feature` file left open as the foreground tab from the
previous session gets no LSP features until the user clicks, navigates, or types in it.
**Tracked in:** [#533](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/533)

---

## 0. Findings — what measurement changed

Phases 0–2 of the plan in #533 were implemented and run a dozen-plus times in the experimental
instance. Three corrections, in order of importance. **Read them before §1–§3, which are kept as
originally written and are wrong in the places noted.**

### The everyday symptom does not exist

On a warm start, with a `.feature` file as the only restored tab, VS activates the
`LanguageServerProvider` **1.1–1.4 s after extension load, every run, on unmodified master**, with
no user interaction. Eleven-plus runs across both branches. There is no "restored tab is dead until
you click it" bug.

### The stub-frame root cause in §1 is refuted

The restored `.feature` document is already **fully initialized ~76 ms after extension load, with
zero stubs**, and its view has a Gherkin drop-down bar attached 100 ms later. VS's `[Stub]` tab
caption (§4) was enabled and never appeared — correctly, because nothing was ever a stub. Delayed
document loading is real, and it is not what was happening here.

### The real bug: activation is edge-triggered on document *open*

There is a genuine defect, and it is much narrower. On the **first launch after an install or
update**, an already-open `.feature` file never activates the provider for that entire session.
Measured on a cold run, one user action at a time:

| Action | Result |
|---|---|
| Click the tab header | `window show (firstShow=False)` — no activation |
| Click inside the file | `window show (firstShow=False)` — no activation |
| Switch to Test Explorer and back | hide + show — no activation |
| Edit the feature | no activation |
| **Close and reopen the file** | new document locks + `firstShow=True` → **activation 233 ms later** |

So **VS activates a `LanguageServerProvider` on the document-open edge and never re-evaluates
documents that are already open.** A document open at a moment when VS does not yet know the
provider exists — which is the case while the contribution cache is rebuilt after a deploy — stays
invisible to it for the life of the session. (That last clause is inference; the edge behaviour
itself is measured.)

### What this does to the techniques in §3

| | Verdict |
|---|---|
| **T1** — broaden `AppliesTo` | Implemented and kept, but **not load-bearing**: the feature-only runs had no C# document open at all, so the Gherkin filter alone was doing the work. Kept as cover for the C#-only restore case, at the cost of activating in non-Reqnroll solutions. |
| **T2** — realize stub frames | **No premise.** There are no stubs to realize. Not built. |
| **T3** — `LoadedWhen` | Implemented, measured, **reverted**. It changed nothing. (It was briefly also blamed for regressing cold starts; that charge was withdrawn when a build without it failed the same way.) |
| **T4** — RDT event sink | Implemented and kept as `DocumentInitializationMonitor`. Every conclusion here rests on it. |
| **T5** — server prewarm | Not built. Still the only idea that would help the cold case, and only by shortening it. |
| **T6** — upstream ask | **Now the main lever.** VisualStudio.Extensibility has no equivalent of `ILanguageClientBroker.LoadAsync`, so an extension cannot recover from a missed activation edge. §2.1 and §2.2 below are still accurate and are the substance of that report. |

The market survey in §2 stands unchanged — it was never about the mechanism.

---

## 1. Where the delay actually comes from — **REFUTED, see §0**

There are three separate gates between "VS starts" and "the restored `.feature` tab has LSP
features". Only one of them is still open.

| # | Gate | Owner | Reqnroll status |
|---|------|-------|-----------------|
| 1 | Extension assembly loaded | `ReqnrollPluginPackage` (`[ProvideAutoLoad(SolutionExists)]`) + `ExtensionEntrypoint.OnInitializedAsync` | **Solved.** Server process + pipe are launched eagerly at extension load. |
| 2 | VS *activates* the `LanguageServerProvider` (calls `CreateServerConnectionAsync`, runs `initialize`) | Visual Studio, gated on an **initialized document** matching `LanguageServerProviderConfiguration.AppliesTo` | **Open.** This is the whole problem. |
| 3 | `textDocument/didOpen` for each restored tab | Visual Studio | **Fine once gate 2 opens** — `DocumentActivationState`'s notes record that VS sends `didOpen` for every restored tab at solution load, not lazily on click. |

Gate 2 stays shut because of **delayed document loading**. When VS reopens a solution, restored
documents are *not* loaded: the window frame is created in a pending-initialization state and a
placeholder ("stub frame") goes into the Running Document Table. The stub is fully initialized
only when the user accesses the document — selecting the tab — or when an extension asks for its
doc data. So the restored `.feature` tab looks open but is not an initialized document, VS never
considers a matching document type to have been opened, and the `LanguageServerProvider` is never
activated. ([Delayed document loading](https://learn.microsoft.com/en-us/visualstudio/extensibility/internals/delayed-document-loading?view=visualstudio))

`VsStubFrameInitializer` already knows how to realize those stubs — but it is called from
`ReqnrollLanguageClient.OnServerInitializationResultAsync`, i.e. *after* gate 2 has already
opened. Chicken and egg: it can flush the background stubs, but it can never open the gate.

The class remarks also record a previous attempt (an "invisible open" that force-activated the
provider) that was reverted because it raced VS's own tab restore — two server processes,
flickering C# code lenses. Any technique below has to be judged against that failure mode.

---

## 2. What the rest of the market does

### 2.1 Classic MEF `ILanguageClient` extensions (the large majority of shipping VS LSP extensions)

Microsoft's own documentation states the constraint plainly: *"Currently, the only way to load
your LSP-based language server extension is by file content type… If no files that match your
defined content type are opened, then your extension won't be loaded."*
([Add a Language Server Protocol extension](https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension?view=visualstudio))

The near-universal workaround is a **companion `AsyncPackage` that force-loads the client through
`ILanguageClientBroker`**:

```csharp
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class MyPackage : AsyncPackage
{
    protected override async Task InitializeAsync(CancellationToken ct, IProgress<ServiceProgressData> p)
    {
        var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        var broker = componentModel.GetService<ILanguageClientBroker>();
        var client = componentModel.GetExtensions<ILanguageClient>().OfType<MyLanguageClient>().First();

        if (!client.Loaded)                                   // guard against double-load
            await broker.LoadAsync(new MyClientMetadata(), client);
    }
}
```

Observed in the wild:

* **Roslyn** — `AlwaysActiveLanguageClientEventListener` (`src/EditorFeatures/Core/LanguageServer/`).
  An `IEventListener` on the host workspace calls `ILanguageClientBroker.LoadAsync` on
  `WorkspaceChangeKind.SolutionAdded`. Its comment is the canonical statement of the pattern:
  normally VS loads the language client when an editor window is created for one of our content
  types, but it wants the client loaded as soon as a solution is loaded — so workspace diagnostics
  work and third parties (Razor) can use dynamic registration. It has to re-implement
  `ILanguageClientMetadata` because the framework's implementation is not public (tracked
  internally for removal). It also unloads on `SolutionRemoved` to avoid reload races.
* **HLSL-LSP** (`KStocky/HLSL-LSP`, `clients/visual-studio/.../HlslLspActivator.cs`) — same shape:
  a package-side activator constructs the client itself and calls `broker.LoadAsync(metadata, client)`.
* The pattern is also the accepted answer on Microsoft Q&A for
  ["How to load Language Server (LSP) when loading the extension package"](https://learn.microsoft.com/en-us/answers/questions/4374660/how-to-load-langauge-server-(lsp)-when-loading-the),
  including the double-load guard (`MarkAsManuallyLoaded` / `IsLoaded`) so the manual load and the
  later content-type-triggered load don't start two servers.

Scoping of the autoload is done with **rule-based UI contexts** (`ProvideUIContextRule`) rather
than blanket `SolutionExists`, so the package only wakes up in solutions that plausibly contain
the language.

### 2.2 VisualStudio.Extensibility (`LanguageServerProvider`) — what Reqnroll uses

The new model has **no public equivalent of `ILanguageClientBroker.LoadAsync`**. As of
`Microsoft.VisualStudio.Extensibility.Contracts` 17.14, `LanguageServerProviderConfiguration`
exposes only a display name and `AppliesTo` (`DocumentFilter[]`), and `LanguageServerProvider`
exposes `Enabled` — documented as *"an enabled language server is allowed to 'activate' once an
applicable document type is opened"*, and setting it to `false` stops running servers. There is
no "activate now". ([Language Server Provider](https://learn.microsoft.com/visualstudio/extensibility/visualstudio.extensibility/language-server-provider/language-server-provider?view=visualstudio))

So in the new model the only levers are:

* **breadth of `AppliesTo`** — which document types can open the gate;
* **`DocumentFilter.FromGlobPattern(pattern, relativePath)`** as an alternative to document types;
* **`ExtensionConfiguration.LoadedWhen`** activation constraints — controls when the *extension*
  loads, not when the LSP provider activates;
* **making a document real**, i.e. defeating the stub frame — which requires classic VSSDK APIs
  (RDT / `IVsWindowFrame`), not VS.Extensibility. Reqnroll already ships the hybrid package that
  can do this.

### 2.3 For contrast — VS Code and Rider

VS Code extensions solved this years ago declaratively: `activationEvents` of
`onLanguage:<id>`, `workspaceContains:**/*.feature`, and `onStartupFinished`. The market norm
outside VS is *activate on workspace shape, not on editor focus*. The VS equivalent of
`workspaceContains` is the rule-based UI context / `ActivationConstraint.ProjectAddedItem`
family — which VS gives us for **package/extension load** but not for **LSP activation**.

---

## 3. Techniques worth trying, in the order I'd try them

### T1 — Broaden `AppliesTo` so activation isn't `.feature`-gated — **shipped, not load-bearing**

```csharp
public override LanguageServerProviderConfiguration LanguageServerProviderConfiguration =>
    new("Reqnroll Language Client",
        new[]
        {
            DocumentFilter.FromDocumentType(GherkinDocumentType.GherkinDocument),
            DocumentFilter.FromDocumentType("CSharp"),   // <-- new
        });
```

A restored `.cs` tab (very common in the foreground) then opens gate 2, and the `.feature` tab's
`didOpen` arrives as soon as its frame is realized. This is cheap and needs no VSSDK plumbing, and
it removes the dependency on *which* tab happens to be foreground.

Considerations before doing it:

* Traffic is a smaller concern than it first appears. The server already receives and handles
  `.cs` `didOpen`/`didChange` — the binding-discovery pipeline is built on them — so broadening
  `AppliesTo` changes which documents the VS client forwards, not whether the server knows what to
  do with them. There is no new handling to write. An inspector-log comparison on a large solution
  is still worth doing to size the *volume* against the in-flight `.cs` staleness work.
* `DocumentFilter.FromGlobPattern("**/*.cs", relativePath: true)` is a narrower alternative if the
  `CSharp` document type pulls in more than we want.
* It does **not** help the "only a `.feature` tab was restored" case — that still needs T2.

**Risk:** low — volume only, no new server behaviour. **Payoff:** covers the common mixed-tabs
restore for a two-line change, which is why it is worth trying before T2. Complement, not a
replacement.

### T2 — Realize restored `.feature` stub frames from the autoloaded package, after solution load — **NO PREMISE, see §0**

Directly attacks the documented root cause. Move (a copy of) the `VsStubFrameInitializer` work out
of `OnServerInitializationResultAsync` and into `ReqnrollPluginPackage`, driven by
`IVsSolutionLoadEvents.OnAfterBackgroundSolutionLoadComplete` (or the
`SolutionExistsAndFullyLoaded` UI context) rather than by LSP activation.

Do it the way the docs prescribe, so we don't accidentally initialize documents we don't want:

1. Enumerate with `IVsRunningDocumentTable4`, and use `GetDocumentFlags` →
   `_VSRDTFLAGS4.RDT_PendingInitialization` to find stubs. **Do not** call
   `IVsRunningDocumentTable.GetDocumentInfo` for the scan — it always materialises doc data, which
   is exactly the "extension forces unnecessary initialization" anti-pattern the doc warns about
   (the current implementation does call it).
2. Realize **only** the frame(s) that matter — start with the active/visible one — via
   `IVsWindowFrame.GetProperty(VSFPROPID_DocData)` or `Show()`.
3. One-shot and idempotent: a flag so a later `didOpen`-driven path can't run it again.

Differences from the reverted attempt, which are the whole point: this realizes a frame VS has
*already restored* (it does not synthesise an invisible open), and it fires after background
solution load has completed rather than during the restore, so there is no window in which it
races VS's own restore. Keep the existing `LspServerConnectionService` caching in mind — a second
`CreateServerConnectionAsync` must not be handed an already-consumed pipe.

**Risk:** medium (this is the code path that broke before). **Payoff:** highest — it is the only
option that makes the *foreground restored `.feature` tab itself* the activation trigger.

### T3 — Make extension load deterministic with `ExtensionConfiguration.LoadedWhen` — **tried and reverted**

Today `ExtensionEntrypoint`'s remarks admit the eager startup depends on *whichever contribution VS
activates first* — in practice `StepCodeLensProvider` when a `.cs` file opens. That's accidental.
Declaring it removes the accident:

```csharp
public override ExtensionConfiguration ExtensionConfiguration => new()
{
    RequiresInProcessHosting = true,
    LoadedWhen = ActivationConstraint.SolutionState(SolutionState.FullyLoaded)
                 | ActivationConstraint.ProjectAddedItem(@"\.feature$"),
};
```

This does **not** activate the LSP provider by itself — but it makes gate 1 and the T2 timer fire
on a defined signal instead of a coincidence, which is what makes T2 reproducible.
([Rule-based activation constraints](https://learn.microsoft.com/visualstudio/extensibility/visualstudio.extensibility/inside-the-sdk/activation-constraints?view=visualstudio))

**Risk:** low. **Payoff:** indirect, but a prerequisite for trusting T2's timing.

### T4 — Subscribe to RDT stub→initialized events instead of polling

Whether or not we force anything, MS documents `IVsRunningDocTableEvents2.OnAfterAttributeChangeEx`
as the way to learn when a pending-initialization document becomes real. A sink on the package
gives us a precise hook for "a `.feature` document just became real" — useful both as the trigger
for follow-up work and as instrumentation to prove where the current latency sits.

**Risk:** very low. **Payoff:** mostly diagnostic, but cheap.

### T5 — Prewarm the server side so the first `didOpen` is instant

Independent of activation timing: at `initialize`, have the server discover and pre-parse the
workspace's `.feature` files and build the binding registry from disk, so when `didOpen` finally
arrives the response is a cache hit. This doesn't make features appear *before* the click, but it
collapses the click-to-paint delay. Partly done already via eager process launch; the remaining
piece is warming the document/binding caches rather than just the process.

**Risk:** low. **Payoff:** fallback value if T2 stays unsafe.

### T6 — Ask Microsoft for the missing API

There is no VS.Extensibility counterpart to `ILanguageClientBroker.LoadAsync`. Worth an issue on
[microsoft/VSExtensibility](https://github.com/microsoft/VSExtensibility/issues) asking for an
explicit activation entry point on `LanguageServerProvider` (or for restored-but-stub documents to
count as opened). Roslyn needed exactly this in the old model and got the broker; the new model
regressed it. Long lead time — ship T1/T2 regardless.

### Not recommended

* **Dropping back to classic `ILanguageClient` just to get the broker.** It would work, and it is
  what the rest of the market uses — but it means giving up the VS.Extensibility contributions
  (CodeLens, commands, document types) the extension is already built on. Only reconsider if T1
  and T2 both fail.
* **Toggling `Enabled` false→true to force re-activation.** Still document-gated, and the setter
  stops any running server. It cannot manufacture an activation.

---

## 4. How to verify

* **See the stubs.** Set `StubTabTitleFormatString` to `{0} [Stub]` under
  `HKEY_CURRENT_USER\Software\Microsoft\VisualStudio\<version>\BackgroundSolutionLoad`
  (the doc's example uses `14.0`; for VS 2022 use the `17.0_<instanceid>` private hive). Restored
  tabs that have not been initialized show `[Stub]` in the title. This alone will confirm or refute
  the whole diagnosis in section 1 in one launch.
* **Timeline from our own logs.** `ExtensionEntrypoint.OnInitializedAsync` →
  `LspServerConnectionService` launch → `ReqnrollLanguageClient` ctor → `CreateServerConnectionAsync`
  → `OnServerInitializationResultAsync` → first `didOpen`. The gap that matters is ctor → first
  `didOpen` versus wall-clock user click. Note the known cold-start caveat: a first launch after
  deploy is ~14.6 s with no LSP features and is *not* a regression — compare warm runs only.
* **Scenarios to cover:** (a) only a `.feature` tab restored; (b) `.feature` restored but a `.cs`
  tab foreground; (c) several `.feature` tabs restored; (d) no solution, single file open.
  Scenario (c) is the one that previously produced the two-server bounce.

---

## Sources

* [Delayed document loading](https://learn.microsoft.com/en-us/visualstudio/extensibility/internals/delayed-document-loading?view=visualstudio)
* [Add a Language Server Protocol extension](https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension?view=visualstudio)
* [Create an Extensible Language Server Provider (VisualStudio.Extensibility)](https://learn.microsoft.com/visualstudio/extensibility/visualstudio.extensibility/language-server-provider/language-server-provider?view=visualstudio)
* [Rule-based activation constraints](https://learn.microsoft.com/visualstudio/extensibility/visualstudio.extensibility/inside-the-sdk/activation-constraints?view=visualstudio)
* [How to load Language Server (LSP) when loading the extension package — Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/4374660/how-to-load-langauge-server-(lsp)-when-loading-the)
* [dotnet/roslyn — AlwaysActiveLanguageClientEventListener.cs](https://github.com/dotnet/roslyn/blob/main/src/EditorFeatures/Core/LanguageServer/AlwaysActiveLanguageClientEventListener.cs)
* [KStocky/HLSL-LSP — HlslLspActivator.cs](https://github.com/KStocky/HLSL-LSP/blob/main/clients/visual-studio/HlslLsp.VisualStudio/HlslLspActivator.cs)
* [microsoft/VSExtensibility](https://github.com/microsoft/VSExtensibility)
