/**
 * In-memory, per-scenario last-run result — tracked entirely in the extension's own state (design
 * doc §5's VS Code decision: no `vscode.TestRun`/`TestMessage`, no native Testing-panel presence).
 * Not persisted across window reloads, same as VS Code's own Testing panel loses its run history on
 * reload.
 */

export type ScenarioOutcome = 'passed' | 'failed';

export interface FailedStepInfo {
  readonly stepText: string;
  readonly detail?: string;
}

export interface ScenarioRunResult {
  readonly outcome: ScenarioOutcome;
  readonly failedStep?: FailedStepInfo;
  readonly ranAt: number;
}

/** Keyed by `<document uri>#<0-based scenario header line>` — matches how a CodeLens/decoration is addressed. */
export class TestResultStore {
  private readonly results = new Map<string, ScenarioRunResult>();

  private static keyFor(uri: string, startLine: number): string {
    return `${uri}#${startLine}`;
  }

  set(uri: string, startLine: number, result: ScenarioRunResult): void {
    this.results.set(TestResultStore.keyFor(uri, startLine), result);
  }

  get(uri: string, startLine: number): ScenarioRunResult | undefined {
    return this.results.get(TestResultStore.keyFor(uri, startLine));
  }

  /** Every cached result for `uri`, with the scenario header line each one is keyed at. */
  getAllForUri(uri: string): ReadonlyArray<{ startLine: number; result: ScenarioRunResult }> {
    const prefix = `${uri}#`;
    const out: Array<{ startLine: number; result: ScenarioRunResult }> = [];
    for (const [key, result] of this.results) {
      if (key.startsWith(prefix)) {
        out.push({ startLine: Number(key.slice(prefix.length)), result });
      }
    }
    return out;
  }

  clearForUri(uri: string): void {
    const prefix = `${uri}#`;
    for (const key of [...this.results.keys()]) {
      if (key.startsWith(prefix)) this.results.delete(key);
    }
  }
}
