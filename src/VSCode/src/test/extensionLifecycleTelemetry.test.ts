import * as assert from 'assert';
import * as vscode from 'vscode';
import {
  LIFECYCLE_STATE_KEY,
  LifecycleState,
  compareVersions,
  computeInstallOrUpgrade,
  computeUse,
  localDateKey,
  reportExtensionInstallOrUpgrade,
  reportExtensionUse,
} from '../extensionLifecycleTelemetry';

const names = (events: { eventName: string }[]) => events.map((e) => e.eventName);

suite('extensionLifecycleTelemetry (#875)', () => {
  suite('computeInstallOrUpgrade', () => {
    test('first activation sends installed and seeds state without a usage day', () => {
      const { state, events } = computeInstallOrUpgrade({ usageDays: 0 }, '2026-10-03', '1.2.0');
      assert.deepStrictEqual(names(events), ['Lifecycle.Extension.Installed']);
      assert.deepStrictEqual(state, {
        installedVersion: '1.2.0',
        lastUsedDate: '2026-10-03',
        usageDays: 0,
      });
    });

    test('same version sends nothing and leaves the state alone', () => {
      const stored: LifecycleState = {
        installedVersion: '1.2.0',
        lastUsedDate: '2026-10-02',
        usageDays: 4,
      };
      const { state, events } = computeInstallOrUpgrade(stored, '2026-10-03', '1.2.0');
      assert.deepStrictEqual(names(events), []);
      assert.deepStrictEqual(state, stored);
    });

    test('a version increase sends upgraded with OldExtensionVersion and records the new version, without counting a day', () => {
      const stored: LifecycleState = {
        installedVersion: '1.2.0',
        lastUsedDate: '2026-10-02',
        usageDays: 2,
      };
      const { state, events } = computeInstallOrUpgrade(stored, '2026-10-03', '1.10.0');
      assert.deepStrictEqual(names(events), ['Lifecycle.Extension.Upgraded']);
      assert.deepStrictEqual(events[0].properties, { OldExtensionVersion: '1.2.0' });
      assert.deepStrictEqual(state, { ...stored, installedVersion: '1.10.0' });
    });

    test('a downgrade sends no upgraded event and keeps the stored version', () => {
      const stored: LifecycleState = {
        installedVersion: '1.3.0',
        lastUsedDate: '2026-10-03',
        usageDays: 2,
      };
      const { state, events } = computeInstallOrUpgrade(stored, '2026-10-03', '1.2.0');
      assert.deepStrictEqual(names(events), []);
      assert.strictEqual(state.installedVersion, '1.3.0');
    });
  });

  suite('computeUse', () => {
    test('on the install day sends only loaded', () => {
      const stored: LifecycleState = {
        installedVersion: '1.2.0',
        lastUsedDate: '2026-10-03',
        usageDays: 0,
      };
      const { state, events } = computeUse(stored, '2026-10-03');
      assert.deepStrictEqual(names(events), ['Lifecycle.Extension.Loaded']);
      assert.deepStrictEqual(state, stored);
    });

    test('a new day increments usage days and carries the count as UsageDays', () => {
      const stored: LifecycleState = {
        installedVersion: '1.2.0',
        lastUsedDate: '2026-10-02',
        usageDays: 6,
      };
      const { state, events } = computeUse(stored, '2026-10-03');
      assert.deepStrictEqual(names(events), [
        'Lifecycle.Extension.Loaded',
        'Lifecycle.Usage.Recorded',
      ]);
      assert.deepStrictEqual(events[1].properties, { UsageDays: '7' });
      assert.strictEqual(state.usageDays, 7);
      assert.strictEqual(state.lastUsedDate, '2026-10-03');
    });

    test('with no stored state sends only loaded', () => {
      const { state, events } = computeUse({ usageDays: 0 }, '2026-10-03');
      assert.deepStrictEqual(names(events), ['Lifecycle.Extension.Loaded']);
      assert.deepStrictEqual(state, { usageDays: 0 });
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

  suite('reportExtensionInstallOrUpgrade / reportExtensionUse', () => {
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

    test('install at activation, then use on detection, persists one combined state', async () => {
      const { context, store } = fakeContext('1.2.0');
      const sent: string[] = [];
      const send = (name: string) => sent.push(name);
      const day = new Date(2026, 9, 3);

      const install = reportExtensionInstallOrUpgrade(context, send, day);
      const use = reportExtensionUse(context, send, day);
      await Promise.all([install, use]);

      assert.deepStrictEqual(sent, ['Lifecycle.Extension.Installed', 'Lifecycle.Extension.Loaded']);
      assert.deepStrictEqual(store.get(LIFECYCLE_STATE_KEY), {
        installedVersion: '1.2.0',
        lastUsedDate: '2026-10-03',
        usageDays: 0,
      });
    });

    test('a later day counts a usage day only when use is reported', async () => {
      const { context, store } = fakeContext('1.2.0', {
        installedVersion: '1.2.0',
        lastUsedDate: '2026-10-02',
        usageDays: 3,
      });
      const sent: string[] = [];
      const send = (name: string) => sent.push(name);
      const day = new Date(2026, 9, 3);

      await reportExtensionInstallOrUpgrade(context, send, day);
      assert.deepStrictEqual(sent, []);
      assert.strictEqual((store.get(LIFECYCLE_STATE_KEY) as LifecycleState).usageDays, 3);

      await reportExtensionUse(context, send, day);
      assert.deepStrictEqual(sent, ['Lifecycle.Extension.Loaded', 'Lifecycle.Usage.Recorded']);
      assert.strictEqual((store.get(LIFECYCLE_STATE_KEY) as LifecycleState).usageDays, 4);
    });

    test('sends nothing when the extension version is unavailable', async () => {
      const { context } = fakeContext(undefined);
      const sent: string[] = [];
      await reportExtensionInstallOrUpgrade(context, (name) => sent.push(name));
      await reportExtensionUse(context, (name) => sent.push(name));
      assert.deepStrictEqual(sent, []);
    });

    test('swallows a failing send', async () => {
      const { context } = fakeContext('1.2.0');
      await reportExtensionInstallOrUpgrade(context, () => {
        throw new Error('boom');
      });
      await reportExtensionUse(context, () => {
        throw new Error('boom');
      });
    });
  });
});
