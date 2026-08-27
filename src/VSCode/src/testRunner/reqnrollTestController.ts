import * as path from 'path';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { findOwningProjectFile, ProjectManager } from '../lsp/projectManager';
import { ReqnrollMethods } from '../lsp/lspMethods';
import { ResolveTestTargetsResponse, ScenarioTestTargetDto } from './scenarioTestTarget';
import { runDotnetTest } from './dotnetTestRunner';
import { buildTestFilter } from './testFilterBuilder';
import { findFirstFailure, parseStepTrace, TrxUnitTestResult } from './trxParser';
import { findStepLine } from './stepLocator';

export const REQNROLL_TEST_CONTROLLER_ID = 'reqnrollScenarios';

/**
 * Recursively collects every `SymbolKind.Method` symbol (Scenario/Scenario Outline both map to
 * `Method` — see `DocumentSymbolHandler.cs`'s `ToSymbolKind`) from a document symbol tree, at any
 * nesting depth. Needed because a scenario nested under a `Rule` (kind `Namespace`) only shows up
 * as a grandchild of the top-level array, not a direct child. Ported from the pre-`TestController`
 * `runCodeLens.ts` (issue #504's migration) — same logic, same reason it's needed.
 */
export function collectMethodSymbols(
  symbols: readonly vscode.DocumentSymbol[],
): vscode.DocumentSymbol[] {
  const result: vscode.DocumentSymbol[] = [];
  for (const symbol of symbols) {
    if (symbol.kind === vscode.SymbolKind.Method) result.push(symbol);
    if (symbol.children.length > 0) result.push(...collectMethodSymbols(symbol.children));
  }
  return result;
}

/**
 * Registers a native `vscode.TestController` for `.feature` scenarios (issue #504's migration off
 * the CodeLens-only "Option 2, own execution" design — see design doc §5/§6's superseding notes).
 * Unlike the CodeLens it replaces, this gives scenarios a real presence in VS Code's Testing
 * sidebar, native pass/fail history, and (via `vscode.TestMessage.location`) a native failed-step
 * marker — all previously hand-rolled via `TestResultStore`/`ResultDecorationService`, now dropped
 * in favor of the Testing API doing this natively for a controller we own.
 *
 * Known, accepted trade-off (unlike the original CodeLens-only design, which avoided this
 * deliberately): C# Dev Kit's own test discovery can *also* place a native decoration on a
 * Reqnroll-generated method's `.feature`-mapped location (confirmed live, issue #504) when its own
 * (separately flaky/async) discovery happens to have run — a user can see two Testing-sidebar
 * entries for the same scenario in that case. Accepted because our controller is the reliable one;
 * C# Dev Kit's is a bonus that doesn't always show up.
 *
 * Debug is deliberately NOT implemented here, matching the CodeLens this replaces — VSTest
 * debug-attach (`VSTEST_HOST_DEBUG` + a debug-adapter launch) is a separate, unverified mechanism
 * that needs its own spike; see docs/Test-Runner-Integration-Design.md.
 */
export function createReqnrollTestController(
  client: LanguageClient,
  context: vscode.ExtensionContext,
  projectManager: Pick<ProjectManager, 'getKnownProjects'>,
): vscode.TestController {
  const controller = vscode.tests.createTestController(
    REQNROLL_TEST_CONTROLLER_ID,
    'Reqnroll Scenarios',
  );
  context.subscriptions.push(controller);

  controller.resolveHandler = async (item) => {
    if (!item) {
      await discoverFeatureFiles(controller);
      return;
    }
    if (item.canResolveChildren) {
      await populateScenarios(controller, item);
    }
  };

  const watcher = vscode.workspace.createFileSystemWatcher('**/*.feature');
  context.subscriptions.push(
    watcher,
    watcher.onDidCreate((uri) => ensureFileItem(controller, uri)),
    watcher.onDidDelete((uri) => controller.items.delete(uri.toString())),
    watcher.onDidChange((uri) => {
      void populateScenarios(controller, ensureFileItem(controller, uri));
    }),
  );

  const runProfile = controller.createRunProfile(
    'Run',
    vscode.TestRunProfileKind.Run,
    (request, token) => runHandler(controller, client, projectManager, request, token),
    true,
  );
  context.subscriptions.push(runProfile);

  return controller;
}

/** Exported for direct testing — mirrors the pre-`TestController` `doRunTest`'s `provideCodeLenses` coverage. */
export async function discoverFeatureFiles(controller: vscode.TestController): Promise<void> {
  const uris = await vscode.workspace.findFiles('**/*.feature', '**/{bin,obj,node_modules}/**');
  for (const uri of uris) ensureFileItem(controller, uri);
}

/** Exported for direct testing. */
export function ensureFileItem(controller: vscode.TestController, uri: vscode.Uri): vscode.TestItem {
  const id = uri.toString();
  let item = controller.items.get(id);
  if (!item) {
    item = controller.createTestItem(id, path.basename(uri.fsPath), uri);
    item.canResolveChildren = true;
    controller.items.add(item);
  }
  return item;
}

/**
 * Populates `fileItem`'s children from the file's Scenario/Scenario Outline symbols. Deliberately
 * does NOT call `reqnroll/resolveTestTargets` here (mirrors the CodeLens this replaces, issue #495's
 * fix) — that per-scenario resolution only happens lazily, inside the run handler, right before a
 * scenario is actually run. Building the item tree only needs cheap symbol positions. Exported for
 * direct testing.
 */
export async function populateScenarios(
  controller: vscode.TestController,
  fileItem: vscode.TestItem,
): Promise<void> {
  let symbols: vscode.DocumentSymbol[] | undefined;
  try {
    symbols = await vscode.commands.executeCommand<vscode.DocumentSymbol[]>(
      'vscode.executeDocumentSymbolProvider',
      fileItem.uri,
    );
  } catch (err) {
    console.warn('reqnrollTestController: vscode.executeDocumentSymbolProvider failed', err);
    fileItem.children.replace([]);
    return;
  }
  if (!symbols || symbols.length === 0) {
    fileItem.children.replace([]);
    return;
  }

  const children = collectMethodSymbols(symbols).map((symbol) => {
    const id = `${fileItem.uri!.toString()}#${symbol.selectionRange.start.line}`;
    const child = controller.createTestItem(id, symbol.name, fileItem.uri);
    child.range = symbol.selectionRange;
    return child;
  });
  fileItem.children.replace(children);
}

/**
 * Exported for direct testing (mirrors the pre-`TestController` `doRunTest`'s injectable
 * `runTestFn`) — bypasses `controller.createRunProfile`'s indirection entirely, so a test can pass
 * a fake `controller`/`client` and a fake `runTestFn` without needing real `dotnet test` or a real
 * profile invocation.
 */
export async function runHandler(
  controller: vscode.TestController,
  client: LanguageClient,
  projectManager: Pick<ProjectManager, 'getKnownProjects'>,
  request: vscode.TestRunRequest,
  token: vscode.CancellationToken,
  runTestFn: typeof runDotnetTest = runDotnetTest,
): Promise<void> {
  const run = controller.createTestRun(request);
  try {
    const roots: vscode.TestItem[] = [];
    if (request.include) {
      roots.push(...request.include);
    } else {
      controller.items.forEach((item) => roots.push(item));
    }

    const queue = await collectLeafItems(controller, roots, request.exclude ?? []);
    for (const item of queue) run.enqueued(item);

    for (const item of queue) {
      if (token.isCancellationRequested) {
        run.skipped(item);
        continue;
      }
      run.started(item);
      await runOneScenario(client, projectManager, run, item, token, runTestFn);
    }
  } finally {
    run.end();
  }
}

/** Expands file-level container items down to leaf scenario items, resolving children on demand for any item the run request named directly without it having been expanded in the tree yet. */
async function collectLeafItems(
  controller: vscode.TestController,
  roots: readonly vscode.TestItem[],
  exclude: readonly vscode.TestItem[],
): Promise<vscode.TestItem[]> {
  const excluded = new Set(exclude.map((item) => item.id));
  const leaves: vscode.TestItem[] = [];

  async function visit(item: vscode.TestItem): Promise<void> {
    if (excluded.has(item.id)) return;
    if (item.canResolveChildren) {
      if (item.children.size === 0) await populateScenarios(controller, item);
      const children: vscode.TestItem[] = [];
      item.children.forEach((child) => children.push(child));
      for (const child of children) await visit(child);
    } else {
      leaves.push(item);
    }
  }

  for (const root of roots) await visit(root);
  return leaves;
}

/**
 * Streams captured stdout to the Test Results "Output" tab (issue #504 follow-up — VS Code doesn't
 * populate that tab on its own; it renders exactly what the extension pushes via `appendOutput`,
 * same as a real terminal, hence the required `\r\n` line endings). Row-tests results (multiple
 * `TrxUnitTestResult` entries under one scenario item) are each labeled by test name so a mixed
 * pass/fail Outline run doesn't read as one undifferentiated blob.
 */
function appendRunOutput(
  run: vscode.TestRun,
  item: vscode.TestItem,
  results: readonly TrxUnitTestResult[],
): void {
  const text = results
    .map((r) => (results.length > 1 ? `${r.testName}\n${r.stdOut}` : r.stdOut))
    .filter((block) => block.length > 0)
    .join('\n\n')
    .replace(/\r?\n/g, '\r\n');
  if (text.length > 0) run.appendOutput(text, undefined, item);
}

function toLspRange(range: vscode.Range) {
  return {
    start: { line: range.start.line, character: range.start.character },
    end: { line: range.end.line, character: range.end.character },
  };
}

async function runOneScenario(
  client: LanguageClient,
  projectManager: Pick<ProjectManager, 'getKnownProjects'>,
  run: vscode.TestRun,
  item: vscode.TestItem,
  token: vscode.CancellationToken,
  runTestFn: typeof runDotnetTest,
): Promise<void> {
  const uri = item.uri;
  const range = item.range;
  if (!uri || !range) {
    run.errored(item, new vscode.TestMessage('Reqnroll: internal error — scenario item has no location.'));
    return;
  }

  let targets: ScenarioTestTargetDto[];
  try {
    const response = await client.sendRequest<ResolveTestTargetsResponse>(
      ReqnrollMethods.resolveTestTargets,
      { textDocument: { uri: uri.toString() }, range: toLspRange(range) },
      token,
    );
    targets = response?.targets ?? [];
  } catch (err) {
    run.errored(item, new vscode.TestMessage(`Reqnroll: failed to resolve the test target: ${String(err)}`));
    return;
  }

  if (targets.length === 0) {
    // Not built yet, or a naming-rule mismatch — matches the "no lens at all" reasoning the
    // CodeLens this replaces used for the same case (buildRunLens's doc comment).
    run.skipped(item);
    return;
  }

  const featureFilePath = uri.fsPath;
  const projectFile = findOwningProjectFile(featureFilePath, projectManager.getKnownProjects());
  if (!projectFile) {
    run.errored(item, new vscode.TestMessage(`Reqnroll: could not find the project that owns ${featureFilePath}.`));
    return;
  }

  const filterExpr = buildTestFilter(targets);
  const startedAt = Date.now();
  const runResult = await runTestFn(projectFile, filterExpr, token);
  const duration = Date.now() - startedAt;

  if (!runResult) {
    run.errored(item, new vscode.TestMessage(`Reqnroll: dotnet test failed to run for ${projectFile}.`));
    return;
  }
  if ('error' in runResult) {
    run.errored(item, new vscode.TestMessage(`Reqnroll: ${runResult.error}`));
    return;
  }
  if (runResult.results.length === 0) {
    run.errored(item, new vscode.TestMessage('Reqnroll: dotnet test ran but produced no results for this filter.'));
    return;
  }

  // VS Code's Test Results "Output" tab shows nothing unless the extension explicitly streams it
  // here — unlike C# Dev Kit's own runner, which does this for its own test items. Reqnroll's step
  // trace (§6) is exactly what a user expects to see in that pane.
  appendRunOutput(run, item, runResult.results);

  const failingResult = runResult.results.find((r) => r.outcome === 'Failed');
  if (!failingResult) {
    run.passed(item, duration);
    return;
  }

  const message = new vscode.TestMessage(failingResult.errorMessage ?? 'Reqnroll: scenario failed.');
  const failure = findFirstFailure(parseStepTrace(failingResult.stdOut));
  if (failure) {
    message.message = failure.detail ?? failure.stepText;
    try {
      const document = await vscode.workspace.openTextDocument(uri);
      const stepLine = findStepLine(document, range.start.line, failure.stepText);
      if (stepLine !== undefined) {
        message.location = new vscode.Location(uri, new vscode.Position(stepLine, 0));
      }
    } catch (err) {
      console.warn('reqnrollTestController: failed to open document for failed-step location', err);
    }
  }
  run.failed(item, message, duration);
}
