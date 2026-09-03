import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import type { getCodeLensRefreshEvent as GetCodeLensRefreshEvent } from '../../commands/codeLensRefresh';

const MODULE_PATH = require.resolve('../../commands/codeLensRefresh');

/**
 * Loads a fresh instance of `codeLensRefresh.ts`'s compiled module, bypassing Node's `require`
 * cache. `getCodeLensRefreshEvent` guards a module-level singleton (`sharedEmitter`) that only
 * its first-ever caller in a given module instance actually wires up an `onRequest` handler for
 * -- and this extension's `workspaceContains` activation event (any .feature file anywhere in the
 * workspace) means the real `activate()` (and therefore the real
 * `registerStepCodeLens`/`registerHookCodeLens`, both of which call this function) can fire as
 * soon as the test host's workspace is scanned, independent of -- and possibly before -- any test
 * in this file, or even this whole suite, actually runs.
 * Busting the require cache per test sidesteps that race entirely: each test gets its own
 * `sharedEmitter`, so "first call" and "second call" are both genuinely under this test's control.
 */
function freshModule(): { getCodeLensRefreshEvent: typeof GetCodeLensRefreshEvent } {
  delete require.cache[MODULE_PATH];
  // eslint-disable-next-line @typescript-eslint/no-require-imports -- deliberate cache-busting require, see doc comment above
  return require(MODULE_PATH) as { getCodeLensRefreshEvent: typeof GetCodeLensRefreshEvent };
}

function fakeClient(onRequest?: (...args: unknown[]) => { dispose: () => void }): LanguageClient {
  return {
    onRequest: onRequest ?? (() => ({ dispose: () => undefined })),
  } as unknown as LanguageClient;
}

function fakeContext(): vscode.ExtensionContext {
  return { subscriptions: [] } as unknown as vscode.ExtensionContext;
}

suite('codeLensRefresh', () => {
  suite('getCodeLensRefreshEvent', () => {
    test('fires the returned event when the server pushes workspace/codeLens/refresh', () => {
      const { getCodeLensRefreshEvent } = freshModule();
      let handler: (() => void) | undefined;
      const client = fakeClient((...args: unknown[]) => {
        handler = args[args.length - 1] as () => void;
        return { dispose: () => undefined };
      });

      const event = getCodeLensRefreshEvent(client, fakeContext());
      let fired = false;
      event(() => {
        fired = true;
      });

      assert.ok(handler, 'onRequest handler should have been captured on first registration');
      handler();

      assert.strictEqual(fired, true);
    });

    test('registers the onRequest handler and a subscription only once, ever', () => {
      const { getCodeLensRefreshEvent } = freshModule();
      let onRequestCallCount = 0;
      const client = fakeClient(() => {
        onRequestCallCount += 1;
        return { dispose: () => undefined };
      });
      const context = fakeContext();

      getCodeLensRefreshEvent(client, context);
      const subscriptionsAfterFirstCall = context.subscriptions.length;
      // A second call -- even with a fresh client/context -- must reuse the existing
      // registration rather than adding another `onRequest` handler (which would silently
      // replace the first, per the doc comment on `getCodeLensRefreshEvent`).
      getCodeLensRefreshEvent(
        fakeClient(() => {
          onRequestCallCount += 1;
          return { dispose: () => undefined };
        }),
        fakeContext(),
      );
      getCodeLensRefreshEvent(client, context);

      assert.strictEqual(onRequestCallCount, 1);
      assert.strictEqual(context.subscriptions.length, subscriptionsAfterFirstCall);
    });

    test('returns the same event instance across repeated calls', () => {
      const { getCodeLensRefreshEvent } = freshModule();

      const first = getCodeLensRefreshEvent(fakeClient(), fakeContext());
      const second = getCodeLensRefreshEvent(fakeClient(), fakeContext());

      assert.strictEqual(first, second);
    });
  });
});
