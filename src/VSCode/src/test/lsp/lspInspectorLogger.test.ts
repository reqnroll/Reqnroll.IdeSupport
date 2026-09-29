import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { resolveLogDirectory } from '../../logging/logPaths';
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
// value it sends the server (see the FileLspTraceChannel doc comment) — it must track the
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

// Issue #792: the wire trace has no Output panel any more, so the inspector file is its only
// artifact — pin that trace() still lands there, in lsp-viewer format, once tracing is enabled.
suite('createTraceChannel file output', () => {
  const config = vscode.workspace.getConfiguration('reqnroll');
  let channel: vscode.LogOutputChannel | undefined;

  teardown(async () => {
    channel?.dispose();
    channel = undefined;
    await config.update('trace.server', undefined, vscode.ConfigurationTarget.Global);
  });

  test('trace() writes an lsp-viewer entry to a reqnroll-vscode-inspector file', async () => {
    const startedAt = Date.now();
    await config.update('trace.server', 'verbose', vscode.ConfigurationTarget.Global);
    channel = createTraceChannel();

    channel.trace("Sending request 'textDocument/hover - (7)'.\nParams: {\"x\":1}");
    channel.dispose();
    channel = undefined;

    const dir = resolveLogDirectory();
    const deadline = Date.now() + 5000;
    let found = false;
    while (!found && Date.now() < deadline) {
      found = fs
        .readdirSync(dir)
        .filter((n) => n.startsWith('reqnroll-vscode-inspector-'))
        .map((n) => path.join(dir, n))
        .filter((f) => fs.statSync(f).mtimeMs >= startedAt - 1000)
        .some((f) => fs.readFileSync(f, 'utf8').includes('"method":"textDocument/hover"'));
      if (!found) await new Promise((r) => setTimeout(r, 100));
    }
    assert.ok(found, 'expected the trace entry in a fresh reqnroll-vscode-inspector-*.log file');
  });
});
