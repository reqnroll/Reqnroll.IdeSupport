/**
 * Minimal, purpose-built TRX (`.trx`, VSTest's XML result format) extractor — only pulls the
 * three things `runTest.ts` needs (`outcome`, `testName`, captured `StdOut`, and `ErrorInfo`),
 * via regex rather than a full XML parser/DOM, since no XML-parsing dependency exists yet in this
 * extension and adding one for three fields isn't worth it. Pure functions, no `vscode` import —
 * directly unit-testable without the Extension Development Host.
 */

/** One `<UnitTestResult>` entry from a TRX file's `<Results>` section. */
export interface TrxUnitTestResult {
  readonly testName: string;
  readonly outcome: string;
  readonly stdOut: string;
  readonly errorMessage?: string;
  readonly stackTrace?: string;
}

const UNIT_TEST_RESULT_RE = /<UnitTestResult\b([^>]*?)(?:\/>|>([\s\S]*?)<\/UnitTestResult>)/g;
const STDOUT_RE = /<StdOut>([\s\S]*?)<\/StdOut>/;
const ERROR_MESSAGE_RE = /<Message>([\s\S]*?)<\/Message>/;
const STACK_TRACE_RE = /<StackTrace>([\s\S]*?)<\/StackTrace>/;

function attr(attrsText: string, name: string): string | undefined {
  const match = new RegExp(`${name}="([^"]*)"`).exec(attrsText);
  return match ? unescapeXml(match[1]) : undefined;
}

/** Parses every `<UnitTestResult>` entry out of a TRX file's raw text. */
export function parseTrx(trxXml: string): TrxUnitTestResult[] {
  const results: TrxUnitTestResult[] = [];
  UNIT_TEST_RESULT_RE.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = UNIT_TEST_RESULT_RE.exec(trxXml)) !== null) {
    const attrsText = match[1];
    const body = match[2] ?? '';

    const stdOutMatch = STDOUT_RE.exec(body);
    const errorMatch = ERROR_MESSAGE_RE.exec(body);
    const stackMatch = STACK_TRACE_RE.exec(body);

    results.push({
      testName: attr(attrsText, 'testName') ?? '',
      outcome: attr(attrsText, 'outcome') ?? '',
      stdOut: stdOutMatch ? unescapeXml(stdOutMatch[1]) : '',
      errorMessage: errorMatch ? unescapeXml(errorMatch[1]) : undefined,
      stackTrace: stackMatch ? unescapeXml(stackMatch[1]) : undefined,
    });
  }
  return results;
}

function unescapeXml(text: string): string {
  return text
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&#(\d+);/g, (_match, code: string) => String.fromCharCode(Number(code)))
    .replace(/&amp;/g, '&'); // must run last, or "&amp;lt;" would double-unescape to "<"
}

// ── Reqnroll's own stdout step trace ───────────────────────────────────────

/**
 * The full outcome-prefix vocabulary Reqnroll's `TestTracer` emits (design doc §6, decompiled from
 * `Reqnroll.Tracing.TestTracer`) — every step outcome, not just the three seen in the original
 * `dotnet test` spike.
 */
export type StepOutcome =
  | 'done'
  | 'error'
  | 'skipped'
  | 'skippedBecauseOfPreviousErrors'
  | 'pending'
  | 'bindingError'
  | 'undefined';

export interface StepTraceEntry {
  readonly stepText: string;
  readonly outcome: StepOutcome;
  readonly detail?: string;
}

// Order doesn't affect matching (the prefixes are mutually exclusive by construction), but is
// kept in the same order as design doc §6's table for readability.
const OUTCOME_PREFIXES: ReadonlyArray<{ prefix: string; outcome: StepOutcome }> = [
  { prefix: 'done:', outcome: 'done' },
  { prefix: 'error:', outcome: 'error' },
  { prefix: 'skipped because of previous errors', outcome: 'skippedBecauseOfPreviousErrors' },
  { prefix: 'skipped:', outcome: 'skipped' },
  { prefix: 'pending:', outcome: 'pending' },
  { prefix: 'binding error:', outcome: 'bindingError' },
  { prefix: 'undefined:', outcome: 'undefined' },
];

/**
 * Parses Reqnroll's own step-by-step trace out of a test's captured stdout — the reliable signal
 * design doc §6 confirms is present by default (no `reqnroll.json` opt-in) and correlates
 * unambiguously to the `.feature` file's own step order. Deliberately does NOT consult
 * `ErrorStackTrace`/`stackTrace` — design doc §6 confirms that always attributes to the scenario's
 * last step regardless of which step actually failed (a `#line hidden`-region generation artifact).
 */
export function parseStepTrace(stdOut: string): StepTraceEntry[] {
  const lines = stdOut.split(/\r\n|\n|\r/);
  const entries: StepTraceEntry[] = [];
  let pendingStepText: string | null = null;

  for (const rawLine of lines) {
    const line = rawLine.trim();
    if (line.length === 0) continue;

    const arrowMatch = /^->\s*(.*)$/.exec(line);
    if (arrowMatch) {
      const rest = arrowMatch[1];
      const matched = OUTCOME_PREFIXES.find((p) => rest.startsWith(p.prefix));
      if (matched && pendingStepText) {
        const detail = rest.slice(matched.prefix.length).trim();
        entries.push({
          stepText: pendingStepText,
          outcome: matched.outcome,
          detail: detail.length > 0 ? detail : undefined,
        });
      }
      pendingStepText = null;
      continue;
    }

    // Any non-"->" line is a step's own keyword+text (Given/When/Then/And/But ...).
    pendingStepText = line;
  }

  return entries;
}

/** The first failing outcome in a step trace — `error`/`bindingError`/`undefined` have no underlying step method, so any of the three counts as the failure to report. */
export function findFirstFailure(entries: readonly StepTraceEntry[]): StepTraceEntry | undefined {
  return entries.find(
    (e) => e.outcome === 'error' || e.outcome === 'bindingError' || e.outcome === 'undefined',
  );
}
