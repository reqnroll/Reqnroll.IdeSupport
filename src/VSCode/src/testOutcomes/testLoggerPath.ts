import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

/** File name of the bundled logger, matching TestLoggerRunSettings.LoggerAssemblyFileName on every IDE side. */
export const TEST_LOGGER_ASSEMBLY_FILE_NAME = 'Reqnroll.IdeSupport.TestLogger.dll';

/**
 * Resolves the directory containing the bundled `Reqnroll.IdeSupport.TestLogger.dll`
 * (LSP-server outcome pipeline, #700/#702) — the same VSTest logger the Visual Studio
 * extension and the Rider plugin each bundle under their own `TestLogger`/`testlogger`
 * subdirectory, packaged here under `testlogger/` instead.
 *
 * Unlike {@link resolveServerPath} in `extension.ts`, there is no per-OS/arch RID to
 * resolve: the logger targets netstandard2.0 with no self-contained runtime and loads
 * inside whichever `dotnet test`/vstest process is already running, on any OS — one
 * build serves every platform.
 *
 * Returns `undefined` (never throws) when the logger isn't bundled — a missing logger
 * degrades to "no server-sourced outcomes for this session," never a hard failure, the
 * same convention `ReqnrollServerPathResolver`'s counterparts use on VS/Rider.
 *
 * `existsSync` is injectable (defaulting to the real `fs.existsSync`) so this stays
 * testable against a fake filesystem without touching disk, mirroring `resolveServerPath`.
 */
export function resolveTestLoggerDirectory(
  context: Pick<vscode.ExtensionContext, 'extensionMode' | 'extensionPath'>,
  existsSync: (path: string) => boolean = fs.existsSync,
): string | undefined {
  const isProduction = context.extensionMode === vscode.ExtensionMode.Production;

  const candidate = isProduction
    ? path.join(context.extensionPath, 'testlogger')
    : path.join(
        context.extensionPath,
        '..',
        '..',
        'src',
        'Core',
        'Reqnroll.IdeSupport.TestLogger',
        'bin',
        'Release',
        'netstandard2.0',
      );

  return existsSync(path.join(candidate, TEST_LOGGER_ASSEMBLY_FILE_NAME)) ? candidate : undefined;
}
