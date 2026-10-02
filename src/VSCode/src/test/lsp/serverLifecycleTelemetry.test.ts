import * as assert from 'assert';
import { State } from 'vscode-languageclient';
import { LanguageClient, StateChangeEvent } from 'vscode-languageclient/node';
import { ServerLifecycleTelemetry } from '../../lsp/serverLifecycleTelemetry';

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
      { eventName: 'ServerStartFailed', properties: { Reason: 'StartFailed', AttemptNumber: 1 } },
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
      { eventName: 'ServerStartFailed', properties: { Reason: 'StartFailed', AttemptNumber: 1 } },
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
        eventName: 'ServerExitedUnexpectedly',
        properties: { Reason: 'ProcessExited', AttemptNumber: 1 },
      },
      { eventName: 'ServerRestarted', properties: { Reason: 'ProcessExited', AttemptNumber: 2 } },
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
        ['ServerStartFailed', 1],
        ['ServerRestarted', 2],
        ['ServerStartFailed', 2],
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
});
