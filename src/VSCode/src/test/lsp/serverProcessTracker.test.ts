import * as assert from 'assert';
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import type { ChildProcess } from 'child_process';
import { ServerProcessTracker } from '../../lsp/serverProcessTracker';

class FakeProcess extends EventEmitter {
  readonly stderr = new PassThrough();

  die(code: number | null, signal: NodeJS.Signals | null, stderrLines: string[] = []): void {
    for (const line of stderrLines) this.stderr.write(`${line}\n`);
    this.emit('exit', code, signal);
    this.stderr.end();
    // Node raises 'close' after the stdio streams have ended.
    setImmediate(() => this.emit('close', code, signal));
  }
}

function setup(): { tracker: ServerProcessTracker; args: string[]; tick: (ms: number) => void } {
  let now = 1_000;
  let id = 0;
  const args = ['--ide', 'vscode'];
  const tracker = new ServerProcessTracker(
    args,
    () => now,
    () => `id-${++id}`,
  );
  return { tracker, args, tick: (ms) => (now += ms) };
}

suite('ServerProcessTracker', () => {
  test('each start puts a fresh --server-session-id into the server arguments', () => {
    const { tracker, args } = setup();

    tracker.onStarting();
    assert.deepStrictEqual(args, ['--ide', 'vscode', '--server-session-id', 'id-1']);

    tracker.onStarting();
    assert.deepStrictEqual(args, ['--ide', 'vscode', '--server-session-id', 'id-2']);
    assert.strictEqual(tracker.sessionId, 'id-2');
    assert.deepStrictEqual(tracker.startDetails(), { ServerSessionId: 'id-2' });
  });

  test('an exit reports the signed exit code, uptime and the parsed crash report', async () => {
    const { tracker, tick } = setup();
    const process = new FakeProcess();
    tracker.onStarting();
    tracker.onRunning(process as unknown as ChildProcess);
    tick(1500);

    // Windows reports 0xE0434352 unsigned; Visual Studio sends it as a signed 32-bit value.
    process.die(0xe0434352, null, [
      'Unhandled exception. System.InvalidOperationException: boom',
      '   at Reqnroll.IdeSupport.X.Y.Run() in C:\\x.cs:line 7',
    ]);
    const details = await tracker.describeExit(2000);

    assert.deepStrictEqual(details, {
      ServerSessionId: 'id-1',
      ExitCode: -532462766,
      UptimeMs: 1500,
      CrashKind: 'UnhandledException',
      ExceptionType: 'System.InvalidOperationException',
      Message: 'boom',
      StackFrames: 'Reqnroll.IdeSupport.X.Y.Run:7',
    });
  });

  test('a POSIX signal is reported as ExitSignal', async () => {
    const { tracker } = setup();
    const process = new FakeProcess();
    tracker.onStarting();
    tracker.onRunning(process as unknown as ChildProcess);

    process.die(null, 'SIGABRT');
    const details = await tracker.describeExit(2000);

    assert.strictEqual(details.ExitSignal, 'SIGABRT');
    assert.strictEqual(details.ExitCode, undefined);
  });

  test('a process that never finishes closing is reported with what is known after the timeout', async () => {
    const { tracker } = setup();
    tracker.onStarting();
    tracker.onRunning(new FakeProcess() as unknown as ChildProcess);

    const details = await tracker.describeExit(10);

    assert.strictEqual(details.ServerSessionId, 'id-1');
    assert.strictEqual(details.ExitCode, undefined);
  });

  test('before any start there is nothing to describe', async () => {
    const { tracker } = setup();
    assert.deepStrictEqual(await tracker.describeExit(10), {});
    assert.deepStrictEqual(tracker.startDetails(), {});
  });
});
