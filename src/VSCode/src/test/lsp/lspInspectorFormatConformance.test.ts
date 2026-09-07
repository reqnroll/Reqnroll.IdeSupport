import * as assert from 'assert';
import { parseLspTraceMessage } from '../../lsp/lspInspectorLogger';

/**
 * Cross-language conformance check (issue #628) between this file's `parseLspTraceMessage` and
 * the VS extension's independent reimplementation of the same
 * [lsp-viewer](https://lampepfl.github.io/lsp-viewer/) wire format,
 * `src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/LspInterception/LspInspectorLogger.cs`.
 *
 * The two implementations consume different *inputs* by necessity - this side only ever sees
 * vscode-languageclient's human-readable `TraceFormat.Text` summary lines and has to reconstruct
 * the envelope from them, while the C# side intercepts already-structured JSON-RPC objects off
 * VS's own duplex pipe - so a single fixture can't feed both directly. Instead, the cases below
 * are duplicated by hand into
 * `tests/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Tests/LspInterception/LspInspectorLoggerFormatConformanceTests.cs`,
 * one input per language for the same logical JSON-RPC event, asserting both produce the same
 * `type` and `message`. **Keep the two files' cases in sync** if either implementation's output
 * shape changes.
 *
 * Deliberately excluded from the comparison: `timestamp` (wall-clock, inherently different per
 * run) and the "extended, ignored by the tool" `latencyMs`/`traceId` fields - which writing this
 * test incidentally confirmed this side never emits at all (no such fields in `LspEntry`), an
 * asymmetry between the two implementations' own bonus diagnostic value that doesn't violate the
 * external lsp-viewer contract (which ignores both fields) and is intentionally left alone here
 * rather than expanded into a feature addition.
 */
suite('parseLspTraceMessage format conformance', () => {
  test('send-request matches the shared fixture', () => {
    const entry = parseLspTraceMessage(
      'Sending request \'textDocument/completion - (5)\'.\nParams: {"foo":"bar"}',
    );

    assert.ok(entry);
    assert.strictEqual(entry.type, 'send-request');
    assert.deepStrictEqual(entry.message, {
      jsonrpc: '2.0',
      method: 'textDocument/completion',
      id: 5,
      params: { foo: 'bar' },
    });
  });

  test('receive-response with result matches the shared fixture', () => {
    const entry = parseLspTraceMessage(
      'Received response \'textDocument/completion - (5)\' in 12ms.\nResult: {"items":[]}',
    );

    assert.ok(entry);
    assert.strictEqual(entry.type, 'receive-response');
    assert.deepStrictEqual(entry.message, {
      jsonrpc: '2.0',
      id: 5,
      result: { items: [] },
    });
  });

  test('send-notification matches the shared fixture', () => {
    const entry = parseLspTraceMessage(
      'Sending notification \'textDocument/didChange\'.\nParams: {"uri":"file:///a.feature"}',
    );

    assert.ok(entry);
    assert.strictEqual(entry.type, 'send-notification');
    assert.deepStrictEqual(entry.message, {
      jsonrpc: '2.0',
      method: 'textDocument/didChange',
      params: { uri: 'file:///a.feature' },
    });
  });
});
