import * as path from 'path';
import Mocha from 'mocha';

/**
 * Bootstraps the Mocha `suite`/`test` globals inside the Extension Development Host and runs the
 * bundled test file. `@vscode/test-electron` loads this module's `run` export as
 * `extensionTestsPath` — pointing that directly at a compiled test file never
 * registers Mocha's BDD globals, so every suite/test call throws `ReferenceError: suite is not
 * defined` before a single test runs.
 */
export function run(): Promise<void> {
  // Default Mocha timeout (2000ms) races against extension.test.ts's own 2000ms activation
  // wait; give suites headroom for the suites that actually run.
  const mocha = new Mocha({ ui: 'tdd', color: true, timeout: 10000 });
  const testsRoot = path.resolve(__dirname);

  // CI runs with REQNROLL_TELEMETRY_ENABLED=0 so the real extension never reports to Application
  // Insights, but the telemetry suites assert that events *are* forwarded to their stubbed reporter.
  // Pin the kill switch to enabled around every test so they do not depend on the ambient value;
  // a test that needs another value sets it itself and the original is restored afterwards.
  let originalTelemetryEnabled: string | undefined;
  mocha.suite.beforeEach(() => {
    originalTelemetryEnabled = process.env.REQNROLL_TELEMETRY_ENABLED;
    process.env.REQNROLL_TELEMETRY_ENABLED = '1';
  });
  mocha.suite.afterEach(() => {
    if (originalTelemetryEnabled === undefined) delete process.env.REQNROLL_TELEMETRY_ENABLED;
    else process.env.REQNROLL_TELEMETRY_ENABLED = originalTelemetryEnabled;
  });

  // extension.test.js pulls in the other suites (projectManager.test, lspInspectorLogger.test)
  // via its own imports, so registering just the entry point is enough.
  mocha.addFile(path.resolve(testsRoot, 'extension.test.js'));

  return new Promise((resolve, reject) => {
    try {
      mocha.run((failures) => {
        if (failures > 0) {
          reject(new Error(`${failures} test(s) failed.`));
        } else {
          resolve();
        }
      });
    } catch (err) {
      reject(err instanceof Error ? err : new Error(String(err)));
    }
  });
}
