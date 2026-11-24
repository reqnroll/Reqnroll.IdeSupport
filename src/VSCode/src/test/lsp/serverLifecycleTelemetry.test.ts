import * as assert from 'assert';
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import { State } from 'vscode-languageclient';
import { LanguageClient, StateChangeEvent } from 'vscode-languageclient/node';
import {
  EXIT_DETAILS_TIMEOUT_MS,
  ServerLifecycleTelemetry,
} from '../../lsp/serverLifecycleTelemetry';
import { ServerProcessTracker } from '../../lsp/serverProcessTracker';

interface Sent {
  eventName: string;
  properties: Record<string, string | number>;
}

function setup(): { sut: ServerLifecycleTelemetry; sent: Sent[]; move: (to: State) => void } {
  let listener: ((e: StateChangeEvent) => void) | undefined;
  const client = {
    onDidChangeState: (l: (e: StateChangeEvent) => void) => {
      listener = l;
      return { dispose: () => undefined };
    },
  } as unknown as Pick<LanguageClient, 'onDidChangeState'>;
  const sent: Sent[] = [];
  const sut = new ServerLifecycleTelemetry(client, (eventName, properties) =>
    sent.push({ eventName, properties }),
  );
  let current = State.Stopped;
  return {
    sut,
    sent,
    move: (to: State) => {
      const oldState = current;
      current = to;
      listener?.({ oldState, newState: to });
    },
  };
}

suite('ServerLifecycleTelemetry', () => {
  test('a healthy start sends nothing', () => {
    const { sent, move } = setup();
    move(State.Starting);
    move(State.Running);
    assert.deepStrictEqual(sent, []);
  });

  test('stopping before ever running is a start failure on attempt 1', () => {
    const { sent, move } = setup();
    move(State.Starting);
    move(State.Stopped);
    assert.deepStrictEqual(sent, [
      {
        eventName: 'Server.Process.StartFailed',
        properties: { Reason: 'StartFailed', AttemptNumber: 1 },
      },
    ]);
  });

  test('a rejected start() after the Stopped transition does not report twice', () => {
    const { sut, sent, move } = setup();
    move(State.Starting);
    move(State.Stopped);
    sut.reportStartRejected();
    assert.strictEqual(sent.length, 1);
  });

  test('a rejected start() with no state transition reports one start failure', () => {
    const { sut, sent } = setup();
    sut.reportStartRejected();
    assert.deepStrictEqual(sent, [
      {
        eventName: 'Server.Process.StartFailed',
        properties: { Reason: 'StartFailed', AttemptNumber: 1 },
      },
    ]);
  });

  test('running then stopped is an unexpected exit, and the automatic restart is reported with its reason', () => {
    const { sent, move } = setup();
    move(State.Starting);
    move(State.Running);
    move(State.Stopped);
    move(State.Starting);
    move(State.Running);
    assert.deepStrictEqual(sent, [
      {
        eventName: 'Server.Process.ExitedUnexpectedly',
        properties: { Reason: 'ProcessExited', AttemptNumber: 1 },
      },
      {
        eventName: 'Server.Process.Restarted',
        properties: { Reason: 'ProcessExited', AttemptNumber: 2 },
      },
    ]);
  });

  test('a restart after a start failure carries the StartFailed reason and a higher attempt number', () => {
    const { sent, move } = setup();
    move(State.Starting);
    move(State.Stopped);
    move(State.Starting);
    move(State.Stopped);
    assert.deepStrictEqual(
      sent.map((s) => [s.eventName, s.properties.AttemptNumber]),
      [
        ['Server.Process.StartFailed', 1],
        ['Server.Process.Restarted', 2],
        ['Server.Process.StartFailed', 2],
      ],
    );
  });

  test('a rejection after the client reached Running is not a start failure', () => {
    const { sut, sent, move } = setup();
    move(State.Starting);
    move(State.Running);
    sut.reportStartRejected();
    assert.deepStrictEqual(sent, []);
  });

  test('a deliberate stop is not reported', () => {
    const { sut, sent, move } = setup();
    move(State.Starting);
    move(State.Running);
    sut.markIntentionalStop();
    move(State.Stopped);
    sut.reportStartRejected();
    assert.deepStrictEqual(sent, []);
  });

  test('event properties are only the closed Reason and AttemptNumber', () => {
    const { sent, move } = setup();
    move(State.Starting);
    move(State.Stopped);
    assert.deepStrictEqual(Object.keys(sent[0].properties).sort(), ['AttemptNumber', 'Reason']);
  });

  suite('with a ServerProcessTracker', () => {
    function setupTracked(): {
      sent: Sent[];
      move: (to: State) => void;
      args: string[];
      process: EventEmitter & { stderr: PassThrough };
    } {
      let listener: ((e: StateChangeEvent) => void) | undefined;
      const process = Object.assign(new EventEmitter(), { stderr: new PassThrough() });
      const client = {
        onDidChangeState: (l: (e: StateChangeEvent) => void) => {
          listener = l;
          return { dispose: () => undefined };
        },
        serverProcess: process,
      } as unknown as Pick<LanguageClient, 'onDidChangeState' | 'serverProcess'>;
      const args: string[] = [];
      let id = 0;
      const tracker = new ServerProcessTracker(args, Date.now, () => `id-${++id}`);
      const sent: Sent[] = [];
      new ServerLifecycleTelemetry(
        client,
        (eventName, properties) => sent.push({ eventName, properties }),
        tracker,
      );
      let current = State.Stopped;
      return {
        sent,
        args,
        process,
        move: (to: State) => {
          const oldState = current;
          current = to;
          listener?.({ oldState, newState: to });
        },
      };
    }

    test('the session id is in the server arguments before the client spawns the process', () => {
      const { args, move } = setupTracked();
      move(State.Starting);
      assert.deepStrictEqual(args, ['--server-session-id', 'id-1']);
    });

    test('a start failure carries the session id of that start', () => {
      const { sent, move } = setupTracked();
      move(State.Starting);
      move(State.Stopped);
      assert.deepStrictEqual(sent, [
        {
          eventName: 'Server.Process.StartFailed',
          properties: { ServerSessionId: 'id-1', Reason: 'StartFailed', AttemptNumber: 1 },
        },
      ]);
    });

    test('an unexpected exit is sent once the process has closed, with its exit details', async () => {
      const { sent, move, process } = setupTracked();
      move(State.Starting);
      move(State.Running);
      move(State.Stopped);
      assert.strictEqual(sent.length, 0, 'waits for the process to close');

      process.stderr.write('Stack overflow.\n   at Reqnroll.IdeSupport.X.Y.Recurse(Int32)\n');
      process.emit('exit', 0xc00000fd, null);
      process.stderr.end();
      process.emit('close', 0xc00000fd, null);
      await new Promise((resolve) => setTimeout(resolve, 20));

      assert.strictEqual(sent.length, 1);
      assert.strictEqual(sent[0].eventName, 'Server.Process.ExitedUnexpectedly');
      assert.strictEqual(sent[0].properties.Reason, 'ProcessExited');
      assert.strictEqual(sent[0].properties.AttemptNumber, 1);
      assert.strictEqual(sent[0].properties.ServerSessionId, 'id-1');
      assert.strictEqual(sent[0].properties.ExitCode, -1073741571);
      assert.strictEqual(sent[0].properties.CrashKind, 'StackOverflow');
      assert.strictEqual(sent[0].properties.StackFrames, 'Reqnroll.IdeSupport.X.Y.Recurse');
    });

    const flush = () => new Promise((resolve) => setTimeout(resolve, 20));

    test('a stop with a process that never ended is a lost connection, and the restart that came first follows it with the same reason', async function () {
      this.timeout(10000);
      const { sent, move } = setupTracked();
      move(State.Starting);
      move(State.Running);
      move(State.Stopped);
      move(State.Starting);
      assert.strictEqual(sent.length, 0, 'the restart is held until the stop is classified');

      await new Promise((resolve) => setTimeout(resolve, EXIT_DETAILS_TIMEOUT_MS + 250));

      assert.deepStrictEqual(
        sent.map((e) => e.eventName),
        ['Server.Process.ConnectionLost', 'Server.Process.Restarted'],
      );
      assert.strictEqual(sent[0].properties.Reason, 'ConnectionLost');
      assert.strictEqual(sent[0].properties.AttemptNumber, 1);
      assert.strictEqual(sent[0].properties.ServerSessionId, 'id-1');
      assert.strictEqual(typeof sent[0].properties.UptimeMs, 'number');
      assert.ok(!('ExitCode' in sent[0].properties));
      assert.deepStrictEqual(sent[1].properties, { Reason: 'ConnectionLost', AttemptNumber: 2 });
    });

    test('a restart that comes before the process exits waits, then follows the exit event with ProcessExited', async () => {
      const { sent, move, process } = setupTracked();
      move(State.Starting);
      move(State.Running);
      move(State.Stopped);
      move(State.Starting);
      assert.strictEqual(sent.length, 0, 'the restart is held until the stop is classified');

      process.emit('exit', 1, null);
      process.stderr.end();
      process.emit('close', 1, null);
      await flush();

      assert.deepStrictEqual(
        sent.map((e) => e.eventName),
        ['Server.Process.ExitedUnexpectedly', 'Server.Process.Restarted'],
      );
      assert.strictEqual(sent[0].properties.ExitCode, 1);
      assert.deepStrictEqual(sent[1].properties, { Reason: 'ProcessExited', AttemptNumber: 2 });
    });

    test('a stop with an exit code and no crash report is an unexpected exit', async () => {
      const { sent, move, process } = setupTracked();
      move(State.Starting);
      move(State.Running);
      move(State.Stopped);
      process.emit('exit', 1, null);
      process.stderr.end();
      process.emit('close', 1, null);
      await flush();

      assert.deepStrictEqual(
        sent.map((e) => e.eventName),
        ['Server.Process.ExitedUnexpectedly'],
      );
      assert.strictEqual(sent[0].properties.ExitCode, 1);
      assert.strictEqual(sent[0].properties.Reason, 'ProcessExited');
    });

    test('a stop with a crash report but no exit code is still an unexpected exit', async () => {
      const { sent, move, process } = setupTracked();
      move(State.Starting);
      move(State.Running);
      move(State.Stopped);
      process.stderr.write('Stack overflow.\n   at Reqnroll.IdeSupport.X.Y.Recurse(Int32)\n');
      process.stderr.end();
      process.emit('close', null, null);
      await flush();

      assert.deepStrictEqual(
        sent.map((e) => e.eventName),
        ['Server.Process.ExitedUnexpectedly'],
      );
      assert.strictEqual(sent[0].properties.CrashKind, 'StackOverflow');
      assert.ok(!('ExitCode' in sent[0].properties));
    });

    test('a restart after the exit details are known carries ProcessExited', async () => {
      const { sent, move, process } = setupTracked();
      move(State.Starting);
      move(State.Running);
      move(State.Stopped);
      process.emit('exit', 134, null);
      process.emit('close', 134, null);
      await flush();
      move(State.Starting);
      await flush();

      assert.deepStrictEqual(
        sent.map((e) => e.eventName),
        ['Server.Process.ExitedUnexpectedly', 'Server.Process.Restarted'],
      );
      assert.strictEqual(sent[sent.length - 1].eventName, 'Server.Process.Restarted');
      assert.strictEqual(sent[sent.length - 1].properties.Reason, 'ProcessExited');
    });
  });
});
