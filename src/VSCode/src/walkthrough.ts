import * as vscode from 'vscode';

/** Fully-qualified walkthrough ID: `<publisher>.<name>#<walkthroughId>` (see package.json). */
export const WALKTHROUGH_ID = 'Reqnroll.reqnroll-ide-support#reqnroll.getStarted';

/** globalState key recording that the Get Started walkthrough has already been shown once. */
export const WALKTHROUGH_SHOWN_KEY = 'reqnroll.walkthroughShown';

/**
 * Opens the Get Started walkthrough the first time the extension activates in a given VS Code
 * profile, and never again (the user can still reopen it via Help > Open Walkthrough...).
 *
 * Skipped outside production mode so Extension Development Host / test runs, which always start
 * with empty state, don't pop it open. The flag is set before the command runs so a failure to
 * open never turns into a retry on every activation.
 */
export async function showWalkthroughOnFirstActivation(
  context: Pick<vscode.ExtensionContext, 'extensionMode' | 'globalState'>,
  executeCommand: (command: string, ...args: unknown[]) => Thenable<unknown> = (command, ...args) =>
    vscode.commands.executeCommand(command, ...args),
): Promise<boolean> {
  if (context.extensionMode !== vscode.ExtensionMode.Production) return false;
  if (context.globalState.get<boolean>(WALKTHROUGH_SHOWN_KEY)) return false;

  await context.globalState.update(WALKTHROUGH_SHOWN_KEY, true);
  await executeCommand('workbench.action.openWalkthrough', WALKTHROUGH_ID, false);
  return true;
}
