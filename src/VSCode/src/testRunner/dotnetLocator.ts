import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

/**
 * Resolves the `dotnet` executable to launch. The extension host's `PATH` isn't guaranteed to
 * include it (issue #452) — most concretely, macOS GUI-launched apps commonly inherit a minimal
 * `PATH` (`/usr/bin:/bin:/usr/sbin:/sbin`) that omits a login-shell-only install. Falls back to
 * `DOTNET_ROOT` and well-known per-OS install locations before giving up and returning the bare
 * command name, which then fails with the OS's normal "not found" error.
 */
export function resolveDotnetExecutable(): string {
  const executableName = os.platform() === 'win32' ? 'dotnet.exe' : 'dotnet';

  if (isOnPath(executableName)) return 'dotnet';

  const dotnetRoot = process.env.DOTNET_ROOT;
  if (dotnetRoot) {
    const candidate = path.join(dotnetRoot, executableName);
    if (fs.existsSync(candidate)) return candidate;
  }

  const wellKnown = wellKnownInstallPaths(executableName).find((candidate) => fs.existsSync(candidate));
  if (wellKnown) return wellKnown;

  return 'dotnet';
}

function isOnPath(executableName: string): boolean {
  const pathEnv = process.env.PATH ?? process.env.Path ?? '';
  return pathEnv
    .split(path.delimiter)
    .some((dir) => dir.length > 0 && fs.existsSync(path.join(dir, executableName)));
}

function wellKnownInstallPaths(executableName: string): string[] {
  switch (os.platform()) {
    case 'win32':
      return [process.env['ProgramFiles'], process.env['ProgramFiles(x86)']]
        .filter((p): p is string => !!p)
        .map((p) => path.join(p, 'dotnet', executableName));
    case 'darwin':
      return ['/usr/local/share/dotnet', '/opt/homebrew/share/dotnet'].map((p) =>
        path.join(p, executableName),
      );
    default:
      return ['/usr/share/dotnet', '/usr/lib/dotnet'].map((p) => path.join(p, executableName));
  }
}
