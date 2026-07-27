import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from './lspMethods';
import { openAndReveal } from './navigationUtils';

interface FindUnusedStepDefinitionsResponse {
  items: UnusedStepDefinitionItem[];
}

interface UnusedStepDefinitionItem {
  projectName?: string;
  className?: string;
  methodName?: string;
  bindingExpression?: string;
  sourceFile?: string;
  sourceLine: number;
  sourceChar: number;
}

/**
 * Implements Find Unused Step Definitions: asks the server to scan the workspace (with a
 * progress notification while it runs) and shows a `QuickPick` of step definitions with no
 * usages in any feature file, navigating to the chosen one's source.
 */
export async function doFindUnusedStepDefinitions(client: LanguageClient): Promise<void> {
  let response: FindUnusedStepDefinitionsResponse;
  try {
    response = await vscode.window.withProgress(
      {
        location: vscode.ProgressLocation.Notification,
        title: 'Reqnroll: Scanning for unused step definitions…',
        cancellable: false,
      },
      () =>
        client.sendRequest<FindUnusedStepDefinitionsResponse>(
          ReqnrollMethods.findUnusedStepDefinitions,
          {},
        ),
    );
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    void vscode.window.showErrorMessage(`Reqnroll: Find Unused Step Definitions failed — ${msg}`);
    return;
  }

  if (!response.items || response.items.length === 0) {
    void vscode.window.showInformationMessage('Reqnroll: No unused step definitions found.');
    return;
  }

  const items = response.items.map((item) => {
    const name = [item.className, item.methodName].filter(Boolean).join('.');
    return {
      label: `$(warning) ${name}`,
      description: item.bindingExpression,
      detail: item.projectName,
      item,
    };
  });

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder: `${response.items.length} unused step definition(s) — select to navigate`,
    matchOnDescription: true,
    matchOnDetail: true,
  });
  if (!picked?.item.sourceFile) return;

  await openAndReveal(
    vscode.Uri.file(picked.item.sourceFile),
    picked.item.sourceLine,
    picked.item.sourceChar,
  );
}
