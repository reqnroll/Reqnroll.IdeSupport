import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from '../lsp/lspMethods';
import { ResolveTestTargetsResponse, ScenarioTestTargetDto } from '../testRunner/scenarioTestTarget';
import { TestResultStore } from '../testRunner/testResultStore';
import { getCodeLensRefreshEvent } from './codeLensRefresh';

/** Arguments passed through the `reqnroll.runTest` command from a lens click — see `runTest.ts`. */
export interface RunTestCommandArgs {
  readonly uri: string;
  readonly range: { start: { line: number; character: number }; end: { line: number; character: number } };
  readonly targets: ScenarioTestTargetDto[];
}

/**
 * Recursively collects every `SymbolKind.Method` symbol (Scenario/Scenario Outline both map to
 * `Method` — see `DocumentSymbolHandler.cs`'s `ToSymbolKind`) from a document symbol tree, at any
 * nesting depth. Needed because a scenario nested under a `Rule` (kind `Namespace`) only shows up
 * as a grandchild of the top-level array, not a direct child.
 */
export function collectMethodSymbols(symbols: readonly vscode.DocumentSymbol[]): vscode.DocumentSymbol[] {
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

  const icon = cachedOutcome === undefined ? '$(play)' : cachedOutcome === 'passed' ? '$(check)' : '$(error)';

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
 * Registers the "▶ Run" CodeLens on each Scenario/Scenario Outline line (design doc §5 — VS Code
 * "Option 2: own execution, no `TestController`"). Unlike `hookCodeLens.ts`/`stepCodeLens.ts` (which
 * query the standard `textDocument/codeLens`), scenario ranges come from the standard
 * `vscode.executeDocumentSymbolProvider` built-in command (routed to the LSP server automatically —
 * no new custom request needed for that), and each range's actual test target(s) come from the new
 * custom `reqnroll/resolveTestTargets` request, mirroring `goToHooks.ts`'s custom-request pattern.
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

      const lenses: vscode.CodeLens[] = [];

      for (const symbol of collectMethodSymbols(symbols)) {
        let response: ResolveTestTargetsResponse;
        try {
          response = await client.sendRequest<ResolveTestTargetsResponse>(
            ReqnrollMethods.resolveTestTargets,
            {
              textDocument: { uri },
              range: {
                start: {
                  line: symbol.selectionRange.start.line,
                  character: symbol.selectionRange.start.character,
                },
                end: {
                  line: symbol.selectionRange.end.line,
                  character: symbol.selectionRange.end.character,
                },
              },
            },
          );
        } catch (err) {
          console.warn('runCodeLens: reqnroll/resolveTestTargets request failed', err);
          continue;
        }

        const cached = resultStore.get(uri, symbol.selectionRange.start.line);
        const lens = buildRunLens(uri, symbol.selectionRange, response?.targets ?? [], cached?.outcome);
        if (lens) lenses.push(lens);
      }

      return lenses;
    },
  };

  context.subscriptions.push(
    vscode.languages.registerCodeLensProvider({ language: 'gherkin' }, provider),
  );
}
