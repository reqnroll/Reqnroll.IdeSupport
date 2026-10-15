import * as assert from 'assert';
import * as vscode from 'vscode';
import { ResponseError } from 'vscode-languageclient/node';
import {
  asEditRejection,
  createEditRejectionMiddleware,
  EDIT_REJECTED_CODE,
} from '../../lsp/editRejection';
import { SaveTracker } from '../../lsp/saveTracker';
import { createRenameMiddleware } from '../../commands/renameStep';

/** Stubs `vscode.window.showErrorMessage`, recording each call, for the duration of `fn`. */
async function withErrorMessageStub<T>(
  calls: { message: string; options: unknown }[],
  fn: () => Promise<T>,
): Promise<T> {
  const original = vscode.window.showErrorMessage;
  (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage = (
    message: string,
    options?: unknown,
  ) => {
    calls.push({ message, options });
    return Promise.resolve(undefined);
  };
  try {
    return await fn();
  } finally {
    (vscode.window as unknown as { showErrorMessage: unknown }).showErrorMessage = original;
  }
}

suite('editRejection', () => {
  suite('asEditRejection', () => {
    test('the code is the LSP RequestFailed code', () => {
      assert.strictEqual(EDIT_REJECTED_CODE, -32803);
    });

    test('returns the message of a -32803 ResponseError', () => {
      assert.strictEqual(
        asEditRejection(new ResponseError(-32803, 'Parameter count mismatch')),
        'Parameter count mismatch',
      );
    });

    test('is undefined for any other code, including ContentModified', () => {
      for (const code of [-32801, -32603, -32800, 0]) {
        assert.strictEqual(
          asEditRejection(new ResponseError(code, 'something')),
          undefined,
          `${code}`,
        );
      }
    });

    test('is undefined for a blank message or a non-ResponseError', () => {
      assert.strictEqual(asEditRejection(new ResponseError(-32803, '   ')), undefined);
      assert.strictEqual(asEditRejection(new Error('-32803')), undefined);
      assert.strictEqual(asEditRejection(undefined), undefined);
      assert.strictEqual(asEditRejection('Parameter count mismatch'), undefined);
    });
  });

  suite('createEditRejectionMiddleware', () => {
    const doc = { uri: vscode.Uri.parse('file:///doc.feature') } as vscode.TextDocument;
    const options = {} as vscode.FormattingOptions;
    const range = {} as vscode.Range;
    const token = {} as vscode.CancellationToken;

    test('a formatting rejection is shown in a modal and resolves to no edits', async () => {
      const calls: { message: string; options: unknown }[] = [];
      const mw = createEditRejectionMiddleware();

      const result = await withErrorMessageStub(calls, () =>
        Promise.resolve(
          mw.provideDocumentFormattingEdits!(doc, options, token, () =>
            Promise.reject(new ResponseError(-32803, 'no Feature could be parsed')),
          ),
        ),
      );

      assert.strictEqual(result, null);
      assert.deepStrictEqual(calls, [
        { message: 'no Feature could be parsed', options: { modal: true } },
      ]);
    });

    test('a range-formatting rejection is shown in a modal and resolves to no edits', async () => {
      const calls: { message: string; options: unknown }[] = [];
      const mw = createEditRejectionMiddleware();

      const result = await withErrorMessageStub(calls, () =>
        Promise.resolve(
          mw.provideDocumentRangeFormattingEdits!(doc, range, options, token, () =>
            Promise.reject(new ResponseError(-32803, 'no Feature could be parsed')),
          ),
        ),
      );

      assert.strictEqual(result, null);
      assert.strictEqual(calls.length, 1);
      assert.deepStrictEqual(calls[0].options, { modal: true });
    });

    test('a rejection while the document is being saved (format on save) is not shown', async () => {
      const calls: { message: string; options: unknown }[] = [];
      const tracker = new SaveTracker();
      tracker.begin('file:///a.feature');
      const mw = createEditRejectionMiddleware(tracker);
      const saving = { uri: vscode.Uri.parse('file:///a.feature') } as vscode.TextDocument;

      const result = await withErrorMessageStub(calls, () =>
        Promise.resolve(
          mw.provideDocumentFormattingEdits!(saving, options, token, () =>
            Promise.reject(new ResponseError(-32803, 'no Feature could be parsed')),
          ),
        ),
      );

      assert.strictEqual(result, null, 'the save continues with no edits');
      assert.strictEqual(calls.length, 0, 'no dialog on an automatic trigger');
    });

    test('the same rejection on a document that is not being saved (explicit Format Document) is shown', async () => {
      const calls: { message: string; options: unknown }[] = [];
      const tracker = new SaveTracker();
      tracker.begin('file:///other.feature');
      const mw = createEditRejectionMiddleware(tracker);
      const explicit = { uri: vscode.Uri.parse('file:///a.feature') } as vscode.TextDocument;

      await withErrorMessageStub(calls, () =>
        Promise.resolve(
          mw.provideDocumentFormattingEdits!(explicit, options, token, () =>
            Promise.reject(new ResponseError(-32803, 'no Feature could be parsed')),
          ),
        ),
      );

      assert.strictEqual(calls.length, 1);
    });

    test('a range-format rejection while the document is being saved is not shown', async () => {
      const calls: { message: string; options: unknown }[] = [];
      const tracker = new SaveTracker();
      tracker.begin('file:///a.feature');
      const mw = createEditRejectionMiddleware(tracker);
      const saving = { uri: vscode.Uri.parse('file:///a.feature') } as vscode.TextDocument;

      const result = await withErrorMessageStub(calls, () =>
        Promise.resolve(
          mw.provideDocumentRangeFormattingEdits!(saving, range, options, token, () =>
            Promise.reject(new ResponseError(-32803, 'no Feature could be parsed')),
          ),
        ),
      );

      assert.strictEqual(result, null);
      assert.strictEqual(calls.length, 0);
    });

    test('a successful result, including an empty edit list, is passed through with no dialog', async () => {
      const calls: { message: string; options: unknown }[] = [];
      const mw = createEditRejectionMiddleware();

      const result = await withErrorMessageStub(calls, () =>
        Promise.resolve(mw.provideDocumentFormattingEdits!(doc, options, token, () => [])),
      );

      assert.deepStrictEqual(result, []);
      assert.strictEqual(calls.length, 0);
    });

    test('any other error is rethrown unchanged and shows no dialog', async () => {
      const calls: { message: string; options: unknown }[] = [];
      const mw = createEditRejectionMiddleware();
      const failure = new ResponseError(-32603, 'internal');

      await withErrorMessageStub(calls, async () => {
        await assert.rejects(
          Promise.resolve(
            mw.provideDocumentFormattingEdits!(doc, options, token, () => Promise.reject(failure)),
          ),
          (err) => err === failure,
        );
      });

      assert.strictEqual(calls.length, 0);
    });
  });

  suite('rename middleware (F2 rename)', () => {
    test('a rename rejection is shown in a modal and resolves to no edit', async () => {
      const calls: { message: string; options: unknown }[] = [];
      const mw = createRenameMiddleware(() => undefined);

      const result = await withErrorMessageStub(calls, () =>
        Promise.resolve(
          mw.provideRenameEdits!(
            { version: 1, uri: vscode.Uri.parse('file:///a.feature') } as vscode.TextDocument,
            new vscode.Position(0, 0),
            'new text',
            {} as vscode.CancellationToken,
            () => Promise.reject(new ResponseError(-32803, 'Parameter count mismatch')),
          ),
        ),
      );

      assert.strictEqual(result, null);
      assert.deepStrictEqual(calls, [
        { message: 'Parameter count mismatch', options: { modal: true } },
      ]);
    });
  });
});
