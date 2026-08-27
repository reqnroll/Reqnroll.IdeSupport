import * as vscode from 'vscode';
import { TestResultStore } from './testResultStore';

/**
 * Renders the pass/fail scenario-line indicator and the failed-step mark (design doc §1 items 2/3,
 * §5 "Pass/fail and failed-step state are tracked entirely in our own extension state and rendered
 * via custom `TextEditorDecorationType` gutter icons"), read from `TestResultStore`.
 *
 * No `gutterIconPath` image assets exist in this extension yet, so — like `tableHighlightService.ts`
 * — this uses `before`-content-text decorations at column 0 of the relevant line rather than true
 * gutter icon images; visually equivalent for this purpose without adding new asset files.
 *
 * The failed-step decoration is deliberately styled as a plain warning-colored marker, not an error
 * squiggle, per design doc §6's "stay visually low-noise against genuine diagnostics" requirement
 * (existing `.feature` error/warning diagnostics already own that visual language).
 */
export class ResultDecorationService implements vscode.Disposable {
  private readonly passedDecoration = vscode.window.createTextEditorDecorationType({
    before: { contentText: '✓ ', color: '#3FB950', fontWeight: 'bold' },
  });

  private readonly failedDecoration = vscode.window.createTextEditorDecorationType({
    before: { contentText: '✗ ', color: '#F85149', fontWeight: 'bold' },
  });

  private readonly failedStepDecoration = vscode.window.createTextEditorDecorationType({
    before: { contentText: '⚠ ', color: '#E3B341' },
  });

  private readonly disposables: vscode.Disposable[] = [];

  constructor(private readonly store: TestResultStore) {
    this.disposables.push(
      this.passedDecoration,
      this.failedDecoration,
      this.failedStepDecoration,
      vscode.window.onDidChangeActiveTextEditor(() => this.refreshVisibleEditors()),
      vscode.window.onDidChangeVisibleTextEditors(() => this.refreshVisibleEditors()),
    );
  }

  dispose(): void {
    for (const disposable of this.disposables) disposable.dispose();
  }

  /** Redraws decorations for `uri`'s visible editor(s) — called by `runTest.ts` right after the store is updated, so results appear immediately without waiting for an editor-change event. */
  applyResult(uri: string): void {
    for (const editor of vscode.window.visibleTextEditors) {
      if (editor.document.uri.toString() === uri) this.refreshEditor(editor);
    }
  }

  private refreshVisibleEditors(): void {
    for (const editor of vscode.window.visibleTextEditors) this.refreshEditor(editor);
  }

  private refreshEditor(editor: vscode.TextEditor): void {
    if (editor.document.languageId !== 'gherkin') return;

    const uri = editor.document.uri.toString();
    const entries = this.store.getAllForUri(uri);

    const passedRanges: vscode.Range[] = [];
    const failedRanges: vscode.Range[] = [];
    const failedStepDecorations: vscode.DecorationOptions[] = [];

    for (const { startLine, result } of entries) {
      const scenarioLineRange = new vscode.Range(startLine, 0, startLine, 0);
      if (result.outcome === 'passed') {
        passedRanges.push(scenarioLineRange);
      } else {
        failedRanges.push(scenarioLineRange);
      }

      if (result.failedStep) {
        const stepLine = findStepLine(editor.document, startLine, result.failedStep.stepText);
        if (stepLine !== undefined) {
          failedStepDecorations.push({
            range: new vscode.Range(stepLine, 0, stepLine, 0),
            hoverMessage: result.failedStep.detail ?? result.failedStep.stepText,
          });
        }
      }
    }

    editor.setDecorations(this.passedDecoration, passedRanges);
    editor.setDecorations(this.failedDecoration, failedRanges);
    editor.setDecorations(this.failedStepDecoration, failedStepDecorations);
  }
}

/**
 * Finds the `.feature` line whose trimmed text matches `stepText` (Reqnroll's stdout trace records
 * each step's keyword+text verbatim), scanning forward from the scenario's header line and stopping
 * at the next scenario/feature/rule/examples header. Known v1 limitation: for a Scenario Outline
 * step containing a `<placeholder>`, Reqnroll's trace shows the row's *substituted* value, which
 * won't literal-match the `.feature` file's own placeholder text — the failed-step mark silently
 * doesn't render in that case rather than mis-highlighting a line, matching this service's low-noise
 * requirement above.
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
