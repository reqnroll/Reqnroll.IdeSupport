import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from '../lsp/lspMethods';
import {
  ResolveTestTargetsResponse,
  ScenarioTestTargetDto,
} from '../testRunner/scenarioTestTarget';
import { TestResultStore } from '../testRunner/testResultStore';
import { getCodeLensRefreshEvent } from './codeLensRefresh';

/** Arguments passed through the `reqnroll.runTest` command from a lens click — see `runTest.ts`. */
export interface RunTestCommandArgs {
  readonly uri: string;
  readonly range: {
    start: { line: number; character: number };
    end: { line: number; character: number };
  };
  readonly targets: ScenarioTestTargetDto[];
}

/**
 * Recursively collects every `SymbolKind.Method` symbol (Scenario/Scenario Outline both map to
 * `Method` — see `DocumentSymbolHandler.cs`'s `ToSymbolKind`) from a document symbol tree, at any
 * nesting depth. Needed because a scenario nested under a `Rule` (kind `Namespace`) only shows up
 * as a grandchild of the top-level array, not a direct child.
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
 * Builds the "Run" CodeLens for one resolved scenario, or `undefined` when there's nothing to run
 * — an empty `targets` list means the scenario isn't resolvable yet (no generated `.feature.cs`, or
 * a naming-rule mismatch; design doc §3's "not-yet-built" trade-off), so no lens is shown at all
 * rather than one that would just error on click.
 */
export function buildRunLens(
  documentUri: string,
  range: vscode.Range,
  targets: readonly ScenarioTestTargetDto[],
  cachedOutcome: 'passed' | 'failed' | undefined,
): vscode.CodeLens | undefined {
  if (targets.length === 0) return undefined;

  const icon =
    cachedOutcome === undefined ? '$(play)' : cachedOutcome === 'passed' ? '$(check)' : '$(error)';

  const args: RunTestCommandArgs = {
    uri: documentUri,
    range: {
      start: { line: range.start.line, character: range.start.character },
      end: { line: range.end.line, character: range.end.character },
    },
    targets: [...targets],
  };

  const lens = new vscode.CodeLens(range);
  lens.command = {
    title: `${icon} Run`,
    command: 'reqnroll.runTest',
    arguments: [args],
  };
  return lens;
}

/**
 * An unresolved "Run" lens placement (issue #495): carries just the scenario's own range and the
 * owning document's URI, nothing else. `provideCodeLenses` returns one of these per scenario symbol
 * without ever calling `reqnroll/resolveTestTargets` — that call only happens in
 * {@link RunCodeLensProvider.resolveCodeLens}, and only for lenses VS Code has actually scrolled
 * into view. On a large `.feature` file this is what keeps the per-file cost proportional to the
 * number of currently-visible lenses instead of the whole document.
 */
class RunCodeLens extends vscode.CodeLens {
  constructor(
    range: vscode.Range,
    readonly documentUri: string,
  ) {
    super(range);
  }
}

/**
 * Registers the "▶ Run" CodeLens on each Scenario/Scenario Outline line (design doc §5 — VS Code
 * "Option 2: own execution, no `TestController`"; re-scoped by issue #495 to use the standard
 * two-phase `provideCodeLenses`/`resolveCodeLens` contract instead of resolving every scenario
 * eagerly). Scenario ranges come from the standard `vscode.executeDocumentSymbolProvider` built-in
 * command (routed to the LSP server automatically — no new custom request needed for that); each
 * range's actual test target(s) come from the custom `reqnroll/resolveTestTargets` request, sent
 * only once VS Code resolves a lens that's actually visible, mirroring `goToHooks.ts`'s
 * custom-request pattern.
 *
 * 🐛 Debug is deliberately not implemented in this pass — VSTest debug-attach is a separate,
 * unverified mechanism; see docs/Test-Runner-Integration-Design.md §5 and the PR description.
 */
export function registerRunCodeLens(
  client: LanguageClient,
  context: vscode.ExtensionContext,
  resultStore: TestResultStore,
): void {
  const provider: vscode.CodeLensProvider = {
    onDidChangeCodeLenses: getCodeLensRefreshEvent(client, context),

    async provideCodeLenses(document: vscode.TextDocument): Promise<vscode.CodeLens[]> {
      const uri = document.uri.toString();

      let symbols: vscode.DocumentSymbol[] | undefined;
      try {
        symbols = await vscode.commands.executeCommand<vscode.DocumentSymbol[]>(
          'vscode.executeDocumentSymbolProvider',
          document.uri,
        );
      } catch (err) {
        console.warn('runCodeLens: vscode.executeDocumentSymbolProvider failed', err);
        return [];
      }
      if (!symbols || symbols.length === 0) return [];

      // No reqnroll/resolveTestTargets calls here (issue #495) — just the placements. VS Code
      // calls resolveCodeLens below, lazily, only for the lenses that scroll into view.
      return collectMethodSymbols(symbols).map(
        (symbol) => new RunCodeLens(symbol.selectionRange, uri),
      );
    },

    async resolveCodeLens(
      codeLens: vscode.CodeLens,
      token: vscode.CancellationToken,
    ): Promise<vscode.CodeLens | undefined> {
      if (!(codeLens instanceof RunCodeLens)) return undefined;

      let response: ResolveTestTargetsResponse;
      try {
        response = await client.sendRequest<ResolveTestTargetsResponse>(
          ReqnrollMethods.resolveTestTargets,
          {
            textDocument: { uri: codeLens.documentUri },
            range: {
              start: {
                line: codeLens.range.start.line,
                character: codeLens.range.start.character,
              },
              end: {
                line: codeLens.range.end.line,
                character: codeLens.range.end.character,
              },
            },
          },
          token,
        );
      } catch (err) {
        console.warn('runCodeLens: reqnroll/resolveTestTargets request failed', err);
        return undefined;
      }

      const cached = resultStore.get(codeLens.documentUri, codeLens.range.start.line);
      return buildRunLens(
        codeLens.documentUri,
        codeLens.range,
        response?.targets ?? [],
        cached?.outcome,
      );
    },
  };

  context.subscriptions.push(
    vscode.languages.registerCodeLensProvider({ language: 'gherkin' }, provider),
  );
}
