import * as vscode from 'vscode';
import { Middleware, ResponseError } from 'vscode-languageclient/node';
import { showEditRejected } from '../logging/appNotify';

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
 */
export function createEditRejectionMiddleware(): Middleware {
  return {
    provideDocumentFormattingEdits: async (document, options, token, next) => {
      try {
        return await next(document, options, token);
      } catch (err) {
        return rejectedOrRethrow<vscode.TextEdit[]>(err);
      }
    },
    provideDocumentRangeFormattingEdits: async (document, range, options, token, next) => {
      try {
        return await next(document, range, options, token);
      } catch (err) {
        return rejectedOrRethrow<vscode.TextEdit[]>(err);
      }
    },
  };
}

/**
 * Shows `err` as a rejection and returns `null` ("no edits") when it is one, otherwise rethrows.
 * Shared with the rename middleware so every standard request treats rejections the same way.
 */
export function rejectedOrRethrow<T>(err: unknown): T | null {
  const rejection = asEditRejection(err);
  if (rejection === undefined) throw err;
  void showEditRejected(rejection);
  return null;
}
