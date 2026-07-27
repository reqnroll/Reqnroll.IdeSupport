import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from './lspMethods';
import { openAndReveal } from './navigationUtils';

interface GoToHooksResponse {
  hooks: GoToHookLocation[];
}

interface GoToHookLocation {
  uri: string;
  startLine: number;
  startChar: number;
  hookType: string;
  hookOrder: number;
  methodName: string;
}

/**
 * Implements Hook Navigation ("Go to Hooks"): queries the server for hooks applicable at the
 * cursor position and navigates directly if there's exactly one, or shows a `QuickPick` to
 * choose among several.
 */
export async function doGoToHooks(client: LanguageClient): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) return;

  const pos = editor.selection.active;
  let response: GoToHooksResponse;
  try {
    response = await client.sendRequest<GoToHooksResponse>(ReqnrollMethods.goToHooks, {
      textDocument: { uri: editor.document.uri.toString() },
      position: { line: pos.line, character: pos.character },
    });
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    void vscode.window.showErrorMessage(`Reqnroll: Go to Hooks failed — ${msg}`);
    return;
  }

  if (!response.hooks || response.hooks.length === 0) {
    void vscode.window.showInformationMessage('Reqnroll: No hooks found at this position.');
    return;
  }

  if (response.hooks.length === 1) {
    await navigateToHook(response.hooks[0]);
    return;
  }

  const items = response.hooks.map((hook) => ({
    label: `$(symbol-event) ${hook.hookType}`,
    description: hook.methodName,
    detail: hook.hookOrder !== 0 ? `Order: ${hook.hookOrder}` : undefined,
    hook,
  }));

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder: `${response.hooks.length} hooks found — select to navigate`,
  });
  if (!picked) return;
  await navigateToHook(picked.hook);
}

async function navigateToHook(hook: GoToHookLocation): Promise<void> {
  await openAndReveal(vscode.Uri.parse(hook.uri), hook.startLine, hook.startChar);
}
