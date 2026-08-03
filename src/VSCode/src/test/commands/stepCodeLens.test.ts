import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { registerStepCodeLens } from '../../commands/stepCodeLens';

/** Minimal stand-in for LanguageClient's sendRequest/onRequest surface used by registerStepCodeLens. */
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
 * A plain `console.warn = fn` assignment is a silent no-op here: the VS Code extension host
 * installs `console.warn` as a `configurable: true` *accessor* (get/set) whose getter always
 * returns its own patched function regardless of what's been "set" — confirmed by inspecting
 * `Object.getOwnPropertyDescriptor(console, 'warn')` inside this suite before this fix. Since the
 * property is configurable, `Object.defineProperty` can still fully replace it with a plain
 * writable data property, unlike a bare assignment.
 */
async function withStubbedConsoleWarn<T>(stub: (...args: unknown[]) => void, fn: () => Thenable<T>): Promise<T> {
  const original = Object.getOwnPropertyDescriptor(console, 'warn')!;
  Object.defineProperty(console, 'warn', { value: stub, writable: true, configurable: true, enumerable: true });
  try {
    return await fn();
  } finally {
    Object.defineProperty(console, 'warn', original);
  }
}

/**
 * Registers the CodeLens provider with `client`/`context`, capturing the `vscode.CodeLensProvider`
 * passed to `vscode.languages.registerCodeLensProvider` so its `provideCodeLenses` can be invoked
 * directly, without needing a real editor/CodeLens-refresh pipeline.
 */
function captureProvider(client: LanguageClient): vscode.CodeLensProvider {
  const original = vscode.languages.registerCodeLensProvider;
  let captured: vscode.CodeLensProvider | undefined;
  (vscode.languages as unknown as { registerCodeLensProvider: unknown }).registerCodeLensProvider =
    (_selector: unknown, provider: vscode.CodeLensProvider) => {
      captured = provider;
      return { dispose: () => undefined };
    };

  try {
    registerStepCodeLens(client, fakeContext());
  } finally {
    (
      vscode.languages as unknown as { registerCodeLensProvider: unknown }
    ).registerCodeLensProvider = original;
  }

  assert.ok(captured, 'registerCodeLensProvider should have been called');
  return captured;
}

suite('stepCodeLens', () => {
  suite('provideCodeLenses', () => {
    test('maps the server response into vscode.CodeLens instances', async () => {
      const client = fakeClient({
        sendRequest: () =>
          Promise.resolve([
            {
              range: { start: { line: 0, character: 0 }, end: { line: 0, character: 5 } },
              command: { title: '2 usages', command: 'reqnroll.findStepUsages', arguments: [1, 2] },
            },
          ]),
      });
      const provider = captureProvider(client);

      const document = { uri: vscode.Uri.parse('file:///Steps.cs') } as vscode.TextDocument;
      const lenses = await provider.provideCodeLenses(document, {} as vscode.CancellationToken);

      assert.strictEqual(lenses?.length, 1);
      assert.strictEqual(lenses[0].command?.title, '2 usages');
      assert.strictEqual(lenses[0].command?.command, 'reqnroll.findStepUsages');
      assert.deepStrictEqual(lenses[0].command?.arguments, [1, 2]);
    });

    test('returns an empty array when the server responds with no lenses', async () => {
      const client = fakeClient({ sendRequest: () => Promise.resolve(null) });
      const provider = captureProvider(client);

      const document = { uri: vscode.Uri.parse('file:///Steps.cs') } as vscode.TextDocument;
      const lenses = await provider.provideCodeLenses(document, {} as vscode.CancellationToken);

      assert.deepStrictEqual(lenses, []);
    });

    test(
      'swallows a request failure into an empty array and logs via console.warn, without ' +
        'showing a user-facing error — CodeLens providers fire on every visible-range change, so ' +
        'popping an error dialog on each transient failure would be noisy; this documents that as ' +
        'a deliberate, tested choice rather than an untested gap (contrast with one-shot commands ' +
        'like Find Step Usages, which do show an error)',
      async () => {
        const client = fakeClient({
          sendRequest: () => Promise.reject(new Error('server unavailable')),
        });
        const provider = captureProvider(client);

        let warned = false;
        const originalShowErrorMessage = vscode.window.showErrorMessage;
        let errorShown = false;
        (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage = () => {
          errorShown = true;
          return Promise.resolve(undefined);
        };

        try {
          const document = { uri: vscode.Uri.parse('file:///Steps.cs') } as vscode.TextDocument;
          const lenses = await withStubbedConsoleWarn(
            () => {
              warned = true;
            },
            () => Promise.resolve(provider.provideCodeLenses(document, {} as vscode.CancellationToken)),
          );

          assert.deepStrictEqual(lenses, []);
          assert.ok(warned, 'console.warn should be called on request failure');
          assert.strictEqual(
            errorShown,
            false,
            'showErrorMessage should NOT be called — CodeLens failures degrade silently by design',
          );
        } finally {
          (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage =
            originalShowErrorMessage;
        }
      },
    );
  });
});
