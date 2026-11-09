import * as assert from 'assert';
import { EventEmitter } from 'events';
import type * as fs from 'fs';
import type * as vscode from 'vscode';
import { GeneralFileLogChannel, formatLine } from '../../logging/generalFileLog';

// Portable subset of Reqnroll.IdeSupport.Common.Logging.LogLineFormatter.FormatPreamble shared
// with the .NET side and the Rider plugin's ReqnrollDebugLogger.formatLine.
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

// autoShowOnWarnOrError means "first warn/error": neither warn() nor error() may show on every
// call, and show() must honour its preserveFocus argument — otherwise the Output panel grabs
// focus mid-typing on every warning.
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

/** A fake write stream that records writes and destroy() calls and can emit 'error'. */
function createFakeStream(): {
  stream: fs.WriteStream;
  writes: string[];
  destroyed: () => number;
  emitter: EventEmitter;
} {
  const emitter = new EventEmitter();
  const writes: string[] = [];
  let destroyCount = 0;
  const fake = Object.assign(emitter, {
    write(chunk: string): boolean {
      writes.push(chunk);
      return true;
    },
    end(): void {},
    destroy(): void {
      destroyCount++;
    },
  });
  return {
    stream: fake as unknown as fs.WriteStream,
    writes,
    destroyed: () => destroyCount,
    emitter,
  };
}

// An async stream failure (disk full, EBUSY, permissions) must not surface as an unhandled
// 'error' event: it is reported once and file logging stops while the panel keeps working.
suite('GeneralFileLogChannel stream errors', () => {
  function createCountingInner(): {
    inner: vscode.LogOutputChannel;
    warnings: string[];
    infos: string[];
  } {
    const warnings: string[] = [];
    const infos: string[] = [];
    const { inner } = createFakeInner();
    const counting = inner as unknown as Record<string, unknown>;
    counting.warn = (m: string) => warnings.push(m);
    counting.info = (m: string) => infos.push(m);
    return { inner, warnings, infos };
  }

  test('registers an error listener so the event is not unhandled', () => {
    const { stream, emitter } = createFakeStream();
    new GeneralFileLogChannel('Reqnroll', stream, false, createFakeInner().inner);

    assert.strictEqual(emitter.listenerCount('error'), 1);
    assert.doesNotThrow(() => emitter.emit('error', new Error('ENOSPC')));
  });

  test('logs a single warning and drops later writes after an error', () => {
    const { stream, writes, destroyed, emitter } = createFakeStream();
    const { inner, warnings, infos } = createCountingInner();
    const channel = new GeneralFileLogChannel('Reqnroll', stream, false, inner);

    channel.info('before');
    emitter.emit('error', new Error('ENOSPC'));
    emitter.emit('error', new Error('ENOSPC again'));
    channel.info('after');
    channel.error('after too');

    assert.strictEqual(writes.length, 1, 'only the pre-error line reaches the file');
    assert.match(writes[0], /before/);
    assert.strictEqual(warnings.length, 1, 'the failure is reported exactly once');
    assert.match(warnings[0], /ENOSPC/);
    assert.strictEqual(destroyed(), 1);
    assert.deepStrictEqual(infos, ['before', 'after'], 'the panel keeps receiving entries');
  });

  test('dispose after an error does not throw', () => {
    const { stream, emitter } = createFakeStream();
    const channel = new GeneralFileLogChannel('Reqnroll', stream, false, createFakeInner().inner);

    emitter.emit('error', new Error('EBUSY'));

    assert.doesNotThrow(() => channel.dispose());
  });
});
