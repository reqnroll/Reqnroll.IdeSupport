import { execFile } from 'child_process';

/**
 * Upper bound for a single `dotnet msbuild` evaluation's stdout (issue #1008).
 *
 * `evaluateProject` and the MTP-capability probes pipe the whole `-getItem`/`-getProperty` JSON
 * through `execFile`. That JSON lists every Compile/None/Content item with resolved metadata, so it
 * grows with project size; a large project can exceed the old 1 MB limit. When it does, Node kills
 * the child with `ERR_CHILD_PROCESS_STDIO_MAXBUFFER` and the caller loses the evaluation — silently,
 * before this change. Raised to a value no realistic single-project evaluation reaches while still
 * bounding memory for a runaway child.
 */
export const MSBUILD_MAX_BUFFER_BYTES = 64 * 1024 * 1024;

/** {@link MSBUILD_MAX_BUFFER_BYTES} in whole MB, for user-facing warning text. */
export const MSBUILD_MAX_BUFFER_MB = MSBUILD_MAX_BUFFER_BYTES / (1024 * 1024);

/** Node's error code for a child process whose stdout exceeded the configured `maxBuffer`. */
export const MAX_BUFFER_OVERFLOW_CODE = 'ERR_CHILD_PROCESS_STDIO_MAXBUFFER';

/** True when `error` is Node's maxBuffer-overflow error (issue #1008). */
export function isMaxBufferOverflowError(error: unknown): boolean {
  return (error as { code?: unknown } | null | undefined)?.code === MAX_BUFFER_OVERFLOW_CODE;
}

/** The outcome of one `dotnet msbuild` invocation. */
export interface MsbuildExecResult {
  /** The child's stdout; empty when the invocation failed. */
  readonly stdout: string;
  /** The spawn/exit error, or null on success. */
  readonly error: Error | null;
}

/** Options for {@link MsbuildExec}. */
export interface MsbuildExecOptions {
  readonly timeoutMs: number;
  readonly env?: NodeJS.ProcessEnv;
}

/**
 * Runs one `dotnet msbuild` invocation. Injected into the evaluators so their failure handling
 * (including the issue #1008 buffer-overflow warning) is unit-testable without spawning a real
 * child process. Never rejects — a spawn failure (e.g. `dotnet` not on PATH) lands in `error`.
 */
export type MsbuildExec = (
  args: readonly string[],
  options: MsbuildExecOptions,
) => Promise<MsbuildExecResult>;

/** The production {@link MsbuildExec}: `execFile('dotnet', args, { timeout, maxBuffer, env })`. */
export const execDotnetMsbuild: MsbuildExec = (args, { timeoutMs, env }) =>
  new Promise((resolve) => {
    const child = execFile(
      'dotnet',
      [...args],
      { timeout: timeoutMs, maxBuffer: MSBUILD_MAX_BUFFER_BYTES, env, encoding: 'utf8' },
      (error, stdout) => resolve({ stdout, error }),
    );

    // Suppress the unhandled 'error' event on spawn failure (e.g. dotnet not on PATH) — the exec
    // callback's `error` parameter already carries it.
    child.on('error', () => {
      /* handled in callback */
    });
  });
