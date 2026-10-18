import * as vscode from 'vscode';
import { Middleware, ResponseError } from 'vscode-languageclient/node';
import { logWarn, showEditRejected } from '../logging/appNotify';
import { SaveTracker, saveTracker } from './saveTracker';

/**
 * LSP `RequestFailed`: the server's code for an edit it refuses for a reason the user can fix
 * (issue #1132). Its message is meant to be shown verbatim; every other error code is an
 * unexpected failure (log it, show at most a generic message), and `ContentModified` (-32801) is a
 * routine retryable cancellation that must never reach a dialog.
 */
export const EDIT_REJECTED_CODE = -32803;

/**
 * If `err` is the server's "edit rejected" error — a `ResponseError` with code
 * {@link EDIT_REJECTED_CODE} and a non-blank message — returns that message; otherwise
 * `undefined`.
 */
export function asEditRejection(err: unknown): string | undefined {
  if (!(err instanceof ResponseError) || err.code !== EDIT_REJECTED_CODE) return undefined;
  return err.message.trim().length > 0 ? err.message : undefined;
}

/**
 * Middleware for the standard edit-producing requests `vscode-languageclient` issues itself
 * (Format Document / Format Selection): a rejection (e.g. "the document cannot be formatted
 * because no Feature could be parsed") is shown in a modal, and the request resolves to "no
 * edits" so the platform does not also show its own error toast. Any other error is rethrown
 * unchanged, so the client's default handling (logging, the generic toast) still applies.
 * An empty edit list from the server — "already formatted" — never reaches the catch and stays
 * silent.
 *
 * **Implicit triggers never get a modal.** A rejection that arrives while the document is being
 * saved (`editor.formatOnSave`, see {@link SaveTracker}) is only logged: the user did not ask for a
 * format, and a half-typed file would otherwise raise a dialog on every save. Format-on-type goes
 * through `textDocument/onTypeFormatting`, which neither this middleware nor the server ever
 * rejects. (Format-on-paste arrives as a range format and cannot be told apart from Format
 * Selection, so it is treated as explicit.)
 */
export function createEditRejectionMiddleware(tracker: SaveTracker = saveTracker): Middleware {
  return {
    provideDocumentFormattingEdits: async (document, options, token, next) => {
      try {
        return await next(document, options, token);
      } catch (err) {
        return rejectedOrRethrow<vscode.TextEdit[]>(err, tracker.isSaving(document.uri.toString()));
      }
    },
    provideDocumentRangeFormattingEdits: async (document, range, options, token, next) => {
      try {
        return await next(document, range, options, token);
      } catch (err) {
        return rejectedOrRethrow<vscode.TextEdit[]>(err, tracker.isSaving(document.uri.toString()));
      }
    },
  };
}

/**
 * Shows `err` as a rejection and returns `null` ("no edits") when it is one, otherwise rethrows.
 * Shared with the rename middleware so every standard request treats rejections the same way.
 * When `implicit` (an automatic trigger such as format-on-save) the rejection is only logged, never
 * shown.
 */
export function rejectedOrRethrow<T>(err: unknown, implicit = false): T | null {
  const rejection = asEditRejection(err);
  if (rejection === undefined) throw err;
  if (implicit) {
    logWarn(`Format skipped on an automatic trigger: ${rejection}`);
  } else {
    void showEditRejected(rejection);
  }
  return null;
}
