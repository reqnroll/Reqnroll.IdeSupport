import * as assert from 'assert';
import * as vscode from 'vscode';
import { createTraceChannel, traceServerToLogLevel } from '../../lsp/lspInspectorLogger';

suite('traceServerToLogLevel', () => {
  const config = vscode.workspace.getConfiguration('reqnroll');

  teardown(async () => {
    await config.update('trace.server', undefined, vscode.ConfigurationTarget.Global);
  });

  test('defaults to Warning when the setting is unset', async () => {
    await config.update('trace.server', undefined, vscode.ConfigurationTarget.Global);
    assert.strictEqual(traceServerToLogLevel(), 'Warning');
  });

  test("maps 'off' to Warning", async () => {
    await config.update('trace.server', 'off', vscode.ConfigurationTarget.Global);
    assert.strictEqual(traceServerToLogLevel(), 'Warning');
  });

  test("maps 'messages' to Info", async () => {
    await config.update('trace.server', 'messages', vscode.ConfigurationTarget.Global);
    assert.strictEqual(traceServerToLogLevel(), 'Info');
  });

  test("maps 'verbose' to Verbose", async () => {
    await config.update('trace.server', 'verbose', vscode.ConfigurationTarget.Global);
    assert.strictEqual(traceServerToLogLevel(), 'Verbose');
  });
});

// The channel's logLevel is what vscode-languageclient reads to decide the InitializeParams.Trace
// value it sends the server (see the TeeLogOutputChannel doc comment) — it must track the
// `reqnroll.trace.server` setting rather than always claiming Trace, or the server ends up
// tracing regardless of what the user actually asked for.
suite('createTraceChannel logLevel', () => {
  const config = vscode.workspace.getConfiguration('reqnroll');
  let channel: vscode.LogOutputChannel;

  teardown(async () => {
    channel?.dispose();
    await config.update('trace.server', undefined, vscode.ConfigurationTarget.Global);
  });

  test("reports Trace when the setting is 'messages'", async () => {
    await config.update('trace.server', 'messages', vscode.ConfigurationTarget.Global);
    channel = createTraceChannel();
    assert.strictEqual(channel.logLevel, vscode.LogLevel.Trace);
  });

  test("reports Trace when the setting is 'verbose'", async () => {
    await config.update('trace.server', 'verbose', vscode.ConfigurationTarget.Global);
    channel = createTraceChannel();
    assert.strictEqual(channel.logLevel, vscode.LogLevel.Trace);
  });

  test('reflects a live setting change back to off, and fires onDidChangeLogLevel', async () => {
    await config.update('trace.server', 'verbose', vscode.ConfigurationTarget.Global);
    channel = createTraceChannel();
    assert.strictEqual(channel.logLevel, vscode.LogLevel.Trace);

    const changed = new Promise<vscode.LogLevel>((resolve) => {
      channel.onDidChangeLogLevel((level) => resolve(level));
    });
    await config.update('trace.server', 'off', vscode.ConfigurationTarget.Global);

    assert.strictEqual(await changed, vscode.LogLevel.Off);
    assert.strictEqual(channel.logLevel, vscode.LogLevel.Off);
  });
});
