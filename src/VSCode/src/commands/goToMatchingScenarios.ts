import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from '../lsp/lspMethods';
import { openAndReveal } from '../util/navigationUtils';

interface GoToMatchingScenariosResponse {
  scenarios: MatchingScenarioLocation[];
}

interface MatchingScenarioLocation {
  uri: string;
  startLine: number;
  startChar: number;
  scenarioName: string;
  isOutline: boolean;
}

/**
 * Implements the hook-match-count CodeLens's click action (issue #373): queries the server for
 * every scenario the hook binding at `uri`/`line`/`char` matches, and navigates directly if
 * there's exactly one, or shows a `QuickPick` to choose among several.
 *
 * Unlike `doGoToHooks`, this is only ever invoked from a CodeLens click (the lens's own attribute
 * location is round-tripped verbatim as the command's arguments) — there's no command-palette/
 * keybinding entry point, so there's no "fall back to the active cursor" case to handle.
 */
export async function doGoToMatchingScenarios(
  client: LanguageClient,
  uri: string,
  line: number,
  character: number,
): Promise<void> {
  let response: GoToMatchingScenariosResponse;
  try {
    response = await client.sendRequest<GoToMatchingScenariosResponse>(
      ReqnrollMethods.goToMatchingScenarios,
      {
        textDocument: { uri },
        position: { line, character },
      },
    );
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    void vscode.window.showErrorMessage(`Reqnroll: Go to Matching Scenarios failed — ${msg}`);
    return;
  }

  if (!response.scenarios || response.scenarios.length === 0) {
    void vscode.window.showInformationMessage('Reqnroll: This hook has no matching scenarios.');
    return;
  }

  if (response.scenarios.length === 1) {
    await navigateToScenario(response.scenarios[0]);
    return;
  }

  const items = response.scenarios.map((scenario) => ({
    label: `$(symbol-method) ${scenario.scenarioName || '(untitled)'}`,
    description: scenario.isOutline ? 'Scenario Outline' : undefined,
    scenario,
  }));

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder: `${response.scenarios.length} matching scenarios — select to navigate`,
  });
  if (!picked) return;
  await navigateToScenario(picked.scenario);
}

async function navigateToScenario(scenario: MatchingScenarioLocation): Promise<void> {
  await openAndReveal(vscode.Uri.parse(scenario.uri), scenario.startLine, scenario.startChar);
}
