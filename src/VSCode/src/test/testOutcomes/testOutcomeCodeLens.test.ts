import * as assert from 'assert';
import { DocumentSymbol, SymbolInformation, SymbolKind } from 'vscode-languageclient/node';
import { GetTestOutcomeResponse, TestOutcomeRow } from '../../testOutcomes/testOutcomesService';
import {
  buildTooltip,
  collectMethodSymbols,
  combineOutcomes,
  renderTitle,
} from '../../testOutcomes/testOutcomeCodeLens';

function range(line: number) {
  return { start: { line, character: 0 }, end: { line: line + 1, character: 0 } };
}

function methodSymbol(name: string, line: number, children: DocumentSymbol[] = []): DocumentSymbol {
  return {
    name,
    kind: SymbolKind.Method,
    range: range(line),
    selectionRange: range(line),
    children,
  };
}

function namespaceSymbol(name: string, children: DocumentSymbol[]): DocumentSymbol {
  return {
    name,
    kind: SymbolKind.Namespace,
    range: range(0),
    selectionRange: range(0),
    children,
  };
}

function row(
  displayName: string,
  outcome: string,
  failedStepText?: string,
  extra: Partial<TestOutcomeRow> = {},
): TestOutcomeRow {
  return { displayName, outcome, durationMs: 0, stepCount: 0, failedStepText, ...extra };
}

function response(
  aggregate: string,
  rows: TestOutcomeRow[] = [],
  isStale = false,
): GetTestOutcomeResponse {
  return { found: true, aggregate, rows, isRunning: false, isStale };
}

suite('testOutcomeCodeLens', () => {
  suite('collectMethodSymbols', () => {
    test('collects a top-level Method symbol', () => {
      const result = collectMethodSymbols([methodSymbol('Add two numbers', 1)]);
      assert.strictEqual(result.length, 1);
      assert.strictEqual(result[0].name, 'Add two numbers');
    });

    test('descends into a Rule (Namespace-kind) child to find a nested scenario', () => {
      const scenario = methodSymbol('Nested scenario', 3);
      const rule = namespaceSymbol('My Rule', [scenario]);
      const result = collectMethodSymbols([rule]);
      assert.strictEqual(result.length, 1);
      assert.strictEqual(result[0].name, 'Nested scenario');
    });

    test('ignores non-Method symbols at any depth', () => {
      const step: DocumentSymbol = {
        name: 'Given a step',
        kind: SymbolKind.Field,
        range: range(2),
        selectionRange: range(2),
      };
      const scenario = methodSymbol('S', 1, [step]);
      const result = collectMethodSymbols([scenario]);
      assert.strictEqual(result.length, 1);
      assert.strictEqual(result[0].name, 'S');
    });

    test('drops SymbolInformation entries (the non-hierarchical shape this server never sends)', () => {
      const flat: SymbolInformation = {
        name: 'Flat',
        kind: SymbolKind.Method,
        location: { uri: 'file:///a.feature', range: range(0) },
      };
      assert.deepStrictEqual(collectMethodSymbols([flat]), []);
    });

    test('returns an empty list for an empty tree', () => {
      assert.deepStrictEqual(collectMethodSymbols([]), []);
    });
  });

  suite('combineOutcomes', () => {
    test('is Passed when every response passed', () => {
      const result = combineOutcomes([response('Passed', [row('r1', 'Passed')])]);
      assert.strictEqual(result.aggregate, 'Passed');
    });

    test('is Failed if any response failed, even alongside a passing one', () => {
      const result = combineOutcomes([
        response('Passed', [row('r1', 'Passed')]),
        response('Failed', [row('r2', 'Failed')]),
      ]);
      assert.strictEqual(result.aggregate, 'Failed');
    });

    test('flattens rows across every response', () => {
      const result = combineOutcomes([
        response('Passed', [row('r1', 'Passed')]),
        response('Failed', [row('r2', 'Failed')]),
      ]);
      assert.strictEqual(result.rows.length, 2);
    });

    test('is stale if any response is stale', () => {
      const result = combineOutcomes([response('Passed', [], false), response('Passed', [], true)]);
      assert.strictEqual(result.isStale, true);
    });
  });

  suite('renderTitle', () => {
    test('shows the check glyph for a passing aggregate', () => {
      assert.strictEqual(renderTitle(response('Passed')), '✓ Passed');
    });

    test('appends a stale marker when the outcome is stale', () => {
      assert.strictEqual(renderTitle(response('Passed', [], true)), '✓ Passed (stale)');
    });

    test('names the failing row and its step when exactly one row failed', () => {
      const result = renderTitle(
        response('Failed', [
          row('row 1', 'Passed'),
          row('row 2', 'Failed', 'When the calculation explodes'),
        ]),
      );
      assert.strictEqual(result, '✗ Failed — row 2: When the calculation explodes');
    });

    test('falls back to the row name alone when no failed-step text is known', () => {
      const result = renderTitle(response('Failed', [row('row 1', 'Failed')]));
      assert.strictEqual(result, '✗ Failed — row 1');
    });

    test('shows a count when more than one row failed', () => {
      const result = renderTitle(
        response('Failed', [row('r1', 'Failed'), row('r2', 'Failed'), row('r3', 'Passed')]),
      );
      assert.strictEqual(result, '✗ Failed (2 of 3 rows)');
    });
  });

  // ── buildTooltip (issue #723: lens hover showed nothing — same bug as VS's 4bdefaf5) ─────────

  suite('buildTooltip', () => {
    test('is undefined for a passing, non-stale outcome', () => {
      assert.strictEqual(buildTooltip(response('Passed')), undefined);
    });

    test('notes a stale passing outcome rather than showing nothing', () => {
      assert.strictEqual(
        buildTooltip(response('Passed', [], true)),
        '✓ Passed (stale — rerun to confirm)',
      );
    });

    test('describes a failing row with its step, step outcome kind, and error message', () => {
      const result = buildTooltip(
        response('Failed', [
          row('row 1', 'Failed', 'When the calculation explodes', {
            failedStepOutcome: 'Error',
            errorMessage: 'System.DivideByZeroException: Attempted to divide by zero.',
          }),
        ]),
      );
      assert.strictEqual(
        result,
        'row 1: When the calculation explodes (threw)\n' +
          'System.DivideByZeroException: Attempted to divide by zero.',
      );
    });

    test('falls back to the row name alone when no failed-step text is known', () => {
      const result = buildTooltip(response('Failed', [row('row 1', 'Failed')]));
      assert.strictEqual(result, 'row 1');
    });

    test('lists every failing row, unlike the inline title which only names the first', () => {
      const result = buildTooltip(
        response('Failed', [
          row('r1', 'Failed', 'step A', { failedStepOutcome: 'Undefined' }),
          row('r2', 'Passed'),
          row('r3', 'Failed', 'step B', { failedStepOutcome: 'BindingError' }),
        ]),
      );
      assert.strictEqual(result, 'r1: step A (undefined step)\n\nr3: step B (binding error)');
    });

    test('falls back to a bare "Failed" when the aggregate is Failed but no row is marked Failed', () => {
      // Defensive: shouldn't happen in practice (combineOutcomes only sets aggregate=Failed when
      // a row is), but must not throw or show an empty string.
      assert.strictEqual(buildTooltip(response('Failed', [row('r1', 'Passed')])), '✗ Failed');
    });
  });
});
