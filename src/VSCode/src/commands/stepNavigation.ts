import * as path from 'path';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from '../lsp/lspMethods';
import { showError, showInfo } from '../logging/appNotify';
import { sendTelemetryEvent } from '../telemetry';
import { TelemetryEvents, TelemetryProperties } from '../telemetryEvents';
import { openAndReveal } from '../util/navigationUtils';
import {
  StepDefinitionItem,
  formatBindingAttribute,
  formatMethodName,
  warnSourceNotOnThisMachine,
} from '../util/stepDefinitionItems';

interface FindStepDefinitionsResponse {
  items: StepDefinitionItem[];
}

/**
 * Implements Go to Step Definition using the custom `reqnroll/findStepDefinitions` request — the
 * same bindings as `textDocument/definition` (both go through the server's
 * `StepAtPositionResolver`), plus the class, method and binding attribute of each (issue #757).
 * Navigates directly if there's exactly one binding, or shows a `QuickPick` of
 * `Class.Method` / `[Given("expression")]` / file:line — the same rows Visual Studio and Rider list,
 * which show why a step is ambiguous. A binding whose source isn't on this machine is listed and
 * explained rather than left out (issue #540). F12 / Peek Definition still use the standard request.
 */
export async function doGoToStepDefinition(client: LanguageClient): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) return;

  const pos = editor.selection.active;
  let response: FindStepDefinitionsResponse | null;
  try {
    response = await client.sendRequest<FindStepDefinitionsResponse | null>(
      ReqnrollMethods.findStepDefinitions,
      {
        textDocument: { uri: editor.document.uri.toString() },
        position: { line: pos.line, character: pos.character },
      },
    );
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    void showError(`Reqnroll: Go to Step Definition failed — ${msg}`);
    return;
  }

  const bindings = distinctByPosition(response?.items ?? []);

  // A genuine navigation (issue #899): the server counts reqnroll/findStepDefinitions as a lookup.
  // LocationCount is the navigable rows, 0 when there is nowhere to go.
  sendTelemetryEvent(TelemetryEvents.goToStepDefinitionCommandExecuted, {
    [TelemetryProperties.locationCount]: bindings.filter(isNavigable).length,
  });

  if (bindings.length === 0) {
    void showInfo('Reqnroll: No step definition found at this position.');
    return;
  }

  if (bindings.length === 1) {
    await navigateTo(bindings[0]);
    return;
  }

  const items = bindings.map((item) => {
    // An entry whose source isn't on this machine can't be navigated to, so it gets a different
    // icon and says so in the row rather than looking identical and then doing nothing on click.
    const sourceFile = (item.isResolved ?? true) ? item.sourceFile : undefined;
    return {
      label: `${sourceFile ? '$(symbol-method)' : '$(error)'} ${formatMethodName(item)}`,
      description: formatBindingAttribute(item),
      detail: sourceFile
        ? `${uriToRelativePath(vscode.Uri.file(sourceFile).toString())}:${item.sourceLine + 1}`
        : 'source not on this machine',
      item,
    };
  });

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder: `${bindings.length} step definitions found — select to navigate`,
    matchOnDescription: true,
  });
  if (!picked) return;
  await navigateTo(picked.item);
}

/**
 * Collapses bindings at the same source position: one method carrying two attributes that both
 * match the step is reported once per binding, but is a single place to navigate to.
 */
export function distinctByPosition(items: readonly StepDefinitionItem[]): StepDefinitionItem[] {
  const seen = new Set<string>();
  return items.filter((item) => {
    const key = `${item.sourceFile ?? item.recordedSourceFile}|${item.sourceLine}|${item.sourceChar}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function isNavigable(
  item: StepDefinitionItem,
): item is StepDefinitionItem & { sourceFile: string } {
  return !!item.sourceFile && (item.isResolved ?? true);
}

async function navigateTo(item: StepDefinitionItem): Promise<void> {
  if (!isNavigable(item)) {
    warnSourceNotOnThisMachine(item);
    return;
  }
  await openAndReveal(vscode.Uri.file(item.sourceFile), item.sourceLine, item.sourceChar);
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
      // Require a path-separator boundary after the folder prefix, so a sibling folder that merely
      // shares a prefix (e.g. /w/Foo vs /w/FooBar) is not treated as containing the file and the
      // label resolved against the wrong folder (issue #1015). A folder path can itself end in a
      // separator (a filesystem root, or a caller-supplied trailing separator), so only append one
      // when it is missing.
      const folderLower = folderFsPath.toLowerCase();
      const prefixLower = folderLower.endsWith(path.sep) ? folderLower : folderLower + path.sep;
      if (fsPathLower.startsWith(prefixLower)) {
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
