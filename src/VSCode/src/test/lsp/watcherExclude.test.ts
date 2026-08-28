import * as assert from 'assert';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as vscode from 'vscode';

/**
 * Proves/disproves Q9's leading hypothesis (issue #31): does `files.watcherExclude` suppress
 * delivery of events from the exact `vscode.workspace.createFileSystemWatcher(new
 * vscode.RelativePattern(outputDir, '*.dll'))` construction `ProjectManager.watchProjectOutputPath`
 * uses (projectManager.ts:330-332)? Mirrors that construction against a throwaway bin/ directory
 * under the open workspace, toggling the exclude on and off via the *user* (Global) settings scope
 * — that's the scope the issue's "common workspace/user default" concern actually refers to, and
 * it writes to the isolated `.vscode-test/user-data` profile rather than the repo's own
 * `.vscode/settings.json`.
 */
suite('files.watcherExclude vs RelativePattern watcher (issue #31 / Q9)', function () {
  this.timeout(30_000);

  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  if (!workspaceFolder) {
    throw new Error('This suite requires an open workspace folder');
  }

  const testRoot = path.join(workspaceFolder.uri.fsPath, '__watcherExcludeTest__');

  async function waitForWatcherEvent(
    outputDir: string,
    dllPath: string,
    timeoutMs = 10_000,
  ): Promise<boolean> {
    await fs.mkdir(outputDir, { recursive: true });

    // Exact same watcher construction as ProjectManager.watchProjectOutputPath.
    const watcher = vscode.workspace.createFileSystemWatcher(
      new vscode.RelativePattern(outputDir, '*.dll'),
    );

    try {
      const fired = new Promise<boolean>((resolve) => {
        const timer = setTimeout(() => resolve(false), timeoutMs);
        const disposable = watcher.onDidCreate(() => {
          clearTimeout(timer);
          disposable.dispose();
          resolve(true);
        });
      });

      // Let the watcher fully arm before the write races it — CI's Linux runners have been
      // observed needing more headroom than a local dev machine for both the config update to
      // settle and the watcher to actually start delivering inotify events.
      await new Promise((resolve) => setTimeout(resolve, 1000));
      await fs.writeFile(dllPath, 'fake-dll-bytes');

      return await fired;
    } finally {
      watcher.dispose();
    }
  }

  teardown(async () => {
    await vscode.workspace
      .getConfiguration()
      .update('files.watcherExclude', undefined, vscode.ConfigurationTarget.Global);
    await fs.rm(testRoot, { recursive: true, force: true });
  });

  test('control: watcher fires when files.watcherExclude does not exclude bin/', async () => {
    await vscode.workspace
      .getConfiguration()
      .update(
        'files.watcherExclude',
        { '**/.git/objects/**': true },
        vscode.ConfigurationTarget.Global,
      );
    await new Promise((resolve) => setTimeout(resolve, 500)); // let the exclude change settle

    const outputDir = path.join(testRoot, 'no-exclude', 'bin', 'Debug', 'net8.0');
    const fired = await waitForWatcherEvent(outputDir, path.join(outputDir, 'App.dll'));

    assert.strictEqual(fired, true, 'Expected the watcher to fire when bin/ is not excluded');
  });

  test('diagnostic: default files.watcherExclude value (unmodified)', () => {
    const inspected = vscode.workspace.getConfiguration().inspect('files.watcherExclude');
    console.log(
      `[Q9 DEFAULT] files.watcherExclude defaultValue=${JSON.stringify(inspected?.defaultValue)}`,
    );
  });

  test('Q9: watcher behavior when files.watcherExclude excludes **/bin/**', async () => {
    await vscode.workspace
      .getConfiguration()
      .update('files.watcherExclude', { '**/bin/**': true }, vscode.ConfigurationTarget.Global);
    await new Promise((resolve) => setTimeout(resolve, 500)); // let the exclude change settle

    const outputDir = path.join(testRoot, 'with-exclude', 'bin', 'Debug', 'net8.0');
    const fired = await waitForWatcherEvent(outputDir, path.join(outputDir, 'App.dll'));

    console.log(
      `[Q9 RESULT] RelativePattern(outputDir, '*.dll') watcher under ` +
        `files.watcherExclude('**/bin/**': true): fired=${fired}`,
    );
  });
});
