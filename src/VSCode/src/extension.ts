import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';
import {
  createTraceChannel,
  protocolLogLevelToArg,
  traceServerToLogLevel,
} from './lsp/lspInspectorLogger';
import { createGeneralLogChannel } from './logging/generalFileLog';
import { setAppLogChannel, showError, showInfo } from './logging/appNotify';
import { ProjectManager } from './lsp/projectManager';
import { StatusBarManager } from './statusBar';
import { doToggleComment } from './commands/commentToggle';
import { doFindStepUsages } from './commands/stepUsages';
import { doFindUnusedStepDefinitions } from './commands/findUnusedStepDefinitions';
import { doGoToHooks, sourceForArgs } from './commands/goToHooks';
import {
  OPEN_TAG_LINK_COMMAND,
  TagLinkDecorations,
  createTagLinkMiddleware,
  openTagLink,
  registerTagLinkRefresh,
} from './lsp/tagLinks';
import { getCodeLensRefreshEvent } from './commands/codeLensRefresh';
import { doGoToMatchingScenarios } from './commands/goToMatchingScenarios';
import { doGoToStepDefinition } from './commands/stepNavigation';
import { registerStepCodeLens } from './commands/stepCodeLens';
import { registerHookCodeLens } from './commands/hookCodeLens';
import { CSHARP_LANGUAGE_ID, GHERKIN_LANGUAGE_ID } from './languageIds';
import {
  ManualDocumentSync,
  createManualSyncMiddleware,
  isCSharpDocument,
} from './lsp/manualDocumentSync';
import {
  collapseActiveSelectionForFeatureStepRename,
  createRenameMiddleware,
  renameStepFromCSharp,
} from './commands/renameStep';
import { createExecuteCommandDedupeMiddleware } from './lsp/executeCommandDedupe';
import { createCodeLensSuppressionMiddleware } from './lsp/codeLensSuppression';
import {
  deferTelemetryTeardownUntil,
  drainTelemetryTeardown,
  ensureTelemetryReporter,
  registerTelemetry,
  sendTelemetryEvent,
} from './telemetry';
import { ServerLifecycleTelemetry } from './lsp/serverLifecycleTelemetry';
import { reportExtensionLifecycle } from './extensionLifecycleTelemetry';
import {
  SOURCE_ACTIVATION,
  createReportingErrorHandler,
  executeForeignCommand,
  registerGuardedCommand,
  reportClientException,
} from './clientExceptionTelemetry';
import { TableHighlightService } from './tableHighlightService';
import { activateTestOutcomes } from './testOutcomes/testOutcomesService';
import { activateMtpProjectStubs } from './testOutcomes/mtpProjectStubs';
import { registerTestOutcomeCodeLens } from './testOutcomes/testOutcomeCodeLens';
import { showWalkthroughOnFirstActivation } from './walkthrough';

let client: LanguageClient | undefined;
let projectManager: ProjectManager | undefined;
let statusBar: StatusBarManager | undefined;
let serverLifecycle: ServerLifecycleTelemetry | undefined;

/** The .NET RID for the current platform/arch combination the server is published for. */
export function ridFor(platform: NodeJS.Platform, arch: string): string {
  if (platform === 'win32') return arch === 'arm64' ? 'win-arm64' : 'win-x64';
  if (platform === 'darwin') return arch === 'arm64' ? 'osx-arm64' : 'osx-x64';
  if (platform === 'linux') return arch === 'arm64' ? 'linux-arm64' : 'linux-x64';
  return 'linux-x64';
}

/** The server executable's file name for the current platform (Windows needs the `.exe` suffix). */
export function serverBinaryName(platform: NodeJS.Platform): string {
  return platform === 'win32'
    ? 'Reqnroll.IdeSupport.LSP.Server.exe'
    : 'Reqnroll.IdeSupport.LSP.Server';
}

/**
 * Resolves the path to the Reqnroll LSP server binary.
 *
 * In development (VSIX not yet built), the server is located relative to
 * this source directory's build output. In production (packaged .vsix),
 * the server is bundled inside the extension under `server/<rid>/`.
 *
 * `existsSync` is injectable (defaulting to the real `fs.existsSync`) so the path-selection
 * logic is testable against a fake filesystem without touching disk.
 */
export function resolveServerPath(
  context: Pick<vscode.ExtensionContext, 'extensionMode' | 'extensionPath'>,
  existsSync: (path: string) => boolean = fs.existsSync,
): string {
  const isProduction = context.extensionMode === vscode.ExtensionMode.Production;
  const rid = ridFor(process.platform, process.arch);
  const binaryName = serverBinaryName(process.platform);

  if (isProduction) {
    const serverDir = path.join(context.extensionPath, 'server', rid);
    const candidate = path.join(serverDir, binaryName);
    if (existsSync(candidate)) {
      return candidate;
    }
    const legacy = path.join(context.extensionPath, 'server', binaryName);
    if (existsSync(legacy)) {
      return legacy;
    }
    throw new Error(
      `Reqnroll LSP server not found at ${candidate} or ${legacy}. ` +
        'Ensure the server is published (see scripts/publish-server.sh).',
    );
  }

  const localBuildOutput = path.join(
    context.extensionPath,
    '..',
    '..',
    'src',
    'LSP',
    'Reqnroll.IdeSupport.LSP.Server',
    'bin',
    'Release',
    'net10.0',
    rid,
    'publish',
    binaryName,
  );
  if (existsSync(localBuildOutput)) {
    return localBuildOutput;
  }

  // Falls back to the same server/<rid>/ layout the isProduction branch above checks — CI's
  // build-vscode-extension job downloads the published server there (see ci.yml) but never runs
  // a local `dotnet publish` of LSP.Server, so the Extension Development Host used by `npm test`
  // (extensionMode is never Production there) would otherwise never find a server binary at all.
  return path.join(context.extensionPath, 'server', rid, binaryName);
}

/** Public surface exposed via the extension's `exports` (see `vscode.Extension.exports`), for tests. */
export interface ReqnrollExtensionApi {
  getClient(): LanguageClient | undefined;
}

/**
 * Extension entry point: resolves and launches the Reqnroll LSP server, wires up the
 * language client (middleware, status bar, telemetry, manual `.cs` document sync), and
 * registers all Reqnroll commands.
 */
export async function activate(context: vscode.ExtensionContext): Promise<ReqnrollExtensionApi> {
  // Client-side exception telemetry (issue #621): the reporter must exist before anything below can
  // throw, and an exception escaping activation is reported (then rethrown unchanged).
  try {
    ensureTelemetryReporter(context);
  } catch {
    // Telemetry setup must never block activation.
  }
  try {
    return await activateCore(context);
  } catch (err: unknown) {
    reportClientException(SOURCE_ACTIVATION, err);
    throw err;
  }
}

/** Every command is registered through this so a throwing handler is reported (issue #621) and rethrown. */
const registerCommand = <A extends unknown[], R>(
  commandId: string,
  handler: (...args: A) => R,
): vscode.Disposable => registerGuardedCommand(vscode.commands.registerCommand, commandId, handler);

async function activateCore(context: vscode.ExtensionContext): Promise<ReqnrollExtensionApi> {
  const api: ReqnrollExtensionApi = { getClient: () => client };

  const notReady = (label: string) => () => {
    void showInfo(`Reqnroll: ${label} will be available once the LSP server is ready.`);
  };

  // "Reqnroll LSP" carries vscode-languageclient's own general client diagnostics (and, until
  // #660 is fixed, OmniSharp's internal framework noise via window/logMessage). "Reqnroll" is the
  // curated app-status channel (issue #661, mirroring the VS extension's #651/#656 pane): only
  // extension/LSP-client lifecycle lines (below and in statusBar.ts) and one-line command outcomes
  // (mirrored via logging/appNotify.ts) land here, so it's the one users should be pointed to for
  // "is the extension doing something" — hence `reqnroll.showOutputChannel` now reveals this one.
  const outputChannel = createGeneralLogChannel('Reqnroll LSP');
  const appLogChannel = createGeneralLogChannel('Reqnroll', {
    filePrefix: 'app',
    autoShowOnWarnOrError: true,
  });
  setAppLogChannel(appLogChannel);
  appLogChannel.info('Reqnroll extension activated.');

  // First-run Get Started walkthrough; fire-and-forget so a failure can't block activation.
  void showWalkthroughOnFirstActivation(context).catch((err) =>
    appLogChannel.warn(`Could not open the Get Started walkthrough: ${String(err)}`),
  );

  // Project-local MTP reporter stubs (issue #741): obj/<Project>.csproj.reqnroll-ide.targets for each
  // MTP-capable Reqnroll project, so any later build of it — including a `dotnet test` C# Dev Kit spawns —
  // compiles the reporter in. Runs early, before any test run could plausibly start, and independent
  // of the LSP client, unlike `activateTestOutcomes` below. Awaited because its MTP-capability scan
  // can shell out to `dotnet msbuild` (issue #722).
  await activateMtpProjectStubs(context);

  const traceChannel = createTraceChannel();

  context.subscriptions.push(
    outputChannel,
    appLogChannel,
    traceChannel,

    registerCommand('reqnroll.showOutputChannel', () => appLogChannel.show()),

    // Comment/Uncomment toggle (Ctrl+/ for gherkin files)
    registerCommand('reqnroll.toggleComment', async () => {
      if (!client) {
        notReady('Comment/Uncomment')();
        return;
      }
      await doToggleComment(client);
    }),

    // Find Step Definition Usages (invoked from command palette, context menu, or CodeLens click)
    // When invoked from a CodeLens the server passes [uri, line, char] as arguments.
    registerCommand('reqnroll.findStepUsages', async (...args: unknown[]) => {
      if (!client) {
        notReady('Find Step Usages')();
        return;
      }
      let uriStr: string;
      let line: number;
      let char: number;
      if (args.length >= 2 && typeof args[0] === 'string' && typeof args[1] === 'number') {
        // Called from CodeLens with server-supplied arguments
        uriStr = args[0];
        line = args[1];
        char = typeof args[2] === 'number' ? args[2] : 0;
      } else {
        // Called from command palette or editor context menu
        const editor = vscode.window.activeTextEditor;
        if (!editor) return;
        uriStr = editor.document.uri.toString();
        line = editor.selection.active.line;
        char = editor.selection.active.character;
      }
      await doFindStepUsages(client, uriStr, line, char);
    }),

    // No-op command for CodeLens items that report 0 usages. Deliberately absent from
    // package.json's contributes.commands: it's only ever invoked as a CodeLens click target
    // (see StepCodeLensHandler.cs), never from the command palette, so it doesn't need a
    // manifest entry — VS Code only requires one for palette/keybinding/menu visibility.
    registerCommand('reqnroll.noStepUsages', () => {
      void showInfo('Reqnroll: This step definition has no usages in any feature file.');
    }),

    // Find Unused Step Definitions
    registerCommand('reqnroll.findUnusedStepDefinitions', async () => {
      if (!client) {
        notReady('Find Unused Step Definitions')();
        return;
      }
      await doFindUnusedStepDefinitions(client);
    }),

    // Hook Navigation ("Go to Hooks"; invoked from the command palette, editor context menu,
    // or the hook-count CodeLens — issue #269). When invoked from a CodeLens the server passes
    // [uri, line, char, ownLevelOnly] as arguments, same convention as reqnroll.findStepUsages
    // plus the extra ownLevelOnly flag so the picker matches exactly what the lens counted.
    // alwaysShowPicker is set for every CodeLens-sourced call (issue #372 follow-up) so clicking
    // a lens always shows the picker, even for a single match, rather than jumping straight
    // there — the command-palette/keybinding path (no args) keeps the direct-navigate shortcut.
    // Follows a clickable tag's link (issue #755); only reached through the links the middleware above
    // re-targets, so it is hidden from the command palette.
    registerCommand(OPEN_TAG_LINK_COMMAND, (url: unknown) => openTagLink(url)),

    registerCommand('reqnroll.goToHooks', async (...args: unknown[]) => {
      if (!client) {
        notReady('Go to Hooks')();
        return;
      }
      if (args.length >= 2 && typeof args[0] === 'string' && typeof args[1] === 'number') {
        await doGoToHooks(
          client,
          {
            uri: args[0],
            line: args[1],
            character: typeof args[2] === 'number' ? args[2] : 0,
            ownLevelOnly: typeof args[3] === 'boolean' ? args[3] : false,
            alwaysShowPicker: true,
          },
          sourceForArgs(args),
        );
      } else {
        // The editor/context menu passes the document Uri as the first argument; the command
        // palette and keybinding pass none.
        await doGoToHooks(client, undefined, sourceForArgs(args));
      }
    }),

    // Hook-match-count CodeLens click action (issue #373) -- only ever invoked from a CodeLens
    // click, with the lens's own attribute location as [uri, line, char] arguments. Deliberately
    // absent from package.json's contributes.commands, matching reqnroll.noStepUsages: it has no
    // command-palette/keybinding entry point.
    registerCommand(
      'reqnroll.goToMatchingScenarios',
      async (uri: string, line: number, character: number) => {
        if (!client) {
          notReady('Go to Matching Scenarios')();
          return;
        }
        await doGoToMatchingScenarios(client, uri, line, character);
      },
    ),

    // Go to Step Definition (rich picker with method name + step type)
    registerCommand('reqnroll.goToStepDefinition', async () => {
      if (!client) {
        notReady('Go to Step Definition')();
        return;
      }
      await doGoToStepDefinition(client);
    }),

    // Define Steps (code action / quick-fix that generates step stubs; delegates to VS Code's native code-action picker)
    registerCommand('reqnroll.defineSteps', async () => {
      await vscode.commands.executeCommand('editor.action.quickFix');
    }),

    // Step Rename refactoring. For .cs files there is no rename provider registered —
    // documentSelector deliberately excludes csharp (see manualDocumentSync.ts) — so
    // renameStepFromCSharp drives the reqnroll/renameTargets + textDocument/rename flow directly
    // (issue #457), mirroring the Visual Studio extension's RenameStepCommand. For .feature files
    // this delegates to VS Code's native rename; collapseActiveSelectionForFeatureStepRename()
    // must run BEFORE editor.action.rename starts — see its doc comment (issue #456): mutating
    // the selection while that command already has an in-flight prepareRename request cancels
    // the rename outright for parameterized steps.
    registerCommand('reqnroll.renameStep', async () => {
      const editor = vscode.window.activeTextEditor;
      if (editor?.document.languageId === CSHARP_LANGUAGE_ID) {
        if (!client) {
          notReady('Rename Step')();
          return;
        }
        await renameStepFromCSharp(client, editor);
        return;
      }

      collapseActiveSelectionForFeatureStepRename();
      await executeForeignCommand('editor.action.rename');
    }),

    // F2 keybinding target (issue #506). F2 is the default C# rename-symbol shortcut, so unlike
    // the explicit "Reqnroll: Rename Step" command above, a miss at the cursor (not on a binding
    // expression, or a .cs file the Reqnroll server doesn't own at all) must fall through to VS
    // Code's own editor.action.rename rather than showing a Reqnroll-specific message — otherwise
    // F2 would stop renaming ordinary C# symbols everywhere in every .cs file.
    registerCommand('reqnroll.renameStepOrSymbol', async () => {
      const editor = vscode.window.activeTextEditor;
      if (editor?.document.languageId === CSHARP_LANGUAGE_ID) {
        if (!client) {
          await executeForeignCommand('editor.action.rename');
          return;
        }
        await renameStepFromCSharp(client, editor, { fallbackToNativeRename: true });
        return;
      }

      collapseActiveSelectionForFeatureStepRename();
      await executeForeignCommand('editor.action.rename');
    }),
  );

  // ── Server path resolution ──────────────────────────────────────────────────
  let serverPath: string;
  try {
    serverPath = resolveServerPath(context);
  } catch (err: unknown) {
    const message = err instanceof Error ? err.message : String(err);
    void showError(`Reqnroll: ${message}`, 'Open Documentation').then((choice) => {
      if (choice === 'Open Documentation') {
        void vscode.env.openExternal(
          vscode.Uri.parse('https://github.com/clrudolphi/Reqnroll.Plugin.VisualStudio_Prototypes'),
        );
      }
    });
    return api;
  }

  // ── LSP client ─────────────────────────────────────────────────────────────
  const serverOptions: ServerOptions = {
    command: serverPath,
    args: [
      '--ide',
      'vscode',
      '--log-level',
      traceServerToLogLevel(),
      '--protocol-log-level',
      protocolLogLevelToArg(),
    ],
    options: {
      env: { ...process.env },
    },
  };

  // Permanent link styling for clickable tags (issue #921); fed by the tag-link middleware below.
  const tagLinkDecorations = new TagLinkDecorations().register();
  context.subscriptions.push(tagLinkDecorations);

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ language: GHERKIN_LANGUAGE_ID, pattern: '**/*.feature' }],
    synchronize: {
      fileEvents: vscode.workspace.createFileSystemWatcher('**/*.{feature,cs}'),
    },
    outputChannel,
    traceOutputChannel: traceChannel,
    // Transport errors are reported as client exceptions (issue #621); restart/shutdown decisions stay
    // with the client's default handler.
    errorHandler: createReportingErrorHandler(() => client!.createDefaultErrorHandler()),
    // .cs sync is driven manually (see manualDocumentSync.ts) because
    // vscode-languageclient's built-in sync has proven unreliable for it; this middleware
    // stops the built-in path from also emitting sync notifications for .cs documents.
    // Step Rename refactoring — prepareRename is intercepted to surface multi-attribute rename ambiguity via a
    // QuickPick before delegating to the standard rename flow (see commands/renameStep.ts).
    // `() => client` is passed rather than `client` directly because `client` isn't assigned
    // until after this object is constructed.
    middleware: {
      ...createManualSyncMiddleware(isCSharpDocument),
      ...createRenameMiddleware(() => client),
      // 'reqnroll.toggleComment' is registered as a VS Code command above (Ctrl+/ handler);
      // drop the server's redundant dynamic workspace/executeCommand registration for it so
      // vscode-languageclient never attempts a duplicate registerCommand call — see
      // executeCommandDedupe.ts for why that collision matters beyond just this command.
      ...createExecuteCommandDedupeMiddleware(['reqnroll.toggleComment']),
      // The server declares codeLensProvider statically (issue #471) so a future capable client
      // can use the deferred-resolve path, but that wakes up vscode-languageclient's own built-in
      // CodeLens feature alongside the hand-rolled providers below (registerStepCodeLens,
      // registerHookCodeLens), doubling every lens — see codeLensSuppression.ts.
      ...createCodeLensSuppressionMiddleware(),
      // Clickable tags (issue #755): the built-in documentLink feature renders the server's links;
      // this re-targets each at 'reqnroll.openTagLink' so a click is observable (telemetry), and
      // keeps the linked tags permanently underlined (issue #921).
      ...createTagLinkMiddleware(tagLinkDecorations),
    },
  };

  // The fileEvents watcher above is passed to vscode-languageclient for sync purposes, but
  // vscode-languageclient does not take ownership of (or .dispose()) the watcher object. Push
  // it to context.subscriptions so its OS-level file-watch handle is released on deactivation.
  const watchers = clientOptions.synchronize!.fileEvents;
  if (watchers) {
    if (Array.isArray(watchers)) {
      for (const w of watchers) context.subscriptions.push(w);
    } else {
      context.subscriptions.push(watchers);
    }
  }

  client = new LanguageClient('reqnroll', 'Reqnroll Language Server', serverOptions, clientOptions);

  statusBar = new StatusBarManager(client, appLogChannel);
  context.subscriptions.push(statusBar);

  // Issue #845: server start-failure / unexpected-exit / restart telemetry. The reporter must exist
  // before `client.start()` so a server that never comes up can still report; `registerTelemetry`
  // below reuses it. Attached before start so the first `Starting` transition is seen.
  ensureTelemetryReporter(context);
  // Issue #875: install / upgrade / daily-usage lifecycle events, parity with Visual Studio.
  void reportExtensionLifecycle(context, sendTelemetryEvent);
  serverLifecycle = new ServerLifecycleTelemetry(client, sendTelemetryEvent);
  context.subscriptions.push(serverLifecycle);

  // Issue #8 — per-pipe-character / per-cell decorations for Gherkin data tables. Doesn't
  // depend on the LSP client, so it starts decorating already-open editors immediately.
  context.subscriptions.push(new TableHighlightService());

  client
    .start()
    .then(
      () => {
        projectManager = new ProjectManager(client!);
        // Step usage count CodeLens for C# files (registered after client is running)
        registerStepCodeLens(client!, context);
        // Hook-match count CodeLens for .feature files (issue #269)
        registerHookCodeLens(client!, context);
        // Clickable tags (issue #755): re-request the links once discovery has made the project's tag
        // patterns available - see registerTagLinkRefresh.
        context.subscriptions.push(
          registerTagLinkRefresh(getCodeLensRefreshEvent(client!, context)),
        );
        // No run/test mechanism of our own (issue #504, reconsidered): C# Dev Kit already provides
        // gutter run/debug and Test Explorer integration for Reqnroll-generated methods, mapped back
        // to the .feature file via Reqnroll's own #line pragmas. A prior CodeLens-based "▶ Run"
        // action and a later vscode.TestController migration were both tried and reverted here.
        // Manually sync .cs documents (see manualDocumentSync.ts / createManualSyncMiddleware
        // above) instead of relying on vscode-languageclient's built-in sync feature.
        context.subscriptions.push(new ManualDocumentSync(client!, isCSharpDocument));
        // Forward server-emitted telemetry/event notifications to Application Insights.
        registerTelemetry(client!, context);
        // LSP-server outcome pipeline (#700/#702), opt-in via reqnroll.testOutcomes.enabled —
        // registers this session with the server and merges the bundled VSTest logger into
        // whatever dotnet.unitTests.runSettingsPath already resolves to, so C# Dev Kit's own test
        // runs report per-row/Scenario-Outline outcomes to the server. Fire-and-forget: never
        // blocks activation, and every failure degrades silently (see the module's own doc comment).
        void activateTestOutcomes(context, client!);
        // Read-only outcome CodeLens on .feature Scenario/Outline lines — see that module's own
        // doc comment for why it carries no real Run/Debug action (issue #504).
        registerTestOutcomeCodeLens(client!, projectManager, context);
      },
      // Second argument of then(), not a trailing catch(): only a rejected start() is a server start
      // failure. A throw in the success handler above (CodeLens registration etc.) happens after a
      // successful start and must not be reported as ServerStartFailed (issue #845).
      (err: unknown) => {
        serverLifecycle?.reportStartRejected();
        throw err;
      },
    )
    .catch((err: unknown) => {
      const msg = err instanceof Error ? err.message : String(err);
      void showError(`Reqnroll LSP server failed to start: ${msg}`);
    });

  return api;
}

/** Extension teardown: disposes the project manager and stops the language client. */
export function deactivate(): Thenable<void> | undefined {
  projectManager?.dispose();
  setAppLogChannel(undefined);
  // Deliberate stop: the resulting `Stopped` is not an unexpected server exit (issue #845).
  serverLifecycle?.markIntentionalStop();
  const stopping = client?.stop();
  if (!stopping) return undefined;
  // The server's shutdown-time telemetry (final FeatureUsageSummary, ServerSessionEnded) arrives while
  // stop() is in flight, but VS Code disposes the subscriptions holding the telemetry forwarder and
  // reporter as soon as this function returns; hold their disposal until stop() has settled (#845).
  deferTelemetryTeardownUntil(stopping);
  return stopping.finally(() => drainTelemetryTeardown());
}
