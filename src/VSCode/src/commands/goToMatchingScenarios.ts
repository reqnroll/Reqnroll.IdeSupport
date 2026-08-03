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
 * every scenario the hook binding at `uri`/`line`/`char` matches and shows a `QuickPick` to
 * navigate to one — always, even for a single match, matching `doFindStepUsages`'s behavior
 * (issue #373 follow-up: an earlier version auto-navigated on exactly one match, which was
 * inconsistent with the step-usages lens).
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

  const items = response.scenarios.map((scenario) => ({
    label: `$(symbol-method) ${scenario.scenarioName || '(untitled)'}`,
    description: scenario.isOutline ? 'Scenario Outline' : undefined,
    scenario,
  }));

  // Singular/plural wording matches the VS and Rider clients' equivalent surfaces verbatim
  // ("1 matching scenario" / "N matching scenarios") — previously always plural here, which read
  // as "1 matching scenarios" for a single match.
  const count = response.scenarios.length;
  const placeHolder =
    count === 1 ? '1 matching scenario — select to navigate' : `${count} matching scenarios — select to navigate`;
  const picked = await vscode.window.showQuickPick(items, { placeHolder });
  if (!picked) return;
  await navigateToScenario(picked.scenario);
}

async function navigateToScenario(scenario: MatchingScenarioLocation): Promise<void> {
  await openAndReveal(vscode.Uri.parse(scenario.uri), scenario.startLine, scenario.startChar);
}
