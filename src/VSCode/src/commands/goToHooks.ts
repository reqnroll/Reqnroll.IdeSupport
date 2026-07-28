import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from '../lsp/lspMethods';
import { openAndReveal } from '../util/navigationUtils';

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
 * Implements Hook Navigation ("Go to Hooks"): queries the server for hooks applicable at
 * `position` (defaulting to the active editor's cursor when omitted — the command-palette/
 * keybinding invocation path). When invoked from the hook-count CodeLens (issue #269) the server
 * passes `[uri, line, char, ownLevelOnly]` as arguments, resolved to `position` by the
 * `reqnroll.goToHooks` command handler, the same way `reqnroll.findStepUsages` already does.
 * `ownLevelOnly` (set only by CodeLens-sourced invocations) asks the server to filter the result
 * to hooks native to the resolved context level, so the picker matches exactly what the lens
 * counted rather than the fuller cumulative list a manual invocation returns.
 *
 * `alwaysShowPicker` (also set only by CodeLens-sourced invocations — issue #372 follow-up) shows
 * the `QuickPick` even for a single match, so clicking a lens always lets the user see which hook
 * it refers to rather than jumping straight there; the keybinding/command-palette path keeps the
 * original single-match shortcut.
 */
export async function doGoToHooks(
  client: LanguageClient,
  position?: {
    uri: string;
    line: number;
    character: number;
    ownLevelOnly?: boolean;
    alwaysShowPicker?: boolean;
  },
): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  const uri = position?.uri ?? editor?.document.uri.toString();
  const line = position?.line ?? editor?.selection.active.line;
  const character = position?.character ?? editor?.selection.active.character;
  if (uri === undefined || line === undefined || character === undefined) return;

  let response: GoToHooksResponse;
  try {
    response = await client.sendRequest<GoToHooksResponse>(ReqnrollMethods.goToHooks, {
      textDocument: { uri },
      position: { line, character },
      ownLevelOnly: position?.ownLevelOnly ?? false,
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

  if (response.hooks.length === 1 && !position?.alwaysShowPicker) {
    await navigateToHook(response.hooks[0]);
    return;
  }

  const items = response.hooks.map((hook) => ({
    label: `$(symbol-event) ${hook.hookType}`,
    description: hook.methodName,
    detail: hook.hookOrder !== 0 ? `Order: ${hook.hookOrder}` : undefined,
    hook,
  }));

  const placeHolder =
    response.hooks.length === 1
      ? '1 hook found — select to navigate'
      : `${response.hooks.length} hooks found — select to navigate`;

  const picked = await vscode.window.showQuickPick(items, { placeHolder });
  if (!picked) return;
  await navigateToHook(picked.hook);
}

async function navigateToHook(hook: GoToHookLocation): Promise<void> {
  await openAndReveal(vscode.Uri.parse(hook.uri), hook.startLine, hook.startChar);
}
