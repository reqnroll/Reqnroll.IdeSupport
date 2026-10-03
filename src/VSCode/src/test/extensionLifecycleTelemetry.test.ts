import * as assert from 'assert';
import * as vscode from 'vscode';
import {
  LIFECYCLE_STATE_KEY,
  LifecycleState,
  compareVersions,
  computeLifecycle,
  localDateKey,
  reportExtensionLifecycle,
} from '../extensionLifecycleTelemetry';

const names = (events: { eventName: string }[]) => events.map((e) => e.eventName);

suite('extensionLifecycleTelemetry (#875)', () => {
  suite('computeLifecycle', () => {
    test('first activation sends loaded + installed and seeds state without a usage day', () => {
      const { state, events } = computeLifecycle({ usageDays: 0 }, '2026-10-03', '1.2.0');
      assert.deepStrictEqual(names(events), ['Extension loaded', 'Extension installed']);
      assert.deepStrictEqual(state, {
        installedVersion: '1.2.0',
        lastUsedDate: '2026-10-03',
        usageDays: 0,
      });
    });

    test('same-day, same-version activation sends only loaded', () => {
      const stored: LifecycleState = { installedVersion: '1.2.0', lastUsedDate: '2026-10-03', usageDays: 4 };
      const { state, events } = computeLifecycle(stored, '2026-10-03', '1.2.0');
      assert.deepStrictEqual(names(events), ['Extension loaded']);
      assert.deepStrictEqual(state, stored);
    });

    test('a new day increments usage days and names the count in the event', () => {
      const stored: LifecycleState = { installedVersion: '1.2.0', lastUsedDate: '2026-10-02', usageDays: 6 };
      const { state, events } = computeLifecycle(stored, '2026-10-03', '1.2.0');
      assert.deepStrictEqual(names(events), ['Extension loaded', '7 day usage']);
      assert.strictEqual(state.usageDays, 7);
      assert.strictEqual(state.lastUsedDate, '2026-10-03');
    });

    test('a version increase sends upgraded with OldExtensionVersion and records the new version', () => {
      const stored: LifecycleState = { installedVersion: '1.2.0', lastUsedDate: '2026-10-03', usageDays: 2 };
      const { state, events } = computeLifecycle(stored, '2026-10-03', '1.10.0');
      assert.deepStrictEqual(names(events), ['Extension loaded', 'Extension upgraded']);
      assert.deepStrictEqual(events[1].properties, { OldExtensionVersion: '1.2.0' });
      assert.strictEqual(state.installedVersion, '1.10.0');
    });

    test('a downgrade sends no upgraded event and keeps the stored version', () => {
      const stored: LifecycleState = { installedVersion: '1.3.0', lastUsedDate: '2026-10-03', usageDays: 2 };
      const { state, events } = computeLifecycle(stored, '2026-10-03', '1.2.0');
      assert.deepStrictEqual(names(events), ['Extension loaded']);
      assert.strictEqual(state.installedVersion, '1.3.0');
    });
  });

  suite('helpers', () => {
    test('compareVersions compares numerically, not lexically', () => {
      assert.strictEqual(compareVersions('1.2.0', '1.10.0'), -1);
      assert.strictEqual(compareVersions('1.10', '1.10.0'), 0);
      assert.strictEqual(compareVersions('2.0.0', '1.99.99'), 1);
    });

    test('localDateKey zero-pads month and day', () => {
      assert.strictEqual(localDateKey(new Date(2026, 0, 5)), '2026-01-05');
    });
  });

  suite('reportExtensionLifecycle', () => {
    function fakeContext(version: unknown, initial?: LifecycleState) {
      const store = new Map<string, unknown>();
      if (initial) store.set(LIFECYCLE_STATE_KEY, initial);
      const context = {
        extension: { packageJSON: { version } },
        globalState: {
          get: <T>(key: string) => store.get(key) as T | undefined,
          update: (key: string, value: unknown) => {
            store.set(key, value);
            return Promise.resolve();
          },
        },
      } as unknown as vscode.ExtensionContext;
      return { context, store };
    }

    test('sends the events and persists the new state', async () => {
      const { context, store } = fakeContext('1.2.0');
      const sent: string[] = [];
      await reportExtensionLifecycle(context, (name) => sent.push(name), new Date(2026, 9, 3));
      assert.deepStrictEqual(sent, ['Extension loaded', 'Extension installed']);
      assert.deepStrictEqual(store.get(LIFECYCLE_STATE_KEY), {
        installedVersion: '1.2.0',
        lastUsedDate: '2026-10-03',
        usageDays: 0,
      });
    });

    test('sends nothing when the extension version is unavailable', async () => {
      const { context } = fakeContext(undefined);
      const sent: string[] = [];
      await reportExtensionLifecycle(context, (name) => sent.push(name));
      assert.deepStrictEqual(sent, []);
    });

    test('swallows a failing send', async () => {
      const { context } = fakeContext('1.2.0');
      await reportExtensionLifecycle(context, () => {
        throw new Error('boom');
      });
    });
  });
});
