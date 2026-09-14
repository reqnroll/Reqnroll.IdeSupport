# Contributing to the Reqnroll Visual Studio Extension

## Prerequisites

- Visual Studio 2022/2026 with the **Visual Studio extension development** workload, plus the
  **VisualStudio.Extensibility** component (VS.Extensibility is the primary API this extension
  uses; VSSDK is a fallback only for capabilities VS.Extensibility doesn't expose yet — see below)
- [.NET SDK](https://dotnet.microsoft.com/download) 10.0 or later (builds/publishes the LSP server
  bundled into the VSIX — matches the `net10.0` `TargetFramework` used across the solution)
  and the .NET Framework 4.8.1 targeting pack (the extension itself is net481)

## Repository layout

```
src/VisualStudio/
  Reqnroll.IdeSupport.VisualStudio.Extension        ← the VSIX: VS.Extensibility LSP client + commands
    ExtensionEntrypoint.cs                          ← extension entry point, DI service registration
    ReqnrollLanguageClient.cs                        ← LanguageServerProvider (the actual LSP client)
    LspInterception/                                 ← LspServerConnectionService, LspInterceptingPipe,
                                                        per-message interceptors
    LspNotifications/                                ← VsProjectEventMonitor + preload-pipe pusher —
                                                        push DTE project state to the server
    FindStepUsages/, GoToHooks/, StepCodeLens/,
    RenameStep/, CommentToggle/, FindUnusedStepDefinitions/  ← per-feature VS-side client logic
  Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration ← MEF classifications, analytics transmitter,
                                                        VsIdeScope, VSSDK fallback pieces (CodeLens, etc.)
  Reqnroll.IdeSupport.VisualStudio.Wizards(.Core/.UI) ← New Project / New Item wizards, welcome dialog
  Reqnroll.IdeSupport.VisualStudio.ItemTemplates,
  Reqnroll.IdeSupport.VisualStudio.ProjectTemplate  ← VSIX template packaging
```

Start with [docs/LSP-IDE-Support-Architecture.md §6.2](../../docs/LSP-IDE-Support-Architecture.md#62-visual-studio)
for the as-built mechanism (extension activation, eager server startup, the LspInterceptingPipe
send/receive pipelines). [docs/LSP-IDE-Support-Feature-Designs.md](../../docs/LSP-IDE-Support-Feature-Designs.md)
covers each feature's VS-specific surfacing.

## Building

```sh
dotnet build src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/Reqnroll.IdeSupport.VisualStudio.Extension.csproj
```

This **also republishes the LSP server** self-contained (win-x64, net10.0) into the VSIX under
`LSPServer/` (target `IncludeLspServerInVsix`). After any change to `src/LSP/`, rebuild this
project to pick it up before testing in VS — a stale bundled server is a common source of "my fix
doesn't seem to be running" confusion.

## Local install of a dev build

The F5/experimental-instance workflow above is for day-to-day development. To try a build in
your **regular** VS instance (e.g. to hand a build to someone else, or confirm something outside
the experimental hive):

```sh
dotnet build src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/Reqnroll.IdeSupport.VisualStudio.Extension.csproj -c Release
```

No separate `dotnet publish` step is needed — see "Building" above: `LSP.Server.csproj` sets
`RuntimeIdentifier`/`SelfContained` as project properties, so plain `dotnet build` already emits a
self-contained win-x64 server (`coreclr.dll`/`hostfxr.dll` included, not just the managed DLL), and
its `BuildConnector` target (`BeforeTargets="Build"`, not publish-only) stages every supported
Connector TFM into that same build output's `Connectors\` folder. `IncludeLspServerInVsix` bundles
that whole build-output tree — server and connectors together — into the VSIX.

This produces
`src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/bin/Release/net481/Reqnroll.IdeSupport.VisualStudio.Extension.vsix`,
with the bundled LSP server built `Release` (quiet default logging — see "Server log-level and
trace defaults" below) rather than the `Debug`/`Verbose` build `dotnet build` alone (no `-c`)
produces.

Double-click the `.vsix` (or run it via `VSIXInstaller.exe`) to install it into your main VS —
**not** the experimental instance. If the Preview extension is already installed, VSIX Installer
offers to update it in place. As with any manual VSIX install, uninstall via **Extensions → Manage
Extensions** when you're done testing, and restart VS afterward.

## Running and debugging in VS

The extension deploys into VS's **experimental instance** (a separate hive, e.g.
`…\AppData\Local\Microsoft\VisualStudio\<ver>_<id>Exp\Extensions\<id>\`), not your everyday VS.
Launch it via **Debug → Start New Instance** (or F5) from the Extension project — this starts a
second `devenv.exe` with the extension loaded, isolated from your main VS install/extensions.

Runtime logs land in `%LocalAppData%\Reqnroll\`, one file per process (the PID in the filename is
the writing process's own — see [../LSP/CONTRIBUTING.md#debugging](../LSP/CONTRIBUTING.md#debugging)
for the full naming/format convention shared across every log in this family):

- `reqnroll-vs-ext-debug-<date>-<pid>.log` — the **extension's own** (client-side) log output.
  **You will often see more than one of these for a single session with the same date** — this is
  expected, not a bug. `Run CodeLens` (and the sibling Hook CodeLens) run out-of-process in VS's own
  CodeLens ServiceHub host (`RunTestCodeLensDataPointProvider` and friends, issue #372), a different
  PID than `devenv.exe`, and deliberately log to their own standalone file rather than the shared
  `IIdeSupportLogger` sink, since they can't reach it across the process boundary. Match the PID in
  the filename against `tasklist`/Task Manager (`ServiceHub.Host.*.exe` vs. `devenv.exe`) if you need
  to tell them apart.
- `reqnroll-vs-server-debug-<date>-<pid>.log` — the **LSP server's own** log output (parses,
  discovery, handler activity), at the level set by `--log-level` (see below). Appended across
  server process launches sharing a day and PID is generally stable per VS session, but check the
  `=== Reqnroll LSP Server started — …, PID N ===` banner line when correlating multiple restarts.
- `reqnroll-vs-inspector-<datetime>.log` — client-side JSON-RPC trace from `LspInspectorLogger` on
  the `LspInterceptingPipe`, one line per message. This is the source of truth for what actually
  crossed the wire (legend negotiation, semanticTokens requests/responses, custom `reqnroll/*`
  traffic) — [lsp-inspector-tool](https://github.com/microsoft/lsp-inspector) compatible format.
- `reqnroll-lsp-connector-<date>-<pid>.log` — the **out-of-process Connector's** own log, one file
  per discovery-run child process. Unlike the logs above, this one usually doesn't exist: it's
  buffered in memory and only written when a discovery run actually fails, or when `--log-level` is
  raised to `Info`+ (see [../LSP/CONTRIBUTING.md](../LSP/CONTRIBUTING.md#connector-logging-buffered-and-gated-by---log-level-not-a-separate-switch)
  for the full mechanism). At the DEBUG-configuration/`--log-level Verbose` default below, it *will*
  be written for every discovery run — don't be surprised to see one per project per session while
  F5-debugging the extension.

When debugging coloring/binding/CodeLens behavior, the ext-debug and server-debug logs together
usually tell the whole story; the inspector log is what to reach for when you suspect a protocol
mismatch specifically, and the connector log (when present) is the first place to look for a
discovery-specific failure — its "Discovery complete"/"Discovery failed" line in the server log
carries a `(connector pid=N)` suffix matching that file's own PID.

### Server log-level and trace defaults

`LspServerConnectionService.ServerArguments` is the full command line the extension passes to the
LSP server process, including the three verbosity flags described in
[../LSP/CONTRIBUTING.md](../LSP/CONTRIBUTING.md#server-logging-and-trace-verbosity)
(`--log-level`, `--protocol-log-level`, `--trace`). It's build-configuration-dependent:

- **DEBUG** (a developer F5-ing the Extension project): `--log-level Verbose --protocol-log-level
  Info --trace Verbose` — the chattiest reasonable defaults across all three.
- **RELEASE** (an installed VSIX, what real users run): `--log-level Warning --protocol-log-level
  Warning --trace Off` — quiet by default.

Unlike VS Code's `reqnroll.trace.server` setting, **VS currently has no end-user UI to raise these
levels at runtime.** To get more verbose logs out of a RELEASE build for a bug report, either
change `ServerArguments` and rebuild, or use the DEBUG configuration directly.

## Testing

```sh
dotnet test tests/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Tests/Reqnroll.IdeSupport.VisualStudio.Tests.csproj
```

`Reqnroll.IdeSupport.VisualStudio.Tests` (net481, xUnit + NSubstitute + AwesomeAssertions — note: `Should()`
is AwesomeAssertions, not FluentAssertions; signed with `reqnroll.snk`) is the home for VS-client
unit tests. It reaches `internal` types via `InternalsVisibleTo` (see the
`AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo"` entries in the
target csproj files).

**Testing philosophy for this project — read before writing a new test here.** The VS extension
is a *thin LSP client*: most of its services (`FindStepUsagesService`, `CommentToggleService`,
`StepCodeLensService`, etc.) mostly serialize parameters and send them over `LspInterceptingPipe`
to the server — the actual behavior lives server-side.

- **Behavior** (toggle/format/rename/find-usages logic) belongs in the LSP server's specs
  (`tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs`), not here.
- **Client parse/transform logic** — mapping a raw JSON-RPC response into a view model, building a
  request payload — belongs here, in `Reqnroll.IdeSupport.VisualStudio.Tests`.
- **VS-COM glue** (Running Document Table / `IVsTextLines` buffer writes, `IVsFindAllReferences`
  table controls, DTE navigation, package autoload) generally isn't unit-testable — it needs a real
  VS host. Don't force a mock-heavy test around COM/`ThreadHelper.JoinableTaskFactory` code just to
  get coverage; a documented manual/smoke-test note in the PR description is more honest than a
  test that mocks away the only thing worth verifying.
- When client-side logic *is* worth testing, extract the pure part into a small `internal static`
  method (see `RenameStepService.ParseWorkspaceEdit`, `FindStepUsagesService.MapResult`,
  `WorkspaceEditApplier.ApplyEditsToText` for the established pattern) so the transport (the pipe)
  is separable from the mapping you actually want to verify.

**Do not port the legacy `Reqnroll.VisualStudio.Specs` feature files** to test this extension —
those scenarios test behavior that now lives server-side and is already covered by the LSP specs.

## Conventions specific to this codebase

- **Gate every VS-specific workaround behind `ClientIdeContext.IsVisualStudio`** (or the
  equivalent flag), even in server-side code that happens to be triggered from here — a fix for a
  VS quirk should never silently change behavior for VS Code/Rider.
- **VS.Extensibility contribution classes are not documented as injectable into each other.**
  If one `[VisualStudioContribution]` class needs data another one owns, register a small mutable
  "state holder" singleton in `ExtensionEntrypoint.InitializeServices` and inject that into both,
  rather than trying to inject one contribution into another's constructor — the latter fails
  silently (no exception in `ActivityLog.xml`, the command just never dispatches).
- **`ReqnrollLanguageClient.OnServerInitializationResultAsync` may run on a background thread.**
  Any DTE/COM access there (or anywhere else that isn't guaranteed on the UI thread already) needs
  an explicit `await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(...)` first.
- **`LspServerConnectionService` starts the server process eagerly**, resolved from
  `ExtensionEntrypoint.OnInitializedAsync` rather than from `ReqnrollLanguageClient`'s constructor
  — the latter is only constructed when VS actually activates the `LanguageServerProvider` (i.e.
  on `.feature`-file open), which turned out not to be early enough. See the as-built note in the
  Architecture doc before changing this — the current design was arrived at after two rounds of
  the "obvious" approach turning out not to actually be eager, verified against real VS session
  logs both times.
