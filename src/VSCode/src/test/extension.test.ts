import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient, State } from 'vscode-languageclient/node';
import { ReqnrollExtensionApi } from '../extension';

// Pull in all additional test suites so the single entry-point loads them all
import './projectManager.test';
import './lspInspectorLogger.test';
import './renameDisambiguation.test';
import './tableHighlightService.test';
import './stepNavigation.test';
import './statusBar.test';

/**
 * Waits for the language client to reach `State.Running`, per issue #205's suggested fix.
 * Checks the current state first — the client may already be running by the time this is
 * called — before subscribing, to avoid missing a transition that already happened.
 */
async function waitForClientRunning(client: LanguageClient, timeoutMs = 10_000): Promise<void> {
  if (client.state === State.Running) return;

  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => {
      disposable.dispose();
      reject(
        new Error(
          `Language client did not reach Running state within ${timeoutMs}ms ` +
            `(current state: ${State[client.state]})`,
        ),
      );
    }, timeoutMs);

    const disposable = client.onDidChangeState((e) => {
      if (e.newState === State.Running) {
        clearTimeout(timer);
        disposable.dispose();
        resolve();
      }
    });
  });
}

suite('Reqnroll Extension Tests', () => {
  const extensionId = 'reqnroll.reqnroll-ide-support';

  test('Extension should be present', () => {
    const ext = vscode.extensions.getExtension(extensionId);
    assert.ok(ext, `Extension ${extensionId} is not installed`);
  });

  test('Extension should activate on gherkin language', async () => {
    const ext = vscode.extensions.getExtension(extensionId)!;
    await ext.activate();
    assert.ok(ext.isActive, 'Extension did not activate');
  });

  test('Language client should start after activation', async () => {
    const ext = vscode.extensions.getExtension<ReqnrollExtensionApi>(extensionId)!;
    await ext.activate();
    assert.ok(ext.isActive, 'Extension should be active after activation');

    const client = ext.exports.getClient();
    assert.ok(client, 'Extension did not export a language client');
    await waitForClientRunning(client);

    assert.strictEqual(client.state, State.Running, 'Language client should be running');
  });

  test('Gherkin language should be registered', async () => {
    const languages = await vscode.languages.getLanguages();
    assert.ok(
      languages.includes('gherkin'),
      `gherkin language not registered. Available: ${languages.join(', ')}`,
    );
  });
});
