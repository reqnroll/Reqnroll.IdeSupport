import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from './lspMethods';
import { openAndReveal } from './navigationUtils';

interface FindStepUsagesResponse {
  isBinding: boolean;
  locations: FindStepUsageItem[];
}

interface FindStepUsageItem {
  uri: string;
  startLine: number;
  startChar: number;
  endLine: number;
  endChar: number;
  stepText?: string;
  keyword?: string;
  scenarioName?: string;
  projectName?: string;
  featureName?: string;
  ruleName?: string;
}

/**
 * Implements Find Step Definition Usages: queries the server for feature-file usages of the
 * step definition binding at `line`/`char`, then either shows a `QuickPick` to navigate among
 * them or reports there are none.
 */
export async function doFindStepUsages(
  client: LanguageClient,
  uriStr: string,
  line: number,
  char: number,
): Promise<void> {
  let response: FindStepUsagesResponse | null | undefined;
  try {
    response = await client.sendRequest<FindStepUsagesResponse | null>(
      ReqnrollMethods.findStepUsages,
      {
        textDocument: { uri: uriStr },
        position: { line, character: char },
        context: { includeDeclaration: false },
      },
    );
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    void vscode.window.showErrorMessage(`Reqnroll: Find Step Usages failed — ${msg}`);
    return;
  }

  if (!response?.isBinding) {
    void vscode.window.showInformationMessage(
      'Reqnroll: The cursor is not on a step definition binding.',
    );
    return;
  }

  if (response.locations.length === 0) {
    void vscode.window.showInformationMessage(
      'Reqnroll: No usages found for this step definition.',
    );
    return;
  }

  const items = response.locations.map((loc) => {
    const keyword = loc.keyword ?? '';
    const stepText = loc.stepText ?? '';
    const label = stepText
      ? `$(file-code) ${[keyword, stepText].filter(Boolean).join(' ')}`
      : `$(file-code) ${vscode.Uri.parse(loc.uri).path.split('/').pop() ?? loc.uri}`;
    const featureAndRule = [loc.featureName, loc.ruleName].filter(Boolean).join(' / ');
    const feature = featureAndRule ? `(${featureAndRule})` : '';
    const scenario = loc.scenarioName ?? '';
    const description = [feature, scenario].filter(Boolean).join(' ');
    return { label, description, detail: loc.projectName, loc };
  });

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder: `${response.locations.length} step usage(s) — select to navigate`,
    matchOnDescription: true,
    matchOnDetail: true,
  });
  if (!picked) return;

  await openAndReveal(vscode.Uri.parse(picked.loc.uri), picked.loc.startLine, picked.loc.startChar);
}
