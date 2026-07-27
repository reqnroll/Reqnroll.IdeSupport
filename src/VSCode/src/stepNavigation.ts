import * as path from 'path';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from './lspMethods';
import { openAndReveal } from './navigationUtils';

interface GoToStepDefinitionsResponse {
  stepDefinitions: GoToStepDefinitionLocation[];
}

interface GoToStepDefinitionLocation {
  uri: string;
  startLine: number;
  startChar: number;
  stepType: string;
  methodName: string;
}

/**
 * Implements Go to Step Definition: queries the server for step definitions matching the step
 * at the cursor and navigates directly if there's exactly one, or shows a rich `QuickPick`
 * (method name + step type) to choose among several.
 */
export async function doGoToStepDefinition(client: LanguageClient): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) return;

  const pos = editor.selection.active;
  let response: GoToStepDefinitionsResponse;
  try {
    response = await client.sendRequest<GoToStepDefinitionsResponse>(
      ReqnrollMethods.goToStepDefinitions,
      {
        textDocument: { uri: editor.document.uri.toString() },
        position: { line: pos.line, character: pos.character },
      },
    );
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    void vscode.window.showErrorMessage(`Reqnroll: Go to Step Definition failed — ${msg}`);
    return;
  }

  if (!response.stepDefinitions || response.stepDefinitions.length === 0) {
    void vscode.window.showInformationMessage(
      'Reqnroll: No step definition found at this position.',
    );
    return;
  }

  if (response.stepDefinitions.length === 1) {
    await navigateToStepDefinition(response.stepDefinitions[0]);
    return;
  }

  const items = response.stepDefinitions.map((def) => ({
    label: `$(symbol-method) ${def.methodName}`,
    description: `[${def.stepType}]`,
    detail: uriToRelativePath(def.uri),
    def,
  }));

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder: `${response.stepDefinitions.length} step definitions found — select to navigate`,
  });
  if (!picked) return;
  await navigateToStepDefinition(picked.def);
}

async function navigateToStepDefinition(def: GoToStepDefinitionLocation): Promise<void> {
  await openAndReveal(vscode.Uri.parse(def.uri), def.startLine, def.startChar);
}

/**
 * Renders `uriStr` relative to whichever of `folderFsPaths` contains it, falling back to the bare
 * filename when none do (or on a parse failure). Compares case-insensitively: file URIs returned
 * by the .NET LSP server can normalize a drive letter's case (e.g. `file:///c:/...`) differently
 * than a workspace folder's `fsPath` (cased as the user opened it), and a case-sensitive
 * comparison would then miss a folder that genuinely contains the file — silently falling through
 * to the less useful bare-filename label instead of erroring, so the mismatch was easy to miss
 * (issue #324). Pure function (folder paths passed in) so it's directly testable without a
 * running Extension Host workspace — see stepNavigation.test.ts.
 */
export function resolveRelativePathIn(uriStr: string, folderFsPaths: readonly string[]): string {
  try {
    const uri = vscode.Uri.parse(uriStr);
    const fsPathLower = uri.fsPath.toLowerCase();
    for (const folderFsPath of folderFsPaths) {
      if (fsPathLower.startsWith(folderFsPath.toLowerCase())) {
        return path.relative(folderFsPath, uri.fsPath);
      }
    }
    return path.basename(uri.fsPath);
  } catch {
    return uriStr;
  }
}

function uriToRelativePath(uriStr: string): string {
  const folders = vscode.workspace.workspaceFolders;
  return resolveRelativePathIn(uriStr, folders ? folders.map((f) => f.uri.fsPath) : []);
}
