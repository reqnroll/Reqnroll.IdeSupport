import * as assert from 'assert';
import type * as vscode from 'vscode';
import { GeneralFileLogChannel, formatLine } from '../../logging/generalFileLog';

// Portable subset of Reqnroll.IdeSupport.Common.Logging.LogLineFormatter.FormatPreamble shared
// with the .NET side and the Rider plugin's ReqnrollDebugLogger.formatLine (issue #626).
suite('formatLine', () => {
  test('renders a UTC ISO-8601 timestamp, a padded level, and the message', () => {
    const line = formatLine('Info', 'hello');
    assert.match(line, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[Info {3}\] hello$/);
  });

  test('pads every real level to the same width', () => {
    assert.match(formatLine('Error', 'x'), /\[Error {2}\] x$/);
    assert.match(formatLine('Warning', 'x'), /\[Warning\] x$/);
    assert.match(formatLine('Info', 'x'), /\[Info {3}\] x$/);
    assert.match(formatLine('Verbose', 'x'), /\[Verbose\] x$/);
  });
});

/** A fake LogOutputChannel that records each show() invocation's arguments. */
function createFakeInner(): { inner: vscode.LogOutputChannel; showCalls: unknown[][] } {
  const showCalls: unknown[][] = [];
  const fake = {
    name: 'fake',
    show(...args: unknown[]): void {
      showCalls.push(args);
    },
    trace(): void {},
    debug(): void {},
    info(): void {},
    warn(): void {},
    error(): void {},
    append(): void {},
    appendLine(): void {},
    replace(): void {},
    clear(): void {},
    hide(): void {},
    dispose(): void {},
  };
  return { inner: fake as unknown as vscode.LogOutputChannel, showCalls };
}

// Issue #1000: autoShowOnWarnOrError is documented as "first warn/error", but both warn() and
// error() used to show on every call, and show() ignored its preserveFocus argument — so the
// Output panel grabbed focus mid-typing on every warning.
suite('GeneralFileLogChannel auto-show (issue #1000)', () => {
  test('auto-show fires only once across repeated warn/error calls', () => {
    const { inner, showCalls } = createFakeInner();
    const channel = new GeneralFileLogChannel('Reqnroll', undefined, true, inner);

    channel.warn('w1');
    channel.error('e1');
    channel.warn('w2');
    channel.error(new Error('e2'));

    assert.strictEqual(showCalls.length, 1, 'auto-show should fire exactly once');
  });

  test('auto-show preserves focus (does not steal it)', () => {
    const { inner, showCalls } = createFakeInner();
    const channel = new GeneralFileLogChannel('Reqnroll', undefined, true, inner);

    channel.warn('w');

    assert.deepStrictEqual(showCalls[0], [true], 'auto-show must forward preserveFocus = true');
  });

  test('does not auto-show at all when disabled', () => {
    const { inner, showCalls } = createFakeInner();
    const channel = new GeneralFileLogChannel('Reqnroll', undefined, false, inner);

    channel.warn('w');
    channel.error('e');

    assert.strictEqual(showCalls.length, 0);
  });

  test('forwards the caller intent: bare show() focuses, show(true) preserves focus', () => {
    const { inner, showCalls } = createFakeInner();
    const channel = new GeneralFileLogChannel('Reqnroll', undefined, false, inner);

    channel.show(); // reqnroll.showOutputChannel — an explicit user action wants focus
    channel.show(true); // reveal without stealing focus
    channel.show(2, false); // explicit column, focus

    assert.deepStrictEqual(showCalls, [[undefined, undefined], [true], [2, false]]);
  });
});
