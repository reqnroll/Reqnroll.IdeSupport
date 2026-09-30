import * as assert from 'assert';
import * as vscode from 'vscode';
import {
  cleanupRunSettingsOnOptOut,
  generatedFileName,
  recordPriorSettingIfNeeded,
  resolveGeneratedRunSettingsPath,
  RunSettingsConfigAccessor,
} from '../../testOutcomes/testOutcomesService';

/**
 * Issue #749: turning `reqnroll.testOutcomes.enabled` off used to leave
 * `dotnet.unitTests.runSettingsPath` pointing at the generated runsettings file forever, and the
 * generated file itself was one fixed name shared by every open workspace, so two windows on
 * different workspaces overwrote each other's registration. These tests cover the two pieces that
 * fix that: `generatedFileName` (per-workspace keying) and the record/restore pair around
 * `context.workspaceState` (`recordPriorSettingIfNeeded` / `cleanupRunSettingsOnOptOut`).
 *
 * `dotnet.unitTests.runSettingsPath` is contributed by C# Dev Kit, not this extension - in the
 * bare Extension Development Host this suite runs in (`--disable-extensions`, see `runTest.ts`)
 * it is not a registered configuration at all, and `vscode.workspace.getConfiguration('dotnet')
 * .update(...)` throws for it (confirmed directly: "Unable to write to Workspace Settings because
 * dotnet.unitTests.runSettingsPath is not a registered configuration"). So unlike this suite's
 * usual pattern of mutating real settings (`lspInspectorLogger.test.ts`, `watcherExclude.test.ts`),
 * `cleanupRunSettingsOnOptOut`/`mergeRunSettings` take an injected `RunSettingsConfigAccessor`
 * (mirroring the injected `readTextOrNull`/`evaluate` parameters `mtpProjectStubs.ts` uses for the
 * same "real dependency isn't available/controllable under test" reason), faked here in-memory.
 * `workspaceState` is faked the same way, mirroring the `fakeContext()` convention used elsewhere
 * in this suite for `vscode.ExtensionContext` parts a real extension host instance doesn't make
 * independently constructible.
 */

const PRIOR_KEY = 'reqnroll.testOutcomes.priorRunSettingsPath';

/** Minimal in-memory stand-in for `vscode.Memento` — enough for get/update round-trips. */
class FakeMemento implements vscode.Memento {
  private readonly store = new Map<string, unknown>();

  keys(): readonly string[] {
    return [...this.store.keys()];
  }

  get<T>(key: string): T | undefined;
  get<T>(key: string, defaultValue: T): T;
  get<T>(key: string, defaultValue?: T): T | undefined {
    return this.store.has(key) ? (this.store.get(key) as T) : defaultValue;
  }

  update(key: string, value: unknown): Thenable<void> {
    if (value === undefined) {
      this.store.delete(key);
    } else {
      this.store.set(key, value);
    }
    return Promise.resolve();
  }
}

/** Minimal in-memory stand-in for the `dotnet.unitTests.runSettingsPath` setting. */
class FakeRunSettingsConfig implements RunSettingsConfigAccessor {
  private value: string | undefined;

  constructor(initial?: string) {
    this.value = initial;
  }

  get(): string | undefined {
    return this.value;
  }

  update(value: string | undefined): Thenable<void> {
    this.value = value;
    return Promise.resolve();
  }
}

suite('testOutcomesService', () => {
  // ── generatedFileName ───────────────────────────────────────────────────

  suite('generatedFileName', () => {
    test('is deterministic for the same workspace folder path', () => {
      const a = generatedFileName('/repo/one');
      const b = generatedFileName('/repo/one');
      assert.strictEqual(a, b);
    });

    test('differs for different workspace folder paths (two windows, two workspaces)', () => {
      const one = generatedFileName('/repo/one');
      const two = generatedFileName('/repo/two');
      assert.notStrictEqual(one, two);
    });

    test('differs between a real workspace folder and no workspace folder at all', () => {
      const withFolder = generatedFileName('/repo/one');
      const noFolder = generatedFileName(undefined);
      assert.notStrictEqual(withFolder, noFolder);
    });

    test('always names a .runsettings file under the reqnroll-vscode-test-outcomes prefix', () => {
      const name = generatedFileName('/repo/one');
      assert.match(name, /^reqnroll-vscode-test-outcomes-[0-9a-f]{12}\.runsettings$/);
    });
  });

  // ── recordPriorSettingIfNeeded ──────────────────────────────────────────

  suite('recordPriorSettingIfNeeded', () => {
    const generatedPath = '/gen/reqnroll-vscode-test-outcomes-abc123.runsettings';

    test('records "was unset" when the setting is currently unset', async () => {
      const state = new FakeMemento();
      await recordPriorSettingIfNeeded(state, undefined, generatedPath);
      assert.deepStrictEqual(state.get(PRIOR_KEY), { wasSet: false });
    });

    test("records the user's own path when the setting points elsewhere", async () => {
      const state = new FakeMemento();
      await recordPriorSettingIfNeeded(state, '/user/my.runsettings', generatedPath);
      assert.deepStrictEqual(state.get(PRIOR_KEY), {
        wasSet: true,
        value: '/user/my.runsettings',
      });
    });

    test('does not record anything when the setting already points at our generated file', async () => {
      const state = new FakeMemento();
      await recordPriorSettingIfNeeded(state, generatedPath, generatedPath);
      assert.strictEqual(state.get(PRIOR_KEY), undefined);
    });

    test('never overwrites an already-recorded value on a later activation', async () => {
      const state = new FakeMemento();
      await recordPriorSettingIfNeeded(state, '/user/my.runsettings', generatedPath);
      // A later activation in the same session, now pointed at our own file.
      await recordPriorSettingIfNeeded(state, generatedPath, generatedPath);
      assert.deepStrictEqual(state.get(PRIOR_KEY), {
        wasSet: true,
        value: '/user/my.runsettings',
      });
    });
  });

  // ── cleanupRunSettingsOnOptOut ───────────────────────────────────────────

  suite('cleanupRunSettingsOnOptOut', () => {
    // The exact path `cleanupRunSettingsOnOptOut` itself computes for the real open workspace
    // (via the module-internal `resolveGeneratedRunSettingsPath`) — it compares the config
    // accessor's current value against this by `isSamePath`, so a fake value must match it
    // exactly to be recognized as "ours". The file is never actually written to disk in these
    // tests, so cleanup's best-effort `fs.rmSync` is a harmless no-op here.
    const generatedPath = resolveGeneratedRunSettingsPath();

    test('restores the setting to unset when it was unset before we took it over', async () => {
      const config = new FakeRunSettingsConfig(generatedPath);
      const state = new FakeMemento();
      await state.update(PRIOR_KEY, { wasSet: false });

      await cleanupRunSettingsOnOptOut(state, config);

      assert.strictEqual(config.get(), undefined);
      assert.strictEqual(state.get(PRIOR_KEY), undefined);
    });

    test("restores the user's own prior path", async () => {
      const config = new FakeRunSettingsConfig(generatedPath);
      const state = new FakeMemento();
      await state.update(PRIOR_KEY, { wasSet: true, value: '/user/my.runsettings' });

      await cleanupRunSettingsOnOptOut(state, config);

      assert.strictEqual(config.get(), '/user/my.runsettings');
      assert.strictEqual(state.get(PRIOR_KEY), undefined);
    });

    test('leaves the setting untouched when it no longer points at our generated file', async () => {
      const config = new FakeRunSettingsConfig('/user/someone-elses.runsettings');
      const state = new FakeMemento();
      await state.update(PRIOR_KEY, { wasSet: false });

      await cleanupRunSettingsOnOptOut(state, config);

      assert.strictEqual(config.get(), '/user/someone-elses.runsettings');
    });

    test('clears any stale recorded state even when the setting no longer points at our file', async () => {
      const config = new FakeRunSettingsConfig('/user/someone-elses.runsettings');
      const state = new FakeMemento();
      await state.update(PRIOR_KEY, { wasSet: true, value: '/stale/prior.runsettings' });

      await cleanupRunSettingsOnOptOut(state, config);

      assert.strictEqual(state.get(PRIOR_KEY), undefined);
    });

    test('is a no-op (never throws) when nothing was ever recorded and the setting is unset', async () => {
      const config = new FakeRunSettingsConfig(undefined);
      const state = new FakeMemento();

      await cleanupRunSettingsOnOptOut(state, config);

      assert.strictEqual(config.get(), undefined);
    });
  });
});
