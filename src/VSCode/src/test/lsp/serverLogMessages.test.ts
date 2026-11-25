import * as assert from 'assert';
import * as vscode from 'vscode';
import {
  NotificationSource,
  registerServerLogMessages,
  ServerLogMessageChannel,
  writeServerLogMessage,
} from '../../lsp/serverLogMessages';

function fakeChannel(): { channel: ServerLogMessageChannel; entries: string[] } {
  const entries: string[] = [];
  const channel = {
    error: (m: string) => entries.push(`error:${m}`),
    warn: (m: string) => entries.push(`warn:${m}`),
    info: (m: string) => entries.push(`info:${m}`),
    debug: (m: string) => entries.push(`debug:${m}`),
  } as unknown as ServerLogMessageChannel;
  return { channel, entries };
}

suite('serverLogMessages', () => {
  test('writes each message type at its own level', () => {
    const { channel, entries } = fakeChannel();

    writeServerLogMessage(channel, { type: 1, message: 'e' });
    writeServerLogMessage(channel, { type: 2, message: 'w' });
    writeServerLogMessage(channel, { type: 3, message: 'i' });
    writeServerLogMessage(channel, { type: 4, message: 'l' });

    assert.deepStrictEqual(entries, ['error:e', 'warn:w', 'info:i', 'debug:l']);
  });

  test('ignores a message without text', () => {
    const { channel, entries } = fakeChannel();

    writeServerLogMessage(channel, { type: 2 });
    writeServerLogMessage(channel, undefined);

    assert.deepStrictEqual(entries, []);
  });

  test('subscribes to reqnroll/logMessage and writes what arrives', () => {
    const { channel, entries } = fakeChannel();
    let subscribed: string | undefined;
    let handler: ((params: unknown) => void) | undefined;
    const client: NotificationSource = {
      onNotification: (method, h) => {
        subscribed = method;
        handler = h;
        return new vscode.Disposable(() => undefined);
      },
    };

    registerServerLogMessages(client, channel);
    handler?.({ type: 2, message: '[Sample] Binding discovery from the compiled assembly failed' });

    assert.strictEqual(subscribed, 'reqnroll/logMessage');
    assert.deepStrictEqual(entries, [
      'warn:[Sample] Binding discovery from the compiled assembly failed',
    ]);
  });
});
