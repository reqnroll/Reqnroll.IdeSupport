# Reqnroll Rider Plugin

Kotlin-only IntelliJ Platform plugin that runs the Reqnroll.IdeSupport LSP server inside
Rider. It registers a `.feature` file type/language and an `LspServerSupportProvider`
(`src/main/kotlin/com/reqnroll/ide/rider/lsp`) — there is no ReSharper-SDK/.NET-backend
half; the IntelliJ Platform's built-in `com.intellij.platform.lsp.api` framework talks to
the server directly over stdio.

Beyond what that generic framework wires up automatically (semantic token coloring, go to
definition, code actions), a few LSP features have no rendering-side consumer in Rider's
platform at all (confirmed by decompiling — only capability-name bookkeeping exists for
`textDocument/codeLens` and `textDocument/inlayHint`), so this plugin calls those requests
directly (`ReqnrollRequestSender`) and renders through IntelliJ's own native extension
points instead: a `CodeVisionProvider` (step-usage-count lenses in `.cs` files) and, for
`.feature` files, a hand-managed `Editor.inlayModel` (`ReqnrollFeatureInlayHintsController`)
rather than the declarative `InlayHintsProvider` framework — that framework dispatches by PSI
language, but `.feature` files have no `ParserDefinition` registered (confirmed live: a
declarative provider registered for it was silently never invoked; see that class's doc
comment for the full story). Two custom `AnAction`s (Find Unused Step Definitions, Find Step
Usages — `com/reqnroll/ide/rider/actions`) surface the equivalent custom `reqnroll/*` requests
that have no standard-LSP client hook at all.

## First-time setup

The Gradle wrapper (`gradlew`, `gradlew.bat`, `gradle/wrapper/`) is committed and
Dependabot-managed — see `distributionUrl` in `gradle/wrapper/gradle-wrapper.properties`
for the exact version currently pinned, rather than trusting a number here, since
Dependabot bumps it independently of this file. Just use `./gradlew` directly, no
bootstrap step needed, on either track below.

If the wrapper ever needs regenerating by hand (rare — Dependabot normally does this),
do it from a machine/container with a system `gradle` preinstalled for exactly that:
`gradle wrapper --gradle-version <version>`. **Don't** use that system `gradle` for
anything else — a system Gradle install can be materially ahead of what
`build.gradle.kts` is written against. (Historical incident, 2026-07: Gradle 9's
`Project.exec()`/Kotlin-DSL changes broke this script when CI briefly evaluated it
under a system Gradle a full major version ahead of the then-committed wrapper — the
wrapper has since been bumped past that point by Dependabot, but the lesson about not
mixing a system `gradle` with the wrapper still holds regardless of which versions are
current.)

There are two tracks, depending on what's already on your machine — pick one:

- **Native toolchain (recommended if you can install a JDK)** — this is what CI already
  uses (`ubuntu-latest` + `actions/setup-java`), so it's the better-trodden, faster path
  if it's available to you:
  - JDK 21 on `PATH` (or `JAVA_HOME` pointing at one) — matches `kotlin { jvmToolchain(21) }`.
  - `dotnet` SDK on `PATH`, for `publishServer` (see "Bundling the LSP server" below).
  - **No local Rider install needed even here** — the IntelliJ Platform Gradle plugin
    downloads the pinned Rider SDK version (`platformVersion` in `gradle.properties`)
    into `~/.gradle/caches` automatically on first `runIde`/build. Large (several GB),
    one-time, needs network access.
  - `./gradlew runIde` then launches a sandboxed Rider window directly on your desktop —
    no container, no X11 forwarding, no bind-mount indirection.
- **Devcontainer** — for a constrained workstation without permission or disk space to
  install a JDK/Rider SDK locally (e.g. a locked-down corporate Windows machine). Slower
  and more indirect (WSLg X11 forwarding, cross-host server publishing), but needs
  nothing installed on the host beyond Docker/VS Code Dev Containers. See "Manual
  verification — devcontainer" below for the full setup.

## Build / run

```
./gradlew buildPlugin   # produces build/distributions/*.zip
./gradlew runIde         # launches a sandboxed Rider instance with the plugin loaded
```

If `src/Rider` is open as the VS Code workspace folder, `.vscode/tasks.json` wraps
`./gradlew runIde` as the default build task (Ctrl+Shift+B / Cmd+Shift+B).

### Debugging from VS Code

Gradle launches the sandbox in its own separate JVM/Rider process, so plain `runIde`
gives VS Code nothing to attach to. To get a real debug target instead of reasoning from
logs (see "Logging" below) or a temporary `Thread.sleep`:

1. Run the **`Gradle: runIde (debug)`** task (Terminal → Run Task…, or
   `Ctrl+Shift+P` → "Tasks: Run Task"). It's the same as the default task but adds
   Gradle's standard `--debug-jvm` flag, which suspends the sandbox's JVM on debug port
   5005 until a debugger connects.
2. Wait for the terminal to print `Listening for transport dt_socket at address: 5005`.
3. Press **F5** — the **Attach to Rider sandbox** launch configuration
   (`.vscode/launch.json`, type `java`, request `attach`, port 5005) connects and the
   suspended sandbox resumes. Set breakpoints in `.kt` source before or after attaching;
   both work once connected.

Requires a Java debugger extension — `vscjava.vscode-java-debug`, included in the
devcontainer's `customizations.vscode.extensions` (rebuild the container after pulling
this change to pick it up); install it manually if you're on the native toolchain track
and don't already have one.

`vscode-java-debug` + `fwcd.kotlin` isn't an officially supported combination for Kotlin
specifically (as opposed to Java) breakpoints — if a breakpoint in a `.kt` file doesn't
bind or hit reliably, fall back to `ReqnrollDebugLogger`'s file logging instead.

## Bundling the LSP server

`ReqnrollServerPathResolver` expects the published server under
`server/<rid>/Reqnroll.IdeSupport.LSP.Server[.exe]` inside the plugin's own install
directory, for whichever RID matches the OS Rider is actually running on — so a real
distributable build needs every supported RID bundled at once, mirroring the layout
`src/VSCode`'s build produces (one universal, OS-detecting package).

There are two ways to populate `server/<rid>/`, both wired up in `build.gradle.kts`:

- **Local dev** — the `publishServer` task runs `dotnet publish` on
  `src/LSP/Reqnroll.IdeSupport.LSP.Server` for the host RID (override with
  `-PserverRid=<rid>`, e.g. `linux-x64`/`osx-arm64`) into `server/<rid>/` here
  (gitignored, like `src/VSCode/server`). `prepareSandbox` depends on it, so this is
  automatic:

  ```
  ./gradlew runIde                              # bundles the host RID only
  ./gradlew buildPlugin -PserverRid=linux-x64   # cross-publish a different single RID
  ```

  Requires the .NET SDK (`dotnet`) on `PATH`. On the native toolchain track this is
  normally already there if you do any .NET development. The devcontainer does *not*
  currently include one (Rider's own bundled backend/Test Explorer inside it has its own
  separate .NET runtime it manages independently, which does *not* put `dotnet` on the
  container's `PATH` for Gradle to find) — run `publishServer` from the Windows host
  instead (see "Manual verification — devcontainer" below), or add the SDK to
  `docker/dev.Dockerfile` if you need it to work inside the container directly.

  `runIde` specifically publishes with `--configuration Debug` (detected from
  `gradle.startParameter.taskNames`); `buildPlugin`/CI publish `Release`. `runIde`'s
  sandboxed IDE process also gets a `reqnroll.devSandbox=true` system property, which
  `ReqnrollLspServerDescriptor` reads to launch the server with `--log-level Verbose`
  instead of the `Warning` a real installed plugin uses — so local dev gets a Debug
  server build and full diagnostic logging with zero manual configuration.

- **CI** (see `.github/workflows/ci.yml`'s `build-rider-plugin` job) — passes
  `-PlspServerBuildDir=<dir>`, where `<dir>` contains a `win-x64/`, `linux-x64/`,
  `osx-x64/`, `osx-arm64/` subdirectory (populated from the `server-<rid>` artifacts
  `test-lsp.yml` already built and tested). `publishServer` is skipped entirely in this
  mode — Gradle never needs `dotnet` on the CI runner — and `prepareSandbox` bundles
  every RID found under `<dir>` instead of just one.

## Manual verification

### Native toolchain

If you have JDK 21 and `dotnet` on `PATH` (see "First-time setup" above), this is just:

```
cd src/Rider
./gradlew runIde
```

A sandboxed Rider window appears directly on your desktop — no container, no X11
forwarding, first run downloads the Rider platform SDK (large, one-time). Skip straight
to "What to check" below.

### Devcontainer (no local JDK/Rider install)

This is the fallback for a constrained workstation — it runs Rider
headless-from-the-container's-perspective, displayed on the host desktop over WSLg's X11
forwarding (`DISPLAY=:0`). All verification below happens *inside* the container, except
publishing the server (the container has no .NET SDK — see "Bundling the LSP server"
above — so publish from the Windows host, into the bind-mounted repo, and point Gradle
at it exactly the way CI does).

1. **Rebuild the container** in VS Code (`Dev Containers: Rebuild and Reopen in
   Container`) after any `docker/dev.Dockerfile` change. The image needs
   `libxext6`/`libxrender1`/`libxtst6`/`libxi6`/`libxrandr2` for the JetBrains
   Runtime's AWT/Swing toolkit to initialize at all — without them `runIde` fails
   immediately with `UnsatisfiedLinkError: libXext.so.6: cannot open shared object
   file` before a window ever appears. It also needs `libicu-dev`/`libssl-dev`/
   `zlib1g` for Rider's own .NET (CoreCLR) backend process — separate from anything
   our plugin does, Rider always spawns one alongside the JVM frontend — without
   which that backend SIGABRTs (`exit code 134`) right after the frontend loads.
2. **From the Windows host** (has `dotnet`), publish the server for `linux-x64` — that's
   the RID the container needs, since Rider itself runs inside it regardless of the
   host OS:
   ```
   dotnet restore src/LSP/Reqnroll.IdeSupport.LSP.Connector/Connector/Connector.csproj --runtime linux-x64
   dotnet publish src/LSP/Reqnroll.IdeSupport.LSP.Server/Reqnroll.IdeSupport.LSP.Server.csproj --configuration Release --runtime linux-x64 --self-contained true --output src/Rider/downloaded-server/linux-x64
   ```
3. **Inside the container**, the published binary needs its executable bit set — Windows
   bind mounts don't reliably preserve it:
   ```
   chmod +x src/Rider/downloaded-server/linux-x64/Reqnroll.IdeSupport.LSP.Server
   ```
4. **Inside the container**, bootstrap the Gradle wrapper once (see "First-time setup"
   above), then launch the sandbox:
   ```
   cd src/Rider
   ./gradlew runIde
   ```
   `devcontainer.json` sets `ORG_GRADLE_PROJECT_lspServerBuildDir` in `containerEnv`,
   so Gradle automatically uses the CI-style external build dir above and never needs
   `dotnet` — no `-P` flag required here.
5. First run downloads the Rider platform SDK (large, one-time). A sandboxed Rider
   window should eventually appear on the Windows desktop via WSLg.

### Testing against a real host solution (optional)

For manual testing against an actual .NET solution outside this repo (e.g. Reqnroll's
Quickstart sample) rather than a scratch project under `/workspaces/rider-samples`, bind
mount it in. The host path is inherently machine-specific, so it isn't a fixed entry in
the committed `devcontainer.json` — add your own `mounts` line locally instead:

1. Set an environment variable on the **host** (Windows) pointing at the solution's
   folder, e.g. in PowerShell:
   ```
   [Environment]::SetEnvironmentVariable("REQNROLL_HOST_SOLUTION_DIR", "C:\Users\you\source\repos\Quickstart", "User")
   ```
   (restart your terminal/VS Code afterward so the new variable is visible)
2. Add a line to your local (uncommitted) copy of `src/Rider/.devcontainer/devcontainer.json`'s
   `mounts` array:
   ```
   "source=${localEnv:REQNROLL_HOST_SOLUTION_DIR},target=/workspaces/host-solution,type=bind"
   ```
3. Rebuild the container. The solution is then reachable at `/workspaces/host-solution`
   in Rider's Open dialog. Also needs a real .NET SDK inside the container to restore —
   see "Bundling the LSP server" above; the devcontainer installs one via
   `dotnet-install.sh` for exactly this reason.

Don't commit the `mounts` line — `${localEnv:...}` resolves to an empty/invalid path (and
fails the container build) for anyone who hasn't set the variable, so this only belongs
in your own working copy.

### What to check

Applies regardless of which track launched the sandbox.

1. Open/create a small scratch project containing a `.feature` file to trigger
   `ReqnrollLspServerSupportProvider.fileOpened`. In the devcontainer, create it under
   `/workspaces/rider-samples` (a dedicated named volume, mounted in
   `devcontainer.json`) rather than anywhere under the repo checkout — it persists
   across container rebuilds the same way the repo does, but stays outside the git
   working tree entirely. Confirm the server actually started:
   - the LSP status widget / "Language Servers" view in Rider should list "Reqnroll";
   - `ps aux | grep Reqnroll.IdeSupport.LSP.Server` (or Task Manager, on the native
     track) should show the process running;
   - the sandbox's `idea.log` (under `build/idea-sandbox/.../log/`) should show the LSP
     initialize handshake, with no errors from `ReqnrollServerPathResolver`.
2. Beyond "does the server start," worth checking the actual feature surface once it's up:
   - `.feature` files: Gherkin keywords/tags/step text are colored (custom `reqnroll.*`
     semantic tokens — Rider's default color scheme doesn't style all of them distinctly
     out of the box; see `ReqnrollSemanticTokensSupport`'s fallback-key choices if a
     specific token type looks unstyled), undefined/ambiguous steps are highlighted, "Define
     missing step(s)" code actions appear on undefined steps, Go to Step Definition (F12)
     jumps into the bound `.cs` method, and the bound method name appears as an inlay hint
     at the end of each step line.
   - `.cs` files: each step-definition method shows a "N step usages" CodeVision lens above
     it; clicking navigates to (or lists) the matching feature-file steps. Right-click → Find
     Step Usages does the same from the caret.
   - Tools menu → Reqnroll → Find Unused Step Definitions scans the whole workspace.
   - None of the above needs an explicit rebuild between edits: `publishServer`'s Gradle
     inputs cover the full `src/LSP`/`src/Core` source trees (content-hashed), so
     `./gradlew runIde` republishes automatically whenever server-side source actually
     changed and skips it (fast) when it didn't — a stale binary shouldn't be the culprit
     if something doesn't reflect a recent server-side change. (Devcontainer track only:
     this doesn't apply to the `-PlspServerBuildDir` external-build-dir flow, which skips
     `publishServer` entirely — republish manually via step 2 above after server changes.)

## Logging

VS and VS Code both tee every LSP JSON-RPC message (both directions) into an
`[LSP - HH:mm:ss] {"isLSPMessage":true,...}` file consumable by
[lsp-viewer](https://lampepfl.github.io/lsp-viewer/) — `LspInspectorLogger` on each
side. **That isn't replicable on Rider**: `com.intellij.platform.lsp.api`'s
`LspServerDescriptor`/`ProjectWideLspServerDescriptor` only exposes `createCommandLine()`
and `startServerProcess()` — the platform spawns the subprocess and owns its stdio pipes
directly, with no interceptor/middleware hook like VS's `IDuplexPipe`-based
`ILspMessageInterceptor` chain (`LspServerConnectionService.cs`) or vscode-languageclient's
`traceOutputChannel`. `LspServerListener` only exposes `serverInitialized`/`serverStopped`
lifecycle callbacks, not raw traffic. For wire-level tracing on Rider, use the platform's
own built-in mechanism instead: `Help → Diagnostic Tools → Debug Log Settings`, add
`#com.intellij.platform.lsp`, then check `idea.log` — it's the platform's own internal
format, not our lsp-viewer JSON, but it's the only supported path.

What *is* replicable: the general client-side glue log (plugin lifecycle/diagnostics —
resolved server path, launch command, exceptions — not wire traffic). `ReqnrollDebugLogger`
(`src/main/kotlin/com/reqnroll/ide/rider/logging`) mirrors the VS extension's
`AsynchronousFileLogger`/`SynchronousFileLogger` convention
(`src/Core/Reqnroll.IdeSupport.Common/Logging`): appends to
`<Reqnroll log dir>/reqnroll-rider-ext-<yyyyMMdd>-<pid>.log`, pruned after 10 days. Log
directory follows the VS Code extension's per-OS convention (`lspInspectorLogger.ts`
`resolveLogDirectory`) rather than the Windows-only VS one, since this plugin runs on the
JVM across the same OSes VS Code does:
- Windows: `%LOCALAPPDATA%\Reqnroll`
- macOS: `~/Library/Logs/Reqnroll`
- Linux: `~/.local/share/Reqnroll`

This is a separate log from the *server's* own `reqnroll-<ide>-*.log` (governed by
`--log-level`, which `runIde` sets to `Verbose` automatically — see "Bundling the LSP
server" above) — `ReqnrollDebugLogger` only covers the plugin's own client-side glue.

## Testing

Pure-logic tests (no IntelliJ Platform fixture needed) are written — `kotlin("test-junit5")`
is wired into `build.gradle.kts`, `./gradlew test` runs them:

- `ReqnrollServerPathResolverTest` — RID/binary-name selection for each `(os.name, os.arch)`
  combination. The resolver's RID logic is `internal` and explicitly parameterized
  (`rid(osName, osArch)`, `isWindows(osName)`, `binaryName(osName)`) specifically so this
  doesn't need to mutate real `System` properties.
- `ProjectFileRoleTest` — `.feature`/`.cs` classification, case-insensitivity, untracked
  extensions falling back to `null`.
- `DocumentActivationStateTest` — every phase transition from the ported
  `DocumentActivationState.cs`, including the issue #85 activation-before-open ordering and
  the close/reopen reset.
- `ReqnrollSemanticTokensSupportTest` — every one of the 11 `reqnroll.*` legend types actually
  has a `TextAttributesKey` mapping (guards against silently losing color for a type if the
  legend grows and the mapping isn't updated to match).
- `ReqnrollLspServerDescriptorTest` — `resolveLogLevel(isDevSandbox)` picks Verbose/Warning
  correctly. Pulled out to `internal` on the companion object for the same reason as
  `ReqnrollServerPathResolver`'s RID logic: parameterized instead of reading the real
  `reqnroll.devSandbox` system property directly.
- `FindUnusedStepDefinitionsActionTest` / `FindStepUsagesRunnerTest` — the popup-row label
  formatting (`renderLabel`) for both custom-command result lists, including the
  null-optional-field omission behavior. Pulled out to `internal` for the same reason.

`intellijPlatform { testFramework(TestFrameworkType.Platform) }` is wired in (issue #566),
unlocking IntelliJ Platform fixture-based tests — but **only application-level ones**:
- `ReqnrollFeatureFileTypeRegistrationTest` — confirms `.feature` resolves to
  `ReqnrollFeatureFileType`/`ReqnrollFeatureLanguage` through plugin.xml's `fileType` extension
  at runtime (catches wiring typos `verifyPlugin`'s bytecode-level checks don't, since they never
  actually load the extension point), via `FileTypeManager.getInstance()` — an *application*-level
  service — under a plain `ApplicationRule` (`org.junit.ClassRule`; JUnit3/4-style tests run via
  the `org.junit.vintage:junit-vintage-engine` also added for this, alongside the pre-existing
  `kotlin("test-junit5")` engine).

**Found empirically, corrects this section's previous assumption:** `BasePlatformTestCase`
(the IntelliJ Platform's standard *project*-level fixture) does not work against this plugin's
`intellijPlatform { rider(...) }` target at all — confirmed live, issue #566: every
`BasePlatformTestCase` test failed identically, regardless of what it exercised, with
`PluginException: solution can't be null` thrown from
`RiderProtocolProjectSessionsManager.registerLocalSession` while the fixture's `Project` is being
initialized. That's Rider's own `ClientProjectSessionsManager` project service (registered
unconditionally for every `Project` under a Rider-type sandbox, nothing to do with this plugin's
code) requiring a real backend `solution`, which a lightweight test-fixture project never has.
This matches JetBrains' own guidance that Rider plugins need their TestNG-based
`com.jetbrains.intellij.resharper:resharper-test-framework` (`BaseTestWithSolution` and similar —
a real backend spun up against real `.sln`-shaped test data) for anything that needs a live
`Project`, not the generic `BasePlatformTestCase`/JUnit setup `testFramework(TestFrameworkType.Platform)`
alone provides. That's a materially bigger, separate lift (different test runner alongside the
JUnit5 one already in use here, real backend startup, test-data solutions) — out of scope for
issue #566; still TODO:
- `ReqnrollLspServerSupportProvider.fileOpened` — needs a real `Project` to construct/pass to a
  fake/spy `LspServerStarter`.
- `ReqnrollLspServerDescriptor.isSupportedFile`/`createCommandLine()` — `isSupportedFile` needs a
  real `Project`-scoped `VirtualFile` (the `--log-level` value itself is covered by
  `ReqnrollLspServerDescriptorTest` above); `createCommandLine()` also needs a real bundled server
  binary on disk.
- `ReqnrollRunnableProjectsListener`/`ReqnrollProjectFilesSync`/`ReqnrollDocumentActivationSync`/
  `ReqnrollProjectBaseline.buildProjectLoadedParams` — each needs a real
  `Project`/`RunnableProjectsModel`/`FileEditorManager` fixture to test the event-wiring itself
  (the pure logic each delegates to — `ProjectFileRole.classify`, `DocumentActivationState` — is
  already covered above).
- `StepUsagesCodeVisionProvider`/`ReqnrollFeatureInlayHintsController` — need a real
  `Editor`/`PsiFile` fixture (itself `Project`-scoped); the request/response plumbing they call
  (`ReqnrollRequestSender`) is thin glue over `LspServer.sendRequestSync` with no independent
  logic to unit-test.
- Deferred: a full end-to-end functional test (real Rider sandbox, open a `.feature`
  file, confirm the LSP connection comes up and `reqnroll/*` notifications actually arrive)
  — expensive; revisit once there's been at least one live `runIde` verification pass to
  know what "working" looks like concretely.

## Known follow-ups

- ~~Debug builds always bundle/launch the Release LSP server at Warning log level~~ **Fixed.**
  `publishServer` now publishes with `--configuration Debug` when invoked via `runIde` (detected
  from `gradle.startParameter.taskNames`) and `Release` otherwise (`buildPlugin`/CI unaffected).
  `runIde`'s JVM also gets a `reqnroll.devSandbox=true` system property, which
  `ReqnrollLspServerDescriptor.createCommandLine()` reads to pick `--log-level Verbose` instead of
  `Warning` in the dev sandbox.
- ~~`GenericOutProcReqnrollConnector`/`OutProcReqnrollConnector.RunDiscovery` logs "Unable to find
  connector: dotnet" on every build~~ **Fixed.** Root cause: on non-Windows
  (`ResolveNonWindowsDotNetCommand`), when `DOTNET_ROOT` is unset, `GetDotNetCommand()` returns the
  literal string `"dotnet"` (relying on `PATH` resolution at process-launch time), but
  `RunDiscovery`'s existence check (`File.Exists(connectorPath)`) could never succeed for a bare
  command name — `File.Exists` doesn't do `PATH` search — so it always short-circuited with this
  misleading error, even when `dotnet` genuinely was resolvable and would launch fine. (Confirmed
  this wasn't just a dev-container environment gap: Rider's own Test Explorer can build and run the
  generated NUnit tests in the same sandbox, so `dotnet` facilities are genuinely available there.)
  The same flawed check was duplicated one layer down in `ProcessHelper.RunProcessInternal`. Both
  now only apply `File.Exists` when the path looks like an actual file path (has a directory
  component); a bare PATH-relative command is trusted to resolve at launch time, and a genuine
  failure to resolve it now surfaces as a real process-launch error instead of a bogus pre-check.
