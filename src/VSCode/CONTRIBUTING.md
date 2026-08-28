# Contributing to the Reqnroll VS Code Extension

## Prerequisites

- [Node.js](https://nodejs.org/) 22 or later
- [.NET SDK](https://dotnet.microsoft.com/download) 10.0 or later (for server publish —
  matches the `net10.0` `TargetFramework` used across the solution's `.csproj` files)
- [VS Code](https://code.visualstudio.com/) 1.96 or later — **intentional minimum, not
  just "current version":** the first release with full `vscode-languageclient` v10
  compatibility (also enforced by `engines.vscode` in `package.json`). Don't lower it
  without checking that compatibility still holds.
- Recommended VS Code extensions: ESLint (`dbaeumer.vscode-eslint`), Prettier (`esbenp.prettier-vscode`)

> **Note:** `typescript` is intentionally capped at `^5.9.3` in `package.json` (not just
> "not yet bumped") — `typescript-eslint@8.65.0` declares a peer dependency of
> `typescript <6.1.0`, so installing a newer TypeScript breaks `npm install` with an
> `ERESOLVE` conflict until `typescript-eslint` adds support for it. The `^` range itself
> is what stops Dependabot from proposing a breaking bump; if Dependabot (or a manual
> bump) ever does try to move past `5.x`, check whether `typescript-eslint` supports it
> yet before accepting.

## Repository layout

```
src/VSCode/               ← this directory (TypeScript extension)
  src/
    extension.ts          ← entry point (activate / deactivate)
    projectManager.ts     ← reqnroll/projectLoaded notifications
    msbuildEvaluator.ts   ← dotnet msbuild property evaluation
    lspInspectorLogger.ts ← optional JSON-RPC file logger
    statusBar.ts          ← LSP server status bar item
    test/                 ← Mocha suite (runs in an Extension Development Host)
  syntaxes/               ← TextMate grammar (.tmLanguage.json)
  scripts/
    publish-server.sh     ← publishes the LSP server for all RIDs
    build-vsix.sh         ← packages the .vsix
    validate-semantic-token-scopes.mjs  ← CI validation
src/LSP/                  ← the shared LSP server (C#)
```

## Activation events

`package.json`'s `activationEvents` includes `workspaceContains:**/*.feature` in addition to
the more obvious `onLanguage:gherkin`. This is intentional, not left over: the step-usage-count
CodeLens (`stepCodeLens.ts`) needs the LSP client and `ProjectManager` running as soon as a
`.cs` file with step-definition CodeLenses is opened — which can happen before the user ever
opens a `.feature` file (`onLanguage:gherkin` wouldn't have fired yet). `workspaceContains` lets
the extension activate as soon as the workspace is known to be a Reqnroll project, independent
of which file the user opens first. Don't remove it without checking that CodeLens still shows
up on a `.cs` file opened before any `.feature` file.

## Development workflow

### 1. Build the LSP server

The extension bundles the LSP server binary. For local development, publish it once:

```sh
cd src/VSCode
npm run build:server
```

This runs `scripts/publish-server.sh`, which publishes the server for your host platform into `src/VSCode/server/<rid>/`.

### 2. Install npm dependencies

```sh
cd src/VSCode
npm ci
```

### 3. Open in VS Code

Open the `src/VSCode` folder in VS Code. Press **F5** to launch the Extension Development Host with the extension loaded. A new VS Code window will open with the extension active.

### 4. Live TypeScript compilation

In a terminal:

```sh
cd src/VSCode
npm run watch
```

This keeps `out/` up to date as you edit `.ts` files, so the Extension Development Host picks up changes on reload (`Ctrl+R` in the dev host window).

### 5. Running tests

```sh
cd src/VSCode
npm run compile && npm test
```

`npm test` downloads VS Code (via `@vscode/test-electron`) and runs the full suite —
TextMate grammar, utility functions, and command/LSP integration tests — inside an Extension
Development Host.

### 6. Lint and format

```sh
cd src/VSCode
npm run lint
npm run format          # write (auto-fix)
npm run format:check    # check only (used in CI)
```

`npm ci`/`npm install` also installs a repo-root git `pre-commit` hook (via `husky` +
`lint-staged`, configured under `lint-staged` in `package.json`) that runs `prettier --write` on
staged `.ts` files automatically. This exists because `npm run format:check` is unreliable to
eyeball on a Windows checkout: `.gitattributes`' `text=auto` normalizes `.ts` files to CRLF in
the working copy, so `prettier --check` flags essentially every file (touched or not) with a
false positive from the line-ending conversion alone — real violations get lost in that noise.
The hook sidesteps it entirely: `prettier --write` always normalizes to LF on disk before
staging, regardless of what the working copy had, so a real formatting issue can't slip through
to CI the way it otherwise can on Windows. If you ever need to skip it (e.g. a WIP commit),
`HUSKY=0 git commit ...` disables it for that one command.

### 7. Packaging

```sh
cd src/VSCode
npm run build:vsix
```

This publishes the server for all four RIDs and packages the `.vsix` in one step. Requires Docker or cross-compilation support for non-host RIDs.

## LSP tracing

To see raw JSON-RPC traffic, open VS Code Settings and set:

```
reqnroll.trace.server: verbose
```

Traffic appears in the **Output** panel under **Reqnroll LSP Trace**. When set to `verbose`, a timestamped log file is also written to `%LOCALAPPDATA%\Reqnroll\` (Windows) or `~/.local/share/Reqnroll/` (macOS/Linux).

Unlike the Visual Studio extension, VS Code doesn't spawn the server with `--trace` or
`--protocol-log-level` — `reqnroll.trace.server` is the one setting that drives both sides:

- The wire-level trace (`InitializeParams.Trace` at startup, `$/setTrace` on later changes) is
  handled entirely by `vscode-languageclient` itself, via the `LogOutputChannel` returned from
  `createTraceChannel()` (`src/lspInspectorLogger.ts`) — `off` maps to `vscode.LogLevel.Off`
  (client sends `trace: "off"`), anything else to `vscode.LogLevel.Trace` (client sends
  `trace: "messages"`/`"verbose"` matching the setting). No `--trace` CLI flag is involved.
- `traceServerToLogLevel()` (same file) separately maps the setting onto the server's
  `--log-level` CLI argument passed in `extension.ts` (`off` → `Warning`, `messages` → `Info`,
  `verbose` → `Verbose`), so the same lever also controls the server's own file/protocol log
  verbosity. `--protocol-log-level` is left at the server's own default (`Warning`) — there's no
  VS Code setting for it yet.

Changing `reqnroll.trace.server` requires a window reload to take effect on the already-running
server (the `--log-level` it maps to is fixed at process launch).

**The Output panel can appear empty even with tracing on.** `reqnroll.trace.server: verbose`
correctly drives `vscode-languageclient` to trace (`InitializeParams.Trace`/`$/setTrace` as
above), but the **Reqnroll LSP Trace** channel is a `vscode.LogOutputChannel`, which has its own
independent display-level filter — set only by the user, via that channel's own dropdown in the
Output panel (or Command Palette → "Developer: Set Log Level…" → pick the channel). Nothing in
`reqnroll.trace.server`, or anywhere else in the extension, can raise that filter programmatically,
so a channel left at its default level will silently show nothing even while tracing is fully
active. If the panel looks empty, check the timestamped file log under `%LOCALAPPDATA%\Reqnroll\`
(or the platform equivalent above) instead — it's written directly to disk and isn't subject to
this filter, so it's the more reliable place to look.

## CI

The GitHub Actions workflow [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) runs its VS Code jobs (`build-vscode-extension`, `tsc-only`) whenever a push or PR touches VS Code, Core, or LSP paths. It:

1. Publishes the server for all four RIDs in parallel
2. Compiles TypeScript, lints, format-checks, and validates semantic token scopes
3. Packages the `.vsix`
