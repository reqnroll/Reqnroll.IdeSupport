import * as assert from 'assert';
import * as vscode from 'vscode';
import {
  ClipboardLog,
  ClipboardTarget,
  copyServerTextToClipboard,
  registerServerClipboard,
} from '../../lsp/serverClipboard';
import { NotificationSource } from '../../lsp/serverLogMessages';

function fakeClipboard(fail = false): { clipboard: ClipboardTarget; written: string[] } {
  const written: string[] = [];
  const clipboard: ClipboardTarget = {
    writeText: (value: string) => {
      if (fail) return Promise.reject(new Error('clipboard unavailable'));
      written.push(value);
      return Promise.resolve();
    },
  };
  return { clipboard, written };
}

function fakeLog(): { log: ClipboardLog; warnings: string[] } {
  const warnings: string[] = [];
  const log = { warn: (message: string) => warnings.push(message) } as unknown as ClipboardLog;
  return { log, warnings };
}

suite('serverClipboard', () => {
  const snippet = '    [When("I press add")]\n    public void WhenIPressAdd()\n    {\n    }\n';

  test('writes the text to the clipboard', async () => {
    const { clipboard, written } = fakeClipboard();
    await copyServerTextToClipboard(clipboard, fakeLog().log, { text: snippet });
    assert.deepStrictEqual(written, [snippet]);
  });

  test('ignores a notification without usable text', async () => {
    const { clipboard, written } = fakeClipboard();
    const { log } = fakeLog();
    await copyServerTextToClipboard(clipboard, log, undefined);
    await copyServerTextToClipboard(clipboard, log, {});
    await copyServerTextToClipboard(clipboard, log, { text: '' });
    await copyServerTextToClipboard(clipboard, log, { text: 42 });
    assert.deepStrictEqual(written, []);
  });

  test('reports a failed write instead of throwing', async () => {
    const { clipboard } = fakeClipboard(true);
    const { log, warnings } = fakeLog();
    await copyServerTextToClipboard(clipboard, log, { text: snippet });
    assert.strictEqual(warnings.length, 1);
    assert.ok(warnings[0].includes('clipboard unavailable'));
  });

  test('subscribes to reqnroll/copyToClipboard and copies what arrives', async () => {
    const { clipboard, written } = fakeClipboard();
    let subscribed: string | undefined;
    let handler: ((params: unknown) => void) | undefined;
    const client: NotificationSource = {
      onNotification: (method, h) => {
        subscribed = method;
        handler = h;
        return new vscode.Disposable(() => undefined);
      },
    };

    registerServerClipboard(client, clipboard, fakeLog().log);
    handler?.({ text: snippet });
    await Promise.resolve();

    assert.strictEqual(subscribed, 'reqnroll/copyToClipboard');
    assert.deepStrictEqual(written, [snippet]);
  });
});
