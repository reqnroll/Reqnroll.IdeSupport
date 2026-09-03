import * as assert from 'assert';
import * as vscode from 'vscode';
import {
  LanguageClient,
  DidOpenTextDocumentNotification,
  DidChangeTextDocumentNotification,
  DidSaveTextDocumentNotification,
  DidCloseTextDocumentNotification,
} from 'vscode-languageclient/node';
import {
  ManualDocumentSync,
  createManualSyncMiddleware,
  isCSharpDocument,
} from '../../lsp/manualDocumentSync';

function fakeDocument(
  uriString: string,
  overrides: Partial<{ languageId: string; version: number; text: string }> = {},
): vscode.TextDocument {
  return {
    uri: vscode.Uri.parse(uriString),
    languageId: overrides.languageId ?? 'csharp',
    version: overrides.version ?? 1,
    getText: () => overrides.text ?? '',
  } as unknown as vscode.TextDocument;
}

interface RecordedNotification {
  type: unknown;
  params: {
    textDocument: { uri: string; languageId?: string; version?: number };
    contentChanges?: { text: string }[];
  };
}

function fakeClient(): { client: LanguageClient; notifications: RecordedNotification[] } {
  const notifications: RecordedNotification[] = [];
  const client = {
    sendNotification: (type: unknown, params: unknown) => {
      notifications.push({ type, params: params as RecordedNotification['params'] });
      return Promise.resolve();
    },
  } as unknown as LanguageClient;
  return { client, notifications };
}

type WorkspaceEventName =
  | 'onDidOpenTextDocument'
  | 'onDidChangeTextDocument'
  | 'onDidSaveTextDocument'
  | 'onDidCloseTextDocument';

const WORKSPACE_EVENT_NAMES: readonly WorkspaceEventName[] = [
  'onDidOpenTextDocument',
  'onDidChangeTextDocument',
  'onDidSaveTextDocument',
  'onDidCloseTextDocument',
];

/**
 * Stands in for the slice of `vscode.workspace` that `ManualDocumentSync` reads at construction
 * time (`textDocuments`) and subscribes to afterwards (the four `onDid*TextDocument` events),
 * so its behavior can be driven deterministically with synthetic documents/events instead of
 * real file I/O and editor interactions -- mirroring the `registerCodeLensProvider`-capturing
 * technique already used by `stepCodeLens.test.ts`/`hookCodeLens.test.ts`, just applied to
 * `vscode.workspace`'s event emitters instead of `vscode.languages`.
 */
function withStubbedWorkspace<T>(
  initialDocuments: vscode.TextDocument[],
  fn: (control: {
    fireOpen: (doc: vscode.TextDocument) => void;
    fireChange: (event: vscode.TextDocumentChangeEvent) => void;
    fireSave: (doc: vscode.TextDocument) => void;
    fireClose: (doc: vscode.TextDocument) => void;
    disposedCount: () => number;
  }) => T,
): T {
  const originalEventDescriptors = WORKSPACE_EVENT_NAMES.map(
    (name) => [name, Object.getOwnPropertyDescriptor(vscode.workspace, name)!] as const,
  );
  const originalTextDocuments = Object.getOwnPropertyDescriptor(vscode.workspace, 'textDocuments')!;

  const captured: Partial<Record<WorkspaceEventName, (arg: unknown) => unknown>> = {};
  let disposedCount = 0;

  for (const name of WORKSPACE_EVENT_NAMES) {
    Object.defineProperty(vscode.workspace, name, {
      configurable: true,
      value: (listener: (arg: unknown) => unknown) => {
        captured[name] = listener;
        return { dispose: () => (disposedCount += 1) };
      },
    });
  }
  Object.defineProperty(vscode.workspace, 'textDocuments', {
    configurable: true,
    value: initialDocuments,
  });

  try {
    return fn({
      fireOpen: (doc) => captured.onDidOpenTextDocument?.(doc),
      fireChange: (event) => captured.onDidChangeTextDocument?.(event),
      fireSave: (doc) => captured.onDidSaveTextDocument?.(doc),
      fireClose: (doc) => captured.onDidCloseTextDocument?.(doc),
      disposedCount: () => disposedCount,
    });
  } finally {
    for (const [name, descriptor] of originalEventDescriptors) {
      Object.defineProperty(vscode.workspace, name, descriptor);
    }
    Object.defineProperty(vscode.workspace, 'textDocuments', originalTextDocuments);
  }
}

suite('manualDocumentSync', () => {
  suite('isCSharpDocument', () => {
    test('true for a .cs path', () => {
      assert.strictEqual(isCSharpDocument(fakeDocument('file:///Steps.cs')), true);
    });

    test('matches case-insensitively', () => {
      assert.strictEqual(isCSharpDocument(fakeDocument('file:///Steps.CS')), true);
    });

    test('false for a .feature path', () => {
      assert.strictEqual(isCSharpDocument(fakeDocument('file:///Steps.feature')), false);
    });

    test('false for a path merely containing "cs" without the extension', () => {
      assert.strictEqual(isCSharpDocument(fakeDocument('file:///Discs.txt')), false);
    });
  });

  suite('createManualSyncMiddleware', () => {
    test('didOpen swallows an owned document without calling next', () => {
      const middleware = createManualSyncMiddleware(() => true);
      let nextCalled = false;
      const result = middleware.didOpen!(fakeDocument('file:///Owned.cs'), () => {
        nextCalled = true;
        return Promise.resolve();
      });

      assert.strictEqual(nextCalled, false);
      return result;
    });

    test('didOpen forwards a non-owned document to next', async () => {
      const middleware = createManualSyncMiddleware(() => false);
      let nextCalled = false;
      await middleware.didOpen!(fakeDocument('file:///NotOwned.feature'), () => {
        nextCalled = true;
        return Promise.resolve();
      });

      assert.strictEqual(nextCalled, true);
    });

    test('didChange consults owns() against event.document', async () => {
      const owned = fakeDocument('file:///Owned.cs');
      const middleware = createManualSyncMiddleware((doc) => doc === owned);
      let nextCalled = false;

      await middleware.didChange!(
        { document: owned, contentChanges: [], reason: undefined },
        () => {
          nextCalled = true;
          return Promise.resolve();
        },
      );
      assert.strictEqual(nextCalled, false, 'owned document should be swallowed');

      const other = fakeDocument('file:///Other.feature');
      await middleware.didChange!(
        { document: other, contentChanges: [], reason: undefined },
        () => {
          nextCalled = true;
          return Promise.resolve();
        },
      );
      assert.strictEqual(nextCalled, true, 'non-owned document should be forwarded');
    });

    test('didSave and didClose swallow an owned document without calling next', async () => {
      const middleware = createManualSyncMiddleware(() => true);
      const doc = fakeDocument('file:///Owned.cs');
      let nextCalled = false;
      const next = () => {
        nextCalled = true;
        return Promise.resolve();
      };

      await middleware.didSave!(doc, next);
      assert.strictEqual(nextCalled, false);

      await middleware.didClose!(doc, next);
      assert.strictEqual(nextCalled, false);
    });
  });

  suite('ManualDocumentSync', () => {
    test('sends didOpen for documents already open at construction time, filtered by owns()', () => {
      withStubbedWorkspace(
        [fakeDocument('file:///Already.cs'), fakeDocument('file:///Skip.feature')],
        () => {
          const { client, notifications } = fakeClient();
          const sync = new ManualDocumentSync(client, isCSharpDocument);
          sync.dispose();

          assert.strictEqual(notifications.length, 1);
          assert.strictEqual(notifications[0].type, DidOpenTextDocumentNotification.type);
          assert.strictEqual(notifications[0].params.textDocument.uri, 'file:///Already.cs');
        },
      );
    });

    test('sends didOpen when a matching document opens later, but not one owns() excludes', () => {
      withStubbedWorkspace([], (ws) => {
        const { client, notifications } = fakeClient();
        const sync = new ManualDocumentSync(client, isCSharpDocument);

        ws.fireOpen(fakeDocument('file:///New.cs'));
        ws.fireOpen(fakeDocument('file:///Ignored.feature'));
        sync.dispose();

        assert.strictEqual(notifications.length, 1);
        assert.strictEqual(notifications[0].params.textDocument.uri, 'file:///New.cs');
      });
    });

    test('does not double-send didOpen for a document already tracked as open', () => {
      withStubbedWorkspace([], (ws) => {
        const { client, notifications } = fakeClient();
        const sync = new ManualDocumentSync(client, isCSharpDocument);
        const doc = fakeDocument('file:///Dup.cs');

        ws.fireOpen(doc);
        ws.fireOpen(doc);
        sync.dispose();

        const opens = notifications.filter((n) => n.type === DidOpenTextDocumentNotification.type);
        assert.strictEqual(opens.length, 1);
      });
    });

    test('sends didChange with the full document text for a tracked document', () => {
      withStubbedWorkspace([], (ws) => {
        const { client, notifications } = fakeClient();
        const sync = new ManualDocumentSync(client, isCSharpDocument);
        const doc = fakeDocument('file:///Tracked.cs', { version: 2, text: 'updated text' });

        ws.fireOpen(doc);
        ws.fireChange({
          document: doc,
          contentChanges: [{}],
          reason: undefined,
        } as unknown as vscode.TextDocumentChangeEvent);
        sync.dispose();

        const change = notifications.find((n) => n.type === DidChangeTextDocumentNotification.type);
        assert.ok(change, 'expected a didChange notification');
        assert.strictEqual(change.params.textDocument.uri, 'file:///Tracked.cs');
        assert.strictEqual(change.params.textDocument.version, 2);
        assert.deepStrictEqual(change.params.contentChanges, [{ text: 'updated text' }]);
      });
    });

    test('ignores a change event with zero content changes (save-time formatting/EOL noise)', () => {
      withStubbedWorkspace([], (ws) => {
        const { client, notifications } = fakeClient();
        const sync = new ManualDocumentSync(client, isCSharpDocument);
        const doc = fakeDocument('file:///NoOp.cs');

        ws.fireOpen(doc);
        notifications.length = 0;
        ws.fireChange({ document: doc, contentChanges: [], reason: undefined });
        sync.dispose();

        assert.strictEqual(notifications.length, 0);
      });
    });

    test('ignores a change event for a document never opened through this instance', () => {
      withStubbedWorkspace([], (ws) => {
        const { client, notifications } = fakeClient();
        const sync = new ManualDocumentSync(client, isCSharpDocument);
        const doc = fakeDocument('file:///Untracked.cs');

        ws.fireChange({
          document: doc,
          contentChanges: [{}],
          reason: undefined,
        } as unknown as vscode.TextDocumentChangeEvent);
        sync.dispose();

        assert.strictEqual(notifications.length, 0);
      });
    });

    test('sends didSave only for a document tracked as open', () => {
      withStubbedWorkspace([], (ws) => {
        const { client, notifications } = fakeClient();
        const sync = new ManualDocumentSync(client, isCSharpDocument);
        const tracked = fakeDocument('file:///Saved.cs');
        const untracked = fakeDocument('file:///NotTracked.cs');

        ws.fireOpen(tracked);
        notifications.length = 0;
        ws.fireSave(tracked);
        ws.fireSave(untracked);
        sync.dispose();

        assert.strictEqual(notifications.length, 1);
        assert.strictEqual(notifications[0].type, DidSaveTextDocumentNotification.type);
        assert.strictEqual(notifications[0].params.textDocument.uri, 'file:///Saved.cs');
      });
    });

    test('sends didClose and stops tracking the document, only for one tracked as open', () => {
      withStubbedWorkspace([], (ws) => {
        const { client, notifications } = fakeClient();
        const sync = new ManualDocumentSync(client, isCSharpDocument);
        const tracked = fakeDocument('file:///Closed.cs');

        ws.fireOpen(tracked);
        notifications.length = 0;
        ws.fireClose(tracked);
        ws.fireClose(fakeDocument('file:///NeverOpened.cs'));
        sync.dispose();

        assert.strictEqual(notifications.length, 1);
        assert.strictEqual(notifications[0].type, DidCloseTextDocumentNotification.type);
        assert.strictEqual(notifications[0].params.textDocument.uri, 'file:///Closed.cs');
      });
    });

    test('re-sends didOpen for a document re-opened after being closed', () => {
      withStubbedWorkspace([], (ws) => {
        const { client, notifications } = fakeClient();
        const sync = new ManualDocumentSync(client, isCSharpDocument);
        const doc = fakeDocument('file:///Reopen.cs');

        ws.fireOpen(doc);
        ws.fireClose(doc);
        ws.fireOpen(doc);
        sync.dispose();

        const opens = notifications.filter((n) => n.type === DidOpenTextDocumentNotification.type);
        assert.strictEqual(opens.length, 2);
      });
    });

    test('dispose() disposes all four workspace subscriptions', () => {
      withStubbedWorkspace([], (ws) => {
        const { client } = fakeClient();
        const sync = new ManualDocumentSync(client, isCSharpDocument);

        sync.dispose();

        assert.strictEqual(ws.disposedCount(), 4);
      });
    });

    test('a change after dispose() is not forwarded (openUris was cleared)', () => {
      withStubbedWorkspace([], (ws) => {
        const { client, notifications } = fakeClient();
        const sync = new ManualDocumentSync(client, isCSharpDocument);
        const doc = fakeDocument('file:///AfterDispose.cs');

        ws.fireOpen(doc);
        sync.dispose();
        notifications.length = 0;

        // The real vscode.workspace event subscriptions are disposed, but this test's stub
        // doesn't unhook the captured listener functions themselves -- exercising them directly
        // still lets us confirm dispose() reset ManualDocumentSync's own bookkeeping (openUris),
        // independent of whether VS Code would actually still deliver the event post-dispose.
        ws.fireChange({
          document: doc,
          contentChanges: [{}],
          reason: undefined,
        } as unknown as vscode.TextDocumentChangeEvent);

        assert.strictEqual(notifications.length, 0);
      });
    });
  });
});
