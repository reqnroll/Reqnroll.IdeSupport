import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

/** File name of the bundle's MSBuild entry point, matching MtpReporterPathResolver.BundleTargetsFileName on the VS/Rider sides. */
export const MTP_REPORTER_BUNDLE_TARGETS_FILE_NAME = 'Reqnroll.IdeSupport.TestReporter.MTP.targets';

/** Directory of reporter sources the `.targets` file compiles into a user's test project. */
export const MTP_REPORTER_SOURCE_DIRECTORY_NAME = 'ReporterSource';

/**
 * Resolves the directory containing the bundled MTP reporter *source bundle* (issue #741) —
 * `Reqnroll.IdeSupport.TestReporter.MTP.targets` plus `ReporterSource/` — the same bundle the
 * Visual Studio extension (`MtpReporter/`) and the Rider plugin (`mtpreporter/`) ship, packaged here
 * under `mtpreporter/` too. Mirrors {@link resolveTestLoggerDirectory}'s production/development split;
 * in development the reporter project's own `bin/Release/net8.0` output carries the bundle (both are
 * Content items of that project).
 *
 * Returns `undefined` (never throws) unless both the `.targets` file and `ReporterSource/` exist —
 * degrades to "no MTP reporter stubs this session," never a hard failure.
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

  return existsSync(path.join(candidate, MTP_REPORTER_BUNDLE_TARGETS_FILE_NAME)) &&
    existsSync(path.join(candidate, MTP_REPORTER_SOURCE_DIRECTORY_NAME))
    ? candidate
    : undefined;
}
