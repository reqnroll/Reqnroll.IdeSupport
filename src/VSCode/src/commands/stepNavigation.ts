import * as path from 'path';
import * as vscode from 'vscode';
import {
  DefinitionRequest,
  LanguageClient,
  Location,
  LocationLink,
} from 'vscode-languageclient/node';
import { openAndReveal } from '../util/navigationUtils';

interface ResolvedLocation {
  uri: string;
  line: number;
  char: number;
}

/**
 * Implements Go to Step Definition using the standard `textDocument/definition` request (the
 * same one VS/Rider's generic LSP clients use — see `DefinitionHandler` server-side). Navigates
 * directly if there's exactly one location, or shows a `QuickPick` built from each candidate's
 * own source line (mirroring what VS's built-in multi-definition results window shows: source
 * text + filename + line number) when there's more than one.
 */
export async function doGoToStepDefinition(client: LanguageClient): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) return;

  const pos = editor.selection.active;
  let result: Location | Location[] | LocationLink[] | null;
  try {
    result = await client.sendRequest(DefinitionRequest.type, {
      textDocument: { uri: editor.document.uri.toString() },
      position: { line: pos.line, character: pos.character },
    });
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    void vscode.window.showErrorMessage(`Reqnroll: Go to Step Definition failed — ${msg}`);
    return;
  }

  const locations = normalizeLocations(result);
  if (locations.length === 0) {
    void vscode.window.showInformationMessage(
      'Reqnroll: No step definition found at this position.',
    );
    return;
  }

  if (locations.length === 1) {
    await navigateTo(locations[0]);
    return;
  }

  const items = await Promise.all(
    locations.map(async (loc) => ({
      label: await getSourceLineText(loc),
      description: `${uriToRelativePath(loc.uri)}:${loc.line + 1}`,
      loc,
    })),
  );

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder: `${locations.length} step definitions found — select to navigate`,
  });
  if (!picked) return;
  await navigateTo(picked.loc);
}

/** Collapses the three shapes `textDocument/definition` can return into a flat location list. */
function normalizeLocations(
  result: Location | Location[] | LocationLink[] | null,
): ResolvedLocation[] {
  if (!result) return [];
  const items = Array.isArray(result) ? result : [result];
  return items.map((item) =>
    'targetUri' in item
      ? {
          uri: item.targetUri,
          line: item.targetRange.start.line,
          char: item.targetRange.start.character,
        }
      : { uri: item.uri, line: item.range.start.line, char: item.range.start.character },
  );
}

async function navigateTo(loc: ResolvedLocation): Promise<void> {
  await openAndReveal(vscode.Uri.parse(loc.uri), loc.line, loc.char);
}

/** Reads the trimmed source text of `loc`'s line, e.g. `public void AddNumbers(int a, int b)`. */
async function getSourceLineText(loc: ResolvedLocation): Promise<string> {
  try {
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.parse(loc.uri));
    return doc.lineAt(loc.line).text.trim();
  } catch {
    return path.basename(vscode.Uri.parse(loc.uri).fsPath);
  }
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
