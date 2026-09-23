import * as vscode from 'vscode';
import {
  DocumentSymbol,
  DocumentSymbolRequest,
  LanguageClient,
  SymbolInformation,
  SymbolKind,
} from 'vscode-languageclient/node';
import { ReqnrollMethods } from '../lsp/lspMethods';
import { GHERKIN_LANGUAGE_ID } from '../languageIds';
import { findOwningProjectFile, ProjectManager } from '../lsp/projectManager';
import { GetTestOutcomeResponse, TestOutcomeRow, getTestOutcome } from './testOutcomesService';

/** Registered once per activation — see the "unresolved CodeLens" note in {@link registerTestOutcomeCodeLens}. */
const NOOP_COMMAND = 'reqnroll.testOutcomes.noop';

/**
 * Read-only "✓/✗" CodeLens on each Scenario/Scenario Outline line in `.feature` files, sourced
 * from the LSP server's `TestOutcomeStore` (LSP-server outcome pipeline, #700/#702) — opt-in via
 * `reqnroll.testOutcomes.enabled`, same as `testOutcomesService.ts`.
 *
 * **Deliberately not a Run/Debug affordance.** VS Code's Reqnroll extension tried and reverted two
 * different `.feature`-file UI surfaces for test running already (a CodeLens, then a
 * `vscode.TestController` — issue #504) precisely because C# Dev Kit's own gutter/Test Explorer
 * integration already covers that adequately, and this extension owning a competing `Run`
 * affordance duplicated it for no real benefit. This lens's command is a genuine no-op
 * ({@link NOOP_COMMAND}) — present only because an unresolved `vscode.CodeLens` (no command at
 * all) may never render at all (see `vscode.CodeLens`'s own doc comment), not to give it a real
 * action. It never triggers, cancels, or otherwise touches a test run, only reports the last one
 * the server heard about. What it adds that C# Dev Kit's own UI structurally cannot: C# Dev Kit
 * annotates the *generated* `.cs` method, never the `.feature` file itself, and has no notion of
 * "one row of a Scenario Outline" at all — this lens sits directly on the `.feature`
 * Scenario/Outline line and names which row failed, on which step, when there's more than one.
 *
 * Refreshed by the server's `reqnroll/testOutcomes/changed` push (fired after every debounced
 * batch of results — see `TestOutcomeTcpListener.cs`), so a C# Dev Kit-triggered run updates this
 * lens without the user needing to touch the `.feature` file to provoke a recompute.
 */
export function registerTestOutcomeCodeLens(
  client: LanguageClient,
  projectManager: ProjectManager,
  context: vscode.ExtensionContext,
): void {
  const enabled = vscode.workspace
    .getConfiguration('reqnroll')
    .get<boolean>('testOutcomes.enabled', false);
  if (!enabled) return;

  const changedEmitter = new vscode.EventEmitter<void>();
  context.subscriptions.push(
    changedEmitter,
    client.onNotification(ReqnrollMethods.testOutcomesChanged, () => changedEmitter.fire()),
    // A vscode.CodeLens with no command is "unresolved" (see its own doc comment) and, absent a
    // resolveCodeLens implementation, may never actually render — confirmed against vscode.d.ts,
    // not assumed. A real do-nothing command keeps every lens resolved (so it always shows) while
    // staying genuinely inert on click, satisfying "read-only" for real rather than by omission.
    vscode.commands.registerCommand(NOOP_COMMAND, () => undefined),
  );

  const provider: vscode.CodeLensProvider = {
    onDidChangeCodeLenses: changedEmitter.event,
    provideCodeLenses: (document) => computeCodeLenses(client, projectManager, document),
  };

  context.subscriptions.push(
    vscode.languages.registerCodeLensProvider({ language: GHERKIN_LANGUAGE_ID }, provider),
  );
}

async function computeCodeLenses(
  client: LanguageClient,
  projectManager: ProjectManager,
  document: vscode.TextDocument,
): Promise<vscode.CodeLens[]> {
  const projectFile = findOwningProjectFile(document.uri.fsPath, projectManager.getKnownProjects());
  const assemblyPath = projectFile ? projectManager.getOutputAssemblyPath(projectFile) : undefined;
  // No known/built project for this file yet — nothing to look up server-side. Not an error: the
  // same "not built yet" reasoning VS/Rider already apply to a scenario with no resolved target.
  if (!assemblyPath) return [];

  let symbols: (DocumentSymbol | SymbolInformation)[] | null;
  try {
    symbols = await client.sendRequest(DocumentSymbolRequest.type, {
      textDocument: { uri: document.uri.toString() },
    });
  } catch (err: unknown) {
    console.warn('testOutcomeCodeLens: textDocument/documentSymbol request failed', err);
    return [];
  }
  if (!symbols) return [];

  const scenarios = collectMethodSymbols(symbols);
  const lenses: vscode.CodeLens[] = [];

  for (const scenario of scenarios) {
    const range = toVscodeRange(scenario.selectionRange);
    if (range.start.line < 0 || range.start.line >= document.lineCount) continue;

    let targets: { declaringTypeFullName: string; methodName: string }[];
    try {
      const response = await client.sendRequest<{
        targets: { declaringTypeFullName: string; methodName: string }[];
      }>(ReqnrollMethods.resolveTestTargets, {
        textDocument: { uri: document.uri.toString() },
        range: scenario.selectionRange,
      });
      targets = response?.targets ?? [];
    } catch {
      continue; // A resolution failure for one scenario shouldn't drop the rest of the document.
    }
    if (targets.length === 0) continue;

    const outcome = await fetchAggregateOutcome(client, assemblyPath, targets);
    if (!outcome) continue;

    // The command is a genuine no-op (see NOOP_COMMAND's registration) — this lens is
    // display-only by design (see the module doc comment); it just needs *a* command to stay
    // "resolved" so VS Code actually renders it. `tooltip` is a real `vscode.Command` field (the
    // lens's own inline hover) — issue #723, the same bug already fixed on the VS side in
    // 4bdefaf5 (`RunTestCodeLensDataPoint.BuildTooltip`/`HookCodeLensDataPoint.BuildTooltip`):
    // this went unset here too, so hovering the lens showed nothing at all instead of failure
    // detail.
    const codeLens = new vscode.CodeLens(new vscode.Range(range.start, range.start), {
      title: renderTitle(outcome),
      tooltip: buildTooltip(outcome),
      command: NOOP_COMMAND,
    });
    lenses.push(codeLens);
  }

  return lenses;
}

/**
 * `SymbolKind.Method` nodes (Scenario/Scenario Outline — see `DocumentSymbolHandler.cs`'s
 * `ToSymbolKind`), at any nesting depth — a scenario nested under a `Rule` (kind `Namespace`) only
 * shows up as a grandchild of the top-level list. `SymbolInformation` entries (the flat, non-
 * hierarchical alternative shape the LSP spec allows) are dropped rather than handled: this
 * server always sends the hierarchical `DocumentSymbol` shape to any client declaring
 * `hierarchicalDocumentSymbolSupport`, which `vscode-languageclient`'s default `ClientCapabilities`
 * does. `internal` (exported) so it's unit-testable without a running Extension Host — mirrors the
 * Rider plugin's `RunLensSupport.collectMethodSymbols`.
 */
export function collectMethodSymbols(
  symbols: readonly (DocumentSymbol | SymbolInformation)[],
): DocumentSymbol[] {
  const result: DocumentSymbol[] = [];
  for (const symbol of symbols) {
    if (!('selectionRange' in symbol)) continue; // SymbolInformation — see doc comment above.
    if (symbol.kind === SymbolKind.Method) result.push(symbol);
    if (symbol.children && symbol.children.length > 0) {
      result.push(...collectMethodSymbols(symbol.children));
    }
  }
  return result;
}

function toVscodeRange(range: {
  start: { line: number; character: number };
  end: { line: number; character: number };
}): vscode.Range {
  return new vscode.Range(
    range.start.line,
    range.start.character,
    range.end.line,
    range.end.character,
  );
}

/**
 * Queries every distinct target method and combines the results with the same `Failed` > `Passed`
 * precedence the server's own `TestOutcomeStore.Aggregate` uses. Returns `undefined` (render
 * nothing) when every target comes back `found: false` — "the server hasn't heard about this
 * scenario this session," same as VS/Rider's "no run yet" state.
 */
async function fetchAggregateOutcome(
  client: LanguageClient,
  assemblyPath: string,
  targets: readonly { declaringTypeFullName: string; methodName: string }[],
): Promise<GetTestOutcomeResponse | undefined> {
  const distinctMethods = [
    ...new Map(targets.map((t) => [`${t.declaringTypeFullName}.${t.methodName}`, t])).values(),
  ];

  const responses = await Promise.all(
    distinctMethods.map((t) =>
      getTestOutcome(client, assemblyPath, t.declaringTypeFullName, t.methodName),
    ),
  );
  const found = responses.filter((r): r is GetTestOutcomeResponse => r?.found ?? false);
  if (found.length === 0) return undefined;

  return combineOutcomes(found);
}

/** `export`ed for unit testing — combines one `GetTestOutcomeResponse` per target method into one aggregate for the scenario. */
export function combineOutcomes(
  responses: readonly GetTestOutcomeResponse[],
): GetTestOutcomeResponse {
  const aggregate = responses.some((r) => r.aggregate === 'Failed') ? 'Failed' : 'Passed';
  return {
    found: true,
    aggregate,
    rows: responses.flatMap((r) => r.rows),
    isRunning: responses.some((r) => r.isRunning),
    isStale: responses.some((r) => r.isStale),
  };
}

/**
 * Builds the lens's always-visible inline title from an aggregate outcome. Names the first
 * failing row's step when there is exactly one failing row (the common case), otherwise a count,
 * so a Scenario Outline failure says *which* example broke without needing to hover.
 * `export`ed for unit testing.
 */
export function renderTitle(outcome: GetTestOutcomeResponse): string {
  const suffix = outcome.isStale ? ' (stale)' : '';
  if (outcome.aggregate !== 'Failed') return `✓ Passed${suffix}`;

  const failedRows = outcome.rows.filter((r) => r.outcome === 'Failed');
  if (failedRows.length === 1) {
    return `✗ Failed${suffix} — ${describeFailedRow(failedRows[0])}`;
  }
  if (failedRows.length > 1) {
    return `✗ Failed${suffix} (${failedRows.length} of ${outcome.rows.length} rows)`;
  }
  return `✗ Failed${suffix}`;
}

function describeFailedRow(row: TestOutcomeRow): string {
  return row.failedStepText ? `${row.displayName}: ${row.failedStepText}` : row.displayName;
}

/**
 * Builds the lens's hover text (`vscode.Command.tooltip`) — the VS Code counterpart of VS's
 * `RunTestCodeLensDataPoint.BuildTooltip`/`HookCodeLensDataPoint.BuildTooltip` (4bdefaf5). Unlike
 * `renderTitle`, which only names the *first* failing row (to keep the always-visible inline text
 * short), this lists every failing row with its failed step and error message — the detail VS
 * puts behind a Details-popup click and Rider puts in its own hover, neither of which this
 * command-less lens has an equivalent surface for, so the hover is the only place VS Code can put
 * it. Returns `undefined` (no hover) only when there is nothing to say — a passing, non-stale
 * outcome. `export`ed for unit testing.
 */
export function buildTooltip(outcome: GetTestOutcomeResponse): string | undefined {
  if (outcome.aggregate !== 'Failed') {
    return outcome.isStale ? '✓ Passed (stale — rerun to confirm)' : undefined;
  }

  const failedRows = outcome.rows.filter((r) => r.outcome === 'Failed');
  if (failedRows.length === 0) return '✗ Failed';

  return failedRows.map(describeFailedRowDetail).join('\n\n');
}

function describeFailedRowDetail(row: TestOutcomeRow): string {
  const stepDetail = row.failedStepText
    ? `${row.displayName}: ${row.failedStepText} (${describeStepOutcome(row.failedStepOutcome)})`
    : row.displayName;
  return row.errorMessage ? `${stepDetail}\n${row.errorMessage}` : stepDetail;
}

/** Mirrors VS's `RunTestCodeLensDataPoint.DescribeStepOutcome` word-for-word for cross-IDE consistency. */
function describeStepOutcome(stepOutcome: string | undefined): string {
  switch (stepOutcome) {
    case 'Error':
      return 'threw';
    case 'BindingError':
      return 'binding error';
    case 'Undefined':
      return 'undefined step';
    default:
      return stepOutcome ?? 'failed';
  }
}
