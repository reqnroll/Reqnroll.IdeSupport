import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from '../lsp/lspMethods';
import { showError, showInfo } from '../logging/appNotify';
import { openAndReveal } from '../util/navigationUtils';
import {
  StepDefinitionItem,
  formatBindingAttribute,
  formatMethodName,
  warnSourceNotOnThisMachine,
} from '../util/stepDefinitionItems';

interface FindUnusedStepDefinitionsResponse {
  items: StepDefinitionItem[];
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
    void showError(`Reqnroll: Find Unused Step Definitions failed — ${msg}`);
    return;
  }

  if (!response.items || response.items.length === 0) {
    void showInfo('Reqnroll: No unused step definitions found.');
    return;
  }

  const items = response.items.map((item) => {
    const name = formatMethodName(item);
    // An entry whose source isn't on this machine can't be navigated to, so it gets a different
    // icon and says so in the row rather than looking identical and then doing nothing on click.
    const resolved = item.isResolved ?? true;
    return {
      label: resolved ? `$(warning) ${name}` : `$(error) ${name}`,
      description: formatBindingAttribute(item),
      detail: resolved
        ? item.projectName
        : [item.projectName, 'source not on this machine'].filter(Boolean).join(' — '),
      item,
    };
  });

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder: `${response.items.length} unused step definition(s) — select to navigate`,
    matchOnDescription: true,
    matchOnDetail: true,
  });
  if (!picked) return;

  // Explain rather than no-op. The server nulls sourceFile precisely so this branch is reachable
  // instead of us handing vscode.Uri.file a path that cannot open.
  if (!picked.item.sourceFile) {
    warnSourceNotOnThisMachine(picked.item);
    return;
  }

  await openAndReveal(
    vscode.Uri.file(picked.item.sourceFile),
    picked.item.sourceLine,
    picked.item.sourceChar,
  );
}
