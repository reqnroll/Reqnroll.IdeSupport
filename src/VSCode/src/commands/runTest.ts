import * as vscode from 'vscode';
import { findOwningProjectFile, ProjectManager } from '../lsp/projectManager';
import { runDotnetTest } from '../testRunner/dotnetTestRunner';
import { ResultDecorationService } from '../testRunner/resultDecorationService';
import { buildTestFilter } from '../testRunner/testFilterBuilder';
import { ScenarioRunResult, TestResultStore } from '../testRunner/testResultStore';
import { findFirstFailure, parseStepTrace } from '../testRunner/trxParser';
import { fireCodeLensRefresh } from './codeLensRefresh';
import { RunTestCommandArgs } from './runCodeLens';

/**
 * Implements the "▶ Run" CodeLens command (design doc §5 — VS Code's own `dotnet test --filter`
 * execution, no `TestController`). `runTestFn` is injectable for testing (defaults to the real
 * `runDotnetTest`, which shells out); tests substitute a fake to avoid actually invoking `dotnet`.
 */
export async function doRunTest(
  projectManager: Pick<ProjectManager, 'getKnownProjects'>,
  resultStore: TestResultStore,
  decorationService: ResultDecorationService,
  args: RunTestCommandArgs,
  runTestFn: typeof runDotnetTest = runDotnetTest,
): Promise<void> {
  const featureFilePath = vscode.Uri.parse(args.uri).fsPath;
  const projectFile = findOwningProjectFile(featureFilePath, projectManager.getKnownProjects());
  if (!projectFile) {
    void vscode.window.showErrorMessage(
      `Reqnroll: could not find the project that owns ${featureFilePath}.`,
    );
    return;
  }

  const filterExpr = buildTestFilter(args.targets);
  if (!filterExpr) {
    void vscode.window.showErrorMessage('Reqnroll: no test target(s) resolved for this scenario.');
    return;
  }

  const runResult = await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: 'Reqnroll: running test...',
      cancellable: true,
    },
    (_progress, token) => runTestFn(projectFile, filterExpr, token),
  );

  if (!runResult) {
    void vscode.window.showErrorMessage(`Reqnroll: dotnet test failed to run for ${projectFile}.`);
    return;
  }

  if (runResult.results.length === 0) {
    void vscode.window.showWarningMessage(
      'Reqnroll: dotnet test ran but produced no results for this filter.',
    );
    return;
  }

  const failingResult = runResult.results.find((r) => r.outcome === 'Failed');
  const outcome: ScenarioRunResult['outcome'] = failingResult ? 'failed' : 'passed';

  let failedStep: ScenarioRunResult['failedStep'];
  if (failingResult) {
    const failure = findFirstFailure(parseStepTrace(failingResult.stdOut));
    if (failure) {
      failedStep = { stepText: failure.stepText, detail: failure.detail };
    }
  }

  resultStore.set(args.uri, args.range.start.line, { outcome, failedStep, ranAt: Date.now() });
  decorationService.applyResult(args.uri);
  fireCodeLensRefresh();
}
