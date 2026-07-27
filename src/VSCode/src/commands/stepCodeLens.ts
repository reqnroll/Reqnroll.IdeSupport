import * as vscode from 'vscode';
import {
  CodeLensRefreshRequest,
  CodeLensRequest,
  LanguageClient,
} from 'vscode-languageclient/node';

/**
 * Registers the step-usage-count CodeLens provider for C# files, querying the server via
 * `textDocument/codeLens` and refreshing lenses when the server pushes
 * `workspace/codeLens/refresh` after a binding registry change.
 */
export function registerStepCodeLens(
  client: LanguageClient,
  context: vscode.ExtensionContext,
): void {
  // The server pushes workspace/codeLens/refresh after a binding registry change (e.g. a
  // rebuild or a Roslyn re-parse), but this provider is registered directly via
  // vscode.languages.registerCodeLensProvider rather than through vscode-languageclient's own
  // CodeLens feature (to avoid clashing with the C# extension's codeLens on .cs files). That
  // means the library has no built-in listener for the refresh push, so without this handler
  // VS Code only re-queries provideCodeLenses on incidental events (e.g. editor focus change).
  const onDidChangeCodeLensesEmitter = new vscode.EventEmitter<void>();

  const provider: vscode.CodeLensProvider = {
    onDidChangeCodeLenses: onDidChangeCodeLensesEmitter.event,
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
    onDidChangeCodeLensesEmitter,
    client.onRequest(CodeLensRefreshRequest.type, () => {
      onDidChangeCodeLensesEmitter.fire();
    }),
    vscode.languages.registerCodeLensProvider({ language: 'csharp' }, provider),
  );
}
