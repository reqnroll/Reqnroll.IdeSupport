import { execFile } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { resolveDotnetExecutable } from './dotnetLocator';
import { parseTrx, TrxUnitTestResult } from './trxParser';

/** Structural subset of `vscode.CancellationToken` — avoids an unnecessary `vscode` import here (mirrors `msbuildEvaluator.ts`'s vscode-free style). */
export interface CancellationSignal {
  readonly isCancellationRequested: boolean;
  onCancellationRequested(listener: () => void): void;
}

export interface DotnetTestRunResult {
  readonly results: TrxUnitTestResult[];
}

/**
 * Returned in place of `null` when the run couldn't even be launched with a message specific
 * enough to act on (e.g. `dotnet` unresolvable, issue #452) rather than the generic
 * "failed to run" shown for every other launch failure.
 */
export interface DotnetTestLaunchFailure {
  readonly error: string;
}

const TEST_TIMEOUT_MS = 120_000;

/**
 * Runs `dotnet test <projectFile> --filter <filterExpr>`, capturing results via a TRX logger
 * (design doc §6: TRX's `<Output><StdOut>` carries Reqnroll's own step trace, already present by
 * default). Returns `null` when the run itself couldn't be started or completed (`dotnet`
 * unavailable, timeout, cancellation, no TRX produced) — a non-zero `dotnet test` exit code from
 * failing tests is NOT treated as a run failure, only the absence of a TRX file is (the presence of
 * the TRX file is the actual signal that the run completed, whatever the individual test outcomes).
 * Follows `msbuildEvaluator.ts`'s `execFile` error-handling shape: never throws.
 */
export async function runDotnetTest(
  projectFile: string,
  filterExpr: string,
  cancellationToken?: CancellationSignal,
): Promise<DotnetTestRunResult | DotnetTestLaunchFailure | null> {
  const resultsDir = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'reqnroll-test-'));
  const trxFileName = 'result.trx';
  const trxPath = path.join(resultsDir, trxFileName);

  try {
    const args = [
      'test',
      projectFile,
      '--filter',
      filterExpr,
      '--logger',
      `trx;LogFileName=${trxFileName}`,
      '--results-directory',
      resultsDir,
      '--nologo',
    ];

    type SpawnResult = { ok: true } | { ok: false; error?: NodeJS.ErrnoException };

    const spawnResult = await new Promise<SpawnResult>((resolve) => {
      const child = execFile(
        resolveDotnetExecutable(),
        args,
        {
          timeout: TEST_TIMEOUT_MS,
          maxBuffer: 10 * 1024 * 1024,
          env: { ...process.env, MSYS_NO_PATHCONV: '1' },
        },
        (error) => {
          // dotnet test exits non-zero when any test fails — expected, not a run failure.
          // Whether the run actually produced results is checked via the TRX file afterward.
          // Still log unexpected launch-time errors (spawn failure, timeout kill, maxBuffer
          // overflow) for diagnostics — these were previously silently dropped since the
          // callback ignored its `error` parameter entirely.
          if (error) {
            console.error(
              `dotnetTestRunner: dotnet test reported an error for ${projectFile}:`,
              error.message,
            );
          }
          resolve({ ok: true });
        },
      );

      child.on('error', (err: NodeJS.ErrnoException) => {
        console.error(`dotnetTestRunner: failed to launch dotnet test for ${projectFile}:`, err);
        resolve({ ok: false, error: err });
      });

      if (cancellationToken) {
        if (cancellationToken.isCancellationRequested) {
          child.kill();
          resolve({ ok: false });
        } else {
          cancellationToken.onCancellationRequested(() => {
            child.kill();
            resolve({ ok: false });
          });
        }
      }
    });

    if (!spawnResult.ok) {
      if (spawnResult.error?.code === 'ENOENT') {
        return {
          error: `Could not launch 'dotnet' for ${projectFile} — the dotnet CLI was not found on PATH, DOTNET_ROOT, or common install locations. Ensure the .NET SDK is installed and accessible to VS Code, then retry.`,
        };
      }
      return null;
    }

    if (!fs.existsSync(trxPath)) return null;

    const trxXml = await fs.promises.readFile(trxPath, 'utf-8');
    return { results: parseTrx(trxXml) };
  } catch (err) {
    console.error(`dotnetTestRunner: run failed for ${projectFile}:`, err);
    return null;
  } finally {
    await fs.promises.rm(resultsDir, { recursive: true, force: true }).catch(() => undefined);
  }
}
