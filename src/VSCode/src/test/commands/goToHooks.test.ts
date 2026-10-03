import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { TelemetryReporter } from '@vscode/extension-telemetry';
import { doGoToHooks, sourceForArgs } from '../../commands/goToHooks';
import { registerTelemetry } from '../../telemetry';
import { GoToHookSource, TelemetryProperties } from '../../telemetryEvents';

/** Minimal stand-in for LanguageClient's sendRequest surface used by doGoToHooks. */
function fakeClient(sendRequest: () => Promise<unknown>): LanguageClient {
  return { sendRequest } as unknown as LanguageClient;
}

/** Stand-in that also captures the request payload passed to sendRequest. */
function capturingClient(
  response: unknown,
  onRequest: (method: unknown, params: unknown) => void,
): LanguageClient {
  return {
    sendRequest: (method: unknown, params: unknown) => {
      onRequest(method, params);
      return Promise.resolve(response);
    },
  } as unknown as LanguageClient;
}

/** Stubs a vscode.window prompt function for the duration of `fn`, restoring it afterwards. */
async function withStubbedWindow<T>(
  overrides: Partial<{
    showErrorMessage: typeof vscode.window.showErrorMessage;
    showInformationMessage: typeof vscode.window.showInformationMessage;
    showQuickPick: typeof vscode.window.showQuickPick;
  }>,
  fn: () => Promise<T>,
): Promise<T> {
  const originals = { ...overrides };
  for (const key of Object.keys(overrides) as (keyof typeof overrides)[]) {
    originals[key] = vscode.window[key] as never;
    (vscode.window as unknown as Record<string, unknown>)[key] = overrides[key];
  }
  try {
    return await fn();
  } finally {
    for (const key of Object.keys(overrides) as (keyof typeof overrides)[]) {
      (vscode.window as unknown as Record<string, unknown>)[key] = originals[key];
    }
  }
}

suite('goToHooks', () => {
  suite('doGoToHooks', () => {
    let editor: vscode.TextEditor;

    suiteSetup(async () => {
      const doc = await vscode.workspace.openTextDocument({
        content: 'irrelevant',
        language: 'plaintext',
      });
      editor = await vscode.window.showTextDocument(doc);
    });

    test('shows an error message when the request throws', async () => {
      const client = fakeClient(() => Promise.reject(new Error('boom')));
      let shownMessage: string | undefined;

      await withStubbedWindow(
        {
          showErrorMessage: (msg: string) => {
            shownMessage = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doGoToHooks(client, undefined, GoToHookSource.command),
      );

      assert.match(shownMessage ?? '', /Go to Hooks failed.*boom/);
    });

    test('shows an info message when no hooks are found', async () => {
      const client = fakeClient(() => Promise.resolve({ hooks: [] }));
      let shownMessage: string | undefined;

      await withStubbedWindow(
        {
          showInformationMessage: (msg: string) => {
            shownMessage = msg;
            return Promise.resolve(undefined);
          },
        },
        () => doGoToHooks(client, undefined, GoToHookSource.command),
      );

      assert.match(shownMessage ?? '', /No hooks found/);
    });

    test('shows a QuickPick with an order detail only when hookOrder is non-zero', async () => {
      const client = fakeClient(() =>
        Promise.resolve({
          hooks: [
            {
              uri: editor.document.uri.toString(),
              startLine: 0,
              startChar: 0,
              hookType: 'BeforeScenario',
              hookOrder: 5,
              methodName: 'Setup',
            },
            {
              uri: editor.document.uri.toString(),
              startLine: 1,
              startChar: 0,
              hookType: 'AfterScenario',
              hookOrder: 0,
              methodName: 'Teardown',
            },
          ],
        }),
      );
      let quickPickItems: readonly { detail?: string; description?: string }[] | undefined;

      await withStubbedWindow(
        {
          showQuickPick: ((items: readonly { detail?: string; description?: string }[]) => {
            quickPickItems = items;
            return Promise.resolve(undefined);
          }) as unknown as typeof vscode.window.showQuickPick,
        },
        () => doGoToHooks(client, undefined, GoToHookSource.command),
      );

      assert.strictEqual(quickPickItems?.length, 2);
      assert.strictEqual(quickPickItems[0].detail, 'Order: 5');
      assert.strictEqual(quickPickItems[1].detail, undefined);
    });

    test('navigates directly for a single hook when alwaysShowPicker is not set (manual invocation)', async () => {
      const client = fakeClient(() =>
        Promise.resolve({
          hooks: [
            {
              uri: editor.document.uri.toString(),
              startLine: 0,
              startChar: 0,
              hookType: 'BeforeScenario',
              hookOrder: 0,
              methodName: 'Setup',
            },
          ],
        }),
      );
      let quickPickShown = false;

      await withStubbedWindow(
        {
          showQuickPick: () => {
            quickPickShown = true;
            return Promise.resolve(undefined);
          },
        },
        () =>
          doGoToHooks(
            client,
            { uri: editor.document.uri.toString(), line: 0, character: 0 },
            GoToHookSource.command,
          ),
      );

      assert.strictEqual(quickPickShown, false);
    });

    test('shows the QuickPick for a single hook when alwaysShowPicker is set (CodeLens click)', async () => {
      const client = fakeClient(() =>
        Promise.resolve({
          hooks: [
            {
              uri: editor.document.uri.toString(),
              startLine: 0,
              startChar: 0,
              hookType: 'BeforeScenario',
              hookOrder: 0,
              methodName: 'Setup',
            },
          ],
        }),
      );
      let quickPickPlaceholder: string | undefined;

      await withStubbedWindow(
        {
          showQuickPick: (_items: unknown, options?: { placeHolder?: string }) => {
            quickPickPlaceholder = options?.placeHolder;
            return Promise.resolve(undefined);
          },
        },
        () =>
          doGoToHooks(
            client,
            {
              uri: editor.document.uri.toString(),
              line: 0,
              character: 0,
              alwaysShowPicker: true,
            },
            GoToHookSource.codeLens,
          ),
      );

      assert.match(quickPickPlaceholder ?? '', /^1 hook found/);
    });

    test('sends ownLevelOnly=false by default (manual invocation)', async () => {
      let sentParams: unknown;
      const client = capturingClient({ hooks: [] }, (_method, params) => {
        sentParams = params;
      });

      await doGoToHooks(
        client,
        { uri: editor.document.uri.toString(), line: 0, character: 0 },
        GoToHookSource.command,
      );

      assert.strictEqual((sentParams as { ownLevelOnly?: boolean })?.ownLevelOnly, false);
    });

    test('forwards ownLevelOnly=true when passed by a CodeLens click', async () => {
      let sentParams: unknown;
      const client = capturingClient({ hooks: [] }, (_method, params) => {
        sentParams = params;
      });

      await doGoToHooks(
        client,
        { uri: editor.document.uri.toString(), line: 0, character: 0, ownLevelOnly: true },
        GoToHookSource.codeLens,
      );

      assert.strictEqual((sentParams as { ownLevelOnly?: boolean })?.ownLevelOnly, true);
    });

    /** Runs `invoke` with the reporter stubbed and returns every [eventName, properties] sent. */
    async function captureTelemetry(
      invoke: (client: LanguageClient) => Promise<void>,
    ): Promise<Array<[string, Record<string, string> | undefined]>> {
      const client = {
        sendRequest: () => Promise.resolve({ hooks: [] }),
        onNotification: () => ({ dispose: () => undefined }),
      } as unknown as LanguageClient;
      const telemetryContext = { subscriptions: [] } as unknown as vscode.ExtensionContext;

      const proto = TelemetryReporter.prototype as unknown as {
        sendTelemetryEvent: (eventName: string, properties?: Record<string, string>) => void;
      };
      const original = proto.sendTelemetryEvent;
      const sent: Array<[string, Record<string, string> | undefined]> = [];
      proto.sendTelemetryEvent = (eventName: string, properties?: Record<string, string>) => {
        sent.push([eventName, properties]);
      };

      try {
        registerTelemetry(client, telemetryContext);
        await invoke(client);
        return sent;
      } finally {
        proto.sendTelemetryEvent = original;
        for (const sub of telemetryContext.subscriptions) sub.dispose();
      }
    }

    test('sends "GoToHook command executed" telemetry directly on a genuine invocation (issue #698)', async () => {
      const sent = await captureTelemetry((client) =>
        doGoToHooks(
          client,
          { uri: editor.document.uri.toString(), line: 0, character: 0 },
          GoToHookSource.command,
        ),
      );

      assert.deepStrictEqual(
        sent.map(([name]) => name),
        ['GoToHook command executed'],
      );
    });

    // Issue #861: Source is derived by sourceForArgs from the real command arguments and is
    // transmitted as the closed enum shared with VS and Rider.
    const sourceCases: Array<[string, () => unknown[], string]> = [
      ['palette/keybinding (no arguments)', () => [], 'Command'],
      ['editor context menu (document Uri argument)', () => [editor.document.uri], 'ContextMenu'],
      [
        'CodeLens click (uri, line, char, ownLevelOnly)',
        () => ['file:///a.feature', 3, 0, true],
        'CodeLens',
      ],
    ];
    for (const [name, makeArgs, expected] of sourceCases) {
      test(`transmits Source=${expected} for ${name}`, async () => {
        const args = makeArgs();
        const sent = await captureTelemetry((client) =>
          doGoToHooks(client, undefined, sourceForArgs(args)),
        );

        assert.strictEqual(sent[0][1]?.Source, expected);
      });
    }
  });

  suite('sourceForArgs', () => {
    test('a real vscode.Uri (editor/context argument) is ContextMenu', () => {
      assert.strictEqual(sourceForArgs([vscode.Uri.file('/w/a.feature')]), 'ContextMenu');
    });

    test('a string and a number (CodeLens arguments) is CodeLens', () => {
      assert.strictEqual(sourceForArgs(['file:///w/a.feature', 4]), 'CodeLens');
      assert.strictEqual(sourceForArgs(['file:///w/a.feature', 4, 2, false]), 'CodeLens');
    });

    test('a lone string, other values or no arguments is Command', () => {
      assert.strictEqual(sourceForArgs(['file:///w/a.feature']), 'Command');
      assert.strictEqual(sourceForArgs([42]), 'Command');
      assert.strictEqual(sourceForArgs([{}]), 'Command');
      assert.strictEqual(sourceForArgs([]), 'Command');
    });

    test('the closed enum and property key match the other IDEs', () => {
      assert.deepStrictEqual(Object.values(GoToHookSource), ['Command', 'ContextMenu', 'CodeLens']);
      assert.strictEqual(TelemetryProperties.source, 'Source');
    });
  });
});
