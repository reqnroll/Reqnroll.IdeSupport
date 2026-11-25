import * as assert from 'assert';
import {
  isAlwaysAllowed,
  MAX_HELD_EVENTS,
  registerReqnrollProjectDetected,
  ReqnrollProjectGate,
} from '../reqnrollProjectGate';

suite('ReqnrollProjectGate', () => {
  let gate: ReqnrollProjectGate;
  let sent: string[];
  const send = (eventName: string) => gate.send(eventName, () => sent.push(eventName));

  setup(() => {
    gate = new ReqnrollProjectGate();
    sent = [];
  });

  test('holds ordinary events until a Reqnroll project is reported', () => {
    send('Lifecycle.Extension.Loaded');
    send('Server.Session.Started');

    assert.deepStrictEqual(sent, []);
    assert.strictEqual(gate.isOpen, false);
  });

  test('opening sends the held events in order, then later events directly', () => {
    send('Lifecycle.Extension.Loaded');
    send('Server.Session.Started');

    gate.open();
    send('Project.Opened');

    assert.deepStrictEqual(sent, [
      'Lifecycle.Extension.Loaded',
      'Server.Session.Started',
      'Project.Opened',
    ]);
    assert.strictEqual(gate.isOpen, true);
  });

  test('opening twice sends the held events once', () => {
    send('Lifecycle.Extension.Loaded');

    gate.open();
    gate.open();

    assert.strictEqual(sent.length, 1);
  });

  for (const eventName of [
    'Error.UnhandledException',
    'Lifecycle.Extension.Installed',
    'Lifecycle.Extension.Upgraded',
    'Server.Process.StartFailed',
    'Server.Process.ExitedUnexpectedly',
    'Server.Process.ConnectionLost',
  ]) {
    test(`never holds ${eventName}`, () => {
      send(eventName);
      assert.deepStrictEqual(sent, [eventName]);
    });
  }

  test('holds a restart, loaded and the usage heartbeat like any other event', () => {
    assert.strictEqual(isAlwaysAllowed('Server.Process.Restarted'), false);
    assert.strictEqual(isAlwaysAllowed('Lifecycle.Extension.Loaded'), false);
    assert.strictEqual(isAlwaysAllowed('Lifecycle.Usage.Recorded'), false);
  });

  test('holds at most the cap and drops the rest', () => {
    for (let i = 0; i < MAX_HELD_EVENTS + 5; i++) send('Perf.Sample');

    gate.open();

    assert.strictEqual(sent.length, MAX_HELD_EVENTS);
  });

  test('a failing held send does not stop the others', () => {
    gate.send('Lifecycle.Extension.Loaded', () => {
      throw new Error('boom');
    });
    send('Server.Session.Started');

    assert.doesNotThrow(() => gate.open());
    assert.deepStrictEqual(sent, ['Server.Session.Started']);
  });

  test('onOpened runs once on open, or at once when already open', () => {
    let calls = 0;
    gate.onOpened(() => calls++);
    assert.strictEqual(calls, 0);

    gate.open();
    gate.open();
    assert.strictEqual(calls, 1);

    gate.onOpened(() => calls++);
    assert.strictEqual(calls, 2);
  });

  test('onOpened listeners run after the held events are sent', () => {
    send('Server.Session.Started');
    gate.onOpened(() => send('Lifecycle.Extension.Loaded'));

    gate.open();

    assert.deepStrictEqual(sent, ['Server.Session.Started', 'Lifecycle.Extension.Loaded']);
  });

  test('registerReqnrollProjectDetected opens the gate on the server notification', () => {
    let method: string | undefined;
    let handler: ((params: unknown) => void) | undefined;
    const client = {
      onNotification: (m: string, h: (params: unknown) => void) => {
        method = m;
        handler = h;
        return { dispose: () => undefined };
      },
    };

    registerReqnrollProjectDetected(client, gate);
    assert.strictEqual(method, 'reqnroll/reqnrollProjectDetected');
    assert.strictEqual(gate.isOpen, false);

    handler?.(undefined);
    assert.strictEqual(gate.isOpen, true);
  });
});
