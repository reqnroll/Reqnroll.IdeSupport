# Contributing to the Reqnroll VS Code Extension

## Prerequisites

- [Node.js](https://nodejs.org/) 22 or later
- [.NET SDK](https://dotnet.microsoft.com/download) 10.0 or later (for server publish)
- [VS Code](https://code.visualstudio.com/) 1.96 or later
- Recommended VS Code extensions: ESLint (`dbaeumer.vscode-eslint`), Prettier (`esbenp.prettier-vscode`)

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
the more obvious `onLanguage:Gherkin`. This is intentional, not left over: the step-usage-count
CodeLens (`stepCodeLens.ts`) needs the LSP client and `ProjectManager` running as soon as a
`.cs` file with step-definition CodeLenses is opened — which can happen before the user ever
opens a `.feature` file (`onLanguage:Gherkin` wouldn't have fired yet). `workspaceContains` lets
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

## CI

The GitHub Actions workflow [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) runs its VS Code jobs (`build-vscode-extension`, `tsc-only`) whenever a push or PR touches VS Code, Core, or LSP paths. It:

1. Publishes the server for all four RIDs in parallel
2. Compiles TypeScript, lints, format-checks, and validates semantic token scopes
3. Packages the `.vsix`
