/**
 * Reduces a JavaScript `Error.stack` to a privacy-safe, bounded attribution string, the
 * TypeScript counterpart of the server's `ExceptionStackSanitizer` and so the same
 * `StackFrames` contract: innermost frame first, one frame per line, at most {@link MAX_FRAMES} entries and
 * {@link MAX_LENGTH} characters, and only the extension's *own* code is named.
 *
 * A frame is the extension's own when its file lies under the extension's install directory
 * (passed as `extensionRoot`); it is emitted as `<function>:<line>` — the function name restricted to
 * identifier characters, plus the line in the shipped bundle (resolvable against that version's source map).
 * File paths, file names, columns and the error message/header line are never emitted. Every run of frames
 * from anywhere else (Node, VS Code, other extensions, `node_modules` packages) collapses to one {@link EXTERNAL_PLACEHOLDER}.
 * Without an extension root, or with no own frame, nothing is returned.
 */
export const EXTERNAL_PLACEHOLDER = '[external]';
export const MAX_FRAMES = 8;
export const MAX_LENGTH = 1024;
const MAX_NAME_LENGTH = 64;

// "at fn (file:line:col)", "at async fn (file:line:col)", "at new Foo (file:line:col)", "at file:line:col".
const FRAME = /^\s*at (?:(.+?) \()?(.+?):(\d+):\d+\)?\s*$/;

function normalizePath(path: string): string {
  let result = path.replace(/^file:\/\//i, '').replace(/^\/([A-Za-z]:)/, '$1');
  try {
    result = decodeURIComponent(result);
  } catch {
    // keep the raw text; it is only compared, never emitted
  }
  return result.replace(/\\/g, '/').toLowerCase();
}

function frameName(raw: string | undefined): string {
  if (!raw) return '{anonymous}';
  const name = raw
    .replace(/^async /, '')
    .replace(/^new /, '')
    .replace(/ \[as [^\]]*\]$/, '')
    .replace(/<anonymous>/g, '')
    .replace(/[^A-Za-z0-9_$.]/g, '')
    .replace(/^\.+|\.+$/g, '')
    .slice(0, MAX_NAME_LENGTH);
  return name.length > 0 ? name : '{anonymous}';
}

/** @returns the sanitized stack, or `undefined` when there is no own frame (or no root to recognise one by). */
export function sanitizeStack(
  stack: string | undefined,
  extensionRoot: string | undefined,
): string | undefined {
  try {
    if (!stack || !extensionRoot) return undefined;
    const root = normalizePath(extensionRoot).replace(/\/+$/, '') + '/';
    const entries: string[] = [];
    let sawOwnFrame = false;
    for (const line of stack.split('\n')) {
      if (entries.length >= MAX_FRAMES) break;
      const match = FRAME.exec(line.replace(/\r$/, ''));
      if (!match) continue;
      const file = normalizePath(match[2]);
      if (file.startsWith(root) && !file.includes('/node_modules/')) {
        sawOwnFrame = true;
        entries.push(`${frameName(match[1])}:${match[3]}`);
      } else if (entries[entries.length - 1] !== EXTERNAL_PLACEHOLDER) {
        entries.push(EXTERNAL_PLACEHOLDER);
      }
    }
    if (!sawOwnFrame) return undefined;

    let result = '';
    for (const entry of entries) {
      const next = result.length === 0 ? entry : `${result}\n${entry}`;
      if (next.length > MAX_LENGTH) break;
      result = next;
    }
    return result.length > 0 ? result : undefined;
  } catch {
    return undefined;
  }
}
