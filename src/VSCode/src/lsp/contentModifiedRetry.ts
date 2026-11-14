import { ResponseError } from 'vscode-languageclient/node';

/**
 * LSP `ContentModified`: the server abandoned a request because the document changed while it
 * was being computed. It is a routine, retryable cancellation, not a failure.
 */
export const CONTENT_MODIFIED_CODE = -32801;

const DEFAULT_RETRIES = 3;
const DEFAULT_DELAY_MS = 100;

/** True when `err` is an LSP `ResponseError` carrying {@link CONTENT_MODIFIED_CODE}. */
export function isContentModified(err: unknown): boolean {
  return err instanceof ResponseError && err.code === CONTENT_MODIFIED_CODE;
}

/**
 * Runs `send`, re-running it after a short delay while it rejects with `ContentModified`, up to
 * `retries` extra attempts. Any other rejection — and a `ContentModified` that survives every
 * retry — propagates to the caller unchanged.
 *
 * For requests issued by providers registered directly with `vscode.languages.*`:
 * `vscode-languageclient` only retries `ContentModified` for the providers it registers itself.
 */
export async function sendWithContentModifiedRetry<T>(
  send: () => Promise<T>,
  options?: { retries?: number; delayMs?: number },
): Promise<T> {
  const retries = options?.retries ?? DEFAULT_RETRIES;
  const delayMs = options?.delayMs ?? DEFAULT_DELAY_MS;
  for (let attempt = 0; ; attempt++) {
    try {
      return await send();
    } catch (err) {
      if (!isContentModified(err) || attempt >= retries) throw err;
      await new Promise<void>((resolve) => setTimeout(resolve, delayMs));
    }
  }
}
