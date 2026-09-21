import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

/** File name of the bundled MTP reporter, matching MtpReporterPathResolver.ReporterAssemblyFileName on the VS/Rider sides. */
export const MTP_REPORTER_ASSEMBLY_FILE_NAME = 'Reqnroll.IdeSupport.TestReporter.MTP.dll';

/**
 * Resolves the directory containing the bundled `Reqnroll.IdeSupport.TestReporter.MTP.dll`
 * (LSP-server outcome pipeline, issue #715 phase 4) — the same MTP in-process reporter the
 * Visual Studio extension (`MtpReporter/`) and the Rider plugin (`mtpreporter/`) each bundle,
 * packaged here under `mtpreporter/` too. Mirrors {@link resolveTestLoggerDirectory} exactly;
 * see that function's own doc comment for the production/development split and the
 * injectable-`existsSync` testing rationale.
 *
 * Returns `undefined` (never throws) when the reporter isn't bundled — degrades to "no ephemeral
 * MTP injection this session," never a hard failure, same convention as the VSTest logger path.
 */
export function resolveMtpReporterDirectory(
  context: Pick<vscode.ExtensionContext, 'extensionMode' | 'extensionPath'>,
  existsSync: (path: string) => boolean = fs.existsSync,
): string | undefined {
  const isProduction = context.extensionMode === vscode.ExtensionMode.Production;

  const candidate = isProduction
    ? path.join(context.extensionPath, 'mtpreporter')
    : path.join(
        context.extensionPath,
        '..',
        '..',
        'src',
        'Core',
        'Reqnroll.IdeSupport.TestReporter.MTP',
        'bin',
        'Release',
        'net8.0',
      );

  return existsSync(path.join(candidate, MTP_REPORTER_ASSEMBLY_FILE_NAME)) ? candidate : undefined;
}
