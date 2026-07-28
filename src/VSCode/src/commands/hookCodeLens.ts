import * as vscode from 'vscode';
import { CodeLensRequest, LanguageClient } from 'vscode-languageclient/node';
import { getCodeLensRefreshEvent } from './codeLensRefresh';

/**
 * Registers the hook-match-count CodeLens provider for `.feature` files (issue #269), querying
 * the server via `textDocument/codeLens` (the same request `registerStepCodeLens` uses for `.cs`
 * files — the server tells the two apart by URI extension and returns lenses for whichever one
 * applies) and refreshing lenses via the shared `workspace/codeLens/refresh` listener (see
 * `getCodeLensRefreshEvent` — only one provider may register the raw `onRequest` handler).
 */
export function registerHookCodeLens(
  client: LanguageClient,
  context: vscode.ExtensionContext,
): void {
  const provider: vscode.CodeLensProvider = {
    onDidChangeCodeLenses: getCodeLensRefreshEvent(client, context),
    async provideCodeLenses(document: vscode.TextDocument): Promise<vscode.CodeLens[]> {
      try {
        const lenses = await client.sendRequest(CodeLensRequest.type, {
          textDocument: { uri: document.uri.toString() },
        });
        if (!lenses || lenses.length === 0) return [];
        return lenses.map((lens) => {
          const range = new vscode.Range(
            lens.range.start.line,
            lens.range.start.character,
            lens.range.end.line,
            lens.range.end.character,
          );
          const codeLens = new vscode.CodeLens(range);
          if (lens.command) {
            codeLens.command = {
              title: lens.command.title,
              command: lens.command.command,
              arguments: lens.command.arguments ?? [],
            };
          }
          return codeLens;
        });
      } catch (err) {
        console.warn('HookCodeLens: textDocument/codeLens request failed', err);
        return [];
      }
    },
  };

  context.subscriptions.push(
    vscode.languages.registerCodeLensProvider({ language: 'gherkin' }, provider),
  );
}
