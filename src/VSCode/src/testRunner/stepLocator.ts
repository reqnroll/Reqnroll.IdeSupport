import * as vscode from 'vscode';

/**
 * Finds the `.feature` line whose trimmed text matches `stepText` (Reqnroll's stdout trace records
 * each step's keyword+text verbatim), scanning forward from the scenario's header line and stopping
 * at the next scenario/feature/rule/examples header. Known limitation: for a Scenario Outline step
 * containing a `<placeholder>`, Reqnroll's trace shows the row's *substituted* value, which won't
 * literal-match the `.feature` file's own placeholder text — the failed-step mark silently isn't
 * attached in that case rather than mis-highlighting a line.
 *
 * Moved out of the pre-`TestController` `resultDecorationService.ts` (issue #504's migration to
 * `vscode.TestController`) — the failed-step *presentation* is now `TestMessage.location`, a native
 * Testing-API primitive, but the *lookup* (matching stdout step text back to a `.feature` line) is
 * unchanged and still needed.
 */
export function findStepLine(
  document: vscode.TextDocument,
  fromLine: number,
  stepText: string,
): number | undefined {
  const normalized = stepText.trim();
  for (let line = fromLine; line < document.lineCount; line++) {
    const text = document.lineAt(line).text.trim();
    if (text === normalized) return line;
    if (line > fromLine && /^(Scenario|Feature|Rule|Examples|Background)\b/.test(text)) break;
  }
  return undefined;
}
