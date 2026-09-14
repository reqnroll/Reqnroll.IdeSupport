import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

/**
 * Resolves the per-OS Reqnroll log directory, shared by every file-logging sink in this
 * extension (issue #625/#626):
 *   Windows : %LOCALAPPDATA%\Reqnroll
 *   macOS   : ~/Library/Logs/Reqnroll
 *   Linux   : ~/.local/share/Reqnroll
 * Mirrors the Rider plugin's `ReqnrollDebugLogger.logDirectory` and the .NET side's
 * `ReqnrollLogPaths.ResolveLogDirectory`, so a support engineer collecting logs from all three
 * IDEs plus the LSP server finds them in one place per OS. Moved here from `lspInspectorLogger.ts`
 * (its original, private home) so `generalFileLog.ts` can share it instead of re-deriving it.
 */
export function resolveLogDirectory(): string {
  switch (process.platform) {
    case 'win32':
      return path.join(process.env['LOCALAPPDATA'] ?? os.homedir(), 'Reqnroll');
    case 'darwin':
      return path.join(os.homedir(), 'Library', 'Logs', 'Reqnroll');
    default:
      return path.join(os.homedir(), '.local', 'share', 'Reqnroll');
  }
}

const MAX_AGE_MS = 10 * 24 * 60 * 60 * 1000;

/**
 * Deletes `reqnroll-*` files older than 10 days from `logDirectory`, matching the retention
 * policy `ReqnrollLogPaths.PruneOldLogFiles` (.NET) and `ReqnrollDebugLogger.pruneOldLogs`
 * (Rider) already apply to their own log files.
 */
export function pruneOldLogs(logDirectory: string): void {
  try {
    const cutoff = Date.now() - MAX_AGE_MS;
    for (const name of fs.readdirSync(logDirectory)) {
      if (!name.startsWith('reqnroll-')) continue;
      const fullPath = path.join(logDirectory, name);
      try {
        if (fs.statSync(fullPath).mtimeMs < cutoff) fs.unlinkSync(fullPath);
      } catch {
        // Best-effort per-file — one unreadable/locked file shouldn't stop the sweep.
      }
    }
  } catch {
    // Best-effort — pruning must never break logging (e.g. the directory doesn't exist yet).
  }
}
