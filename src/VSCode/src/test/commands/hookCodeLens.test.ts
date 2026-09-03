import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { registerHookCodeLens } from '../../commands/hookCodeLens';

/** Minimal stand-in for LanguageClient's sendRequest/onRequest surface used by registerHookCodeLens. */
function fakeClient(overrides: {
  sendRequest?: (method: string, params: unknown) => Promise<unknown>;
  onRequest?: (...args: unknown[]) => { dispose: () => void };
}): LanguageClient {
  return {
    sendRequest: overrides.sendRequest ?? (() => Promise.resolve(null)),
    onRequest: overrides.onRequest ?? (() => ({ dispose: () => undefined })),
  } as unknown as LanguageClient;
}

/** Minimal stand-in for vscode.ExtensionContext's subscriptions surface. */
function fakeContext(): vscode.ExtensionContext {
  return { subscriptions: [] } as unknown as vscode.ExtensionContext;
}

/**
 * Stubs `console.warn` for the duration of `fn`, restoring the original descriptor afterwards.
 * See `stepCodeLens.test.ts`'s identical helper: the extension host installs `console.warn` as a
 * `configurable: true` accessor, so a bare assignment is a silent no-op and this must go through
 * `Object.defineProperty` instead.
 */
async function withStubbedConsoleWarn<T>(
  stub: (...args: unknown[]) => void,
  fn: () => Thenable<T>,
): Promise<T> {
  const original = Object.getOwnPropertyDescriptor(console, 'warn')!;
  Object.defineProperty(console, 'warn', {
    value: stub,
    writable: true,
    configurable: true,
    enumerable: true,
  });
  try {
    return await fn();
  } finally {
    Object.defineProperty(console, 'warn', original);
  }
}

/**
 * Registers the CodeLens provider with `client`/`context`, capturing the `vscode.CodeLensProvider`
 * and selector passed to `vscode.languages.registerCodeLensProvider` so behavior can be verified
 * directly, without needing a real editor/CodeLens-refresh pipeline.
 */
function captureProvider(client: LanguageClient): {
  provider: vscode.CodeLensProvider;
  selector: unknown;
} {
  const original = vscode.languages.registerCodeLensProvider;
  let captured: vscode.CodeLensProvider | undefined;
  let capturedSelector: unknown;
  (vscode.languages as unknown as { registerCodeLensProvider: unknown }).registerCodeLensProvider =
    (selector: unknown, provider: vscode.CodeLensProvider) => {
      capturedSelector = selector;
      captured = provider;
      return { dispose: () => undefined };
    };

  try {
    registerHookCodeLens(client, fakeContext());
  } finally {
    (
      vscode.languages as unknown as { registerCodeLensProvider: unknown }
    ).registerCodeLensProvider = original;
  }

  assert.ok(captured, 'registerCodeLensProvider should have been called');
  return { provider: captured, selector: capturedSelector };
}

suite('hookCodeLens', () => {
  suite('registerHookCodeLens', () => {
    test('registers the provider for gherkin documents', () => {
      const { selector } = captureProvider(fakeClient({}));

      assert.deepStrictEqual(selector, { language: 'gherkin' });
    });
  });

  suite('provideCodeLenses', () => {
    test('maps the server response into vscode.CodeLens instances', async () => {
      const client = fakeClient({
        sendRequest: () =>
          Promise.resolve([
            {
              range: { start: { line: 0, character: 0 }, end: { line: 0, character: 5 } },
              command: {
                title: '2 hooks',
                command: 'reqnroll.goToHooks',
                arguments: ['file:///Steps.feature', 0, 0],
              },
            },
          ]),
      });
      const { provider } = captureProvider(client);

      const document = { uri: vscode.Uri.parse('file:///Steps.feature') } as vscode.TextDocument;
      const lenses = await provider.provideCodeLenses(document, {} as vscode.CancellationToken);

      assert.strictEqual(lenses?.length, 1);
      assert.strictEqual(lenses[0].command?.title, '2 hooks');
      assert.strictEqual(lenses[0].command?.command, 'reqnroll.goToHooks');
      assert.deepStrictEqual(lenses[0].command?.arguments, ['file:///Steps.feature', 0, 0]);
    });

    test('returns an empty array when the server responds with no lenses', async () => {
      const client = fakeClient({ sendRequest: () => Promise.resolve(null) });
      const { provider } = captureProvider(client);

      const document = { uri: vscode.Uri.parse('file:///Steps.feature') } as vscode.TextDocument;
      const lenses = await provider.provideCodeLenses(document, {} as vscode.CancellationToken);

      assert.deepStrictEqual(lenses, []);
    });

    test('swallows a request failure into an empty array and logs via console.warn, without showing a user-facing error', async () => {
      const client = fakeClient({
        sendRequest: () => Promise.reject(new Error('server unavailable')),
      });
      const { provider } = captureProvider(client);

      let warned = false;
      const originalShowErrorMessage = vscode.window.showErrorMessage;
      let errorShown = false;
      (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage = () => {
        errorShown = true;
        return Promise.resolve(undefined);
      };

      try {
        const document = { uri: vscode.Uri.parse('file:///Steps.feature') } as vscode.TextDocument;
        const lenses = await withStubbedConsoleWarn(
          () => {
            warned = true;
          },
          () =>
            Promise.resolve(provider.provideCodeLenses(document, {} as vscode.CancellationToken)),
        );

        assert.deepStrictEqual(lenses, []);
        assert.ok(warned, 'console.warn should be called on request failure');
        assert.strictEqual(
          errorShown,
          false,
          'showErrorMessage should NOT be called -- CodeLens failures degrade silently by design',
        );
      } finally {
        (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage =
          originalShowErrorMessage;
      }
    });
  });
});
