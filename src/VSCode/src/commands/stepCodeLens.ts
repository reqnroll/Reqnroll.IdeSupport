import * as vscode from 'vscode';
import { CodeLensRequest, LanguageClient } from 'vscode-languageclient/node';
import { getCodeLensRefreshEvent } from './codeLensRefresh';

/**
 * Registers the step-usage-count CodeLens provider for C# files, querying the server via
 * `textDocument/codeLens` and refreshing lenses when the server pushes
 * `workspace/codeLens/refresh` after a binding registry change.
 */
export function registerStepCodeLens(
  client: LanguageClient,
  context: vscode.ExtensionContext,
): void {
  // This provider is registered directly via vscode.languages.registerCodeLensProvider rather
  // than through vscode-languageclient's own CodeLens feature (to avoid clashing with the C#
  // extension's codeLens on .cs files), so without the shared refresh event below, VS Code would
  // only re-query provideCodeLenses on incidental events (e.g. editor focus change) rather than
  // promptly after a binding registry change (e.g. a rebuild or a Roslyn re-parse).
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
        console.warn('StepCodeLens: textDocument/codeLens request failed', err);
        return [];
      }
    },
  };

  context.subscriptions.push(
    vscode.languages.registerCodeLensProvider({ language: 'csharp' }, provider),
  );
}
