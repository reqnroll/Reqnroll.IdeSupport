import { execFile } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { logInfo, logWarn } from '../logging/appNotify';
import { isMtpCapable } from './mtpProjectDetection';
import {
  resolveMtpReporterDirectory,
  MTP_REPORTER_BUNDLE_TARGETS_FILE_NAME,
} from './mtpReporterPath';

/**
 * Project-local MTP reporter stubs — issue #741, the VS Code counterpart of the Visual Studio
 * extension's `MtpProjectStubs.cs` and the Rider plugin's `MtpProjectStubs.kt`.
 *
 * For every MTP-capable C# project in the workspace, writes `obj/<Project>.csproj.reqnroll-ide.targets`,
 * a one-line `Import` of the bundled `Reqnroll.IdeSupport.TestReporter.MTP.targets`. MSBuild imports it
 * through `$(MSBuildProjectExtensionsPath)$(MSBuildProjectFile).*.targets` (the mechanism NuGet's own
 * `obj/<Project>.csproj.nuget.g.targets` uses), and the imported file compiles the reporter's sources
 * into that project's own test assembly. So it reaches every build of that project — including the
 * `dotnet test` C# Dev Kit spawns — without depending on environment-variable inheritance (the
 * never-live-verified risk #2 of the previous `CustomAfterMicrosoftCommonTargets` design) and without
 * touching any other project or any global state.
 */

export const STUB_FILE_SUFFIX = '.reqnroll-ide.targets';

const EXCLUDED_DIRECTORY_NAMES = new Set(['bin', 'obj', '.git', '.vs', '.vscode', 'node_modules']);

/** A property that moves obj/ (or the project-extensions path) away from its default; its presence means "ask MSBuild" instead of assuming `obj/`. */
const EXTENSIONS_PATH_OVERRIDE =
  /<\s*(BaseIntermediateOutputPath|MSBuildProjectExtensionsPath|UseArtifactsOutput|ArtifactsPath)\b/i;

const MSBUILD_EVAL_TIMEOUT_MS = 30_000;

/**
 * Enumerates every `.csproj` under `root`, pruning build-output/VCS/editor/dependency directories.
 * Exported for testing.
 */
export function enumerateProjectFiles(root: string): string[] {
  const found: string[] = [];
  const stack = [root];

  while (stack.length > 0) {
    const dir = stack.pop()!;
    let entries: fs.Dirent[];
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      continue;
    }

    for (const entry of entries) {
      if (entry.isDirectory()) {
        if (!EXCLUDED_DIRECTORY_NAMES.has(entry.name)) {
          stack.push(path.join(dir, entry.name));
        }
      } else if (entry.isFile() && entry.name.toLowerCase().endsWith('.csproj')) {
        found.push(path.join(dir, entry.name));
      }
    }
  }

  return found;
}

/** The stub's content: an `Exists`-guarded import of the bundle's `.targets` file and nothing else. Exported for testing. */
export function buildStubXml(bundleTargetsPath: string): string {
  return (
    '<Project>\n' +
    '  <!-- Written by the Reqnroll IDE extension (issue #741): connects this project to the Reqnroll\n' +
    '       Microsoft.Testing.Platform test-outcome reporter. Project-local and inert when the extension\n' +
    '       is not installed. Opt out with <ReqnrollIdeSupportDisableMtpReporter>true</ReqnrollIdeSupportDisableMtpReporter>. -->\n' +
    `  <Import Project="${bundleTargetsPath}" Condition="Exists('${bundleTargetsPath}')" />\n` +
    '</Project>\n'
  );
}

/**
 * The directory MSBuild imports `$(MSBuildProjectFile).*.targets` from. Fast path: `<project dir>/obj`,
 * unless the project file or a `Directory.Build.props` above it mentions a property that moves it —
 * then `evaluate` (a real MSBuild evaluation) decides, and `undefined` means "unknown, don't write".
 * Exported for testing.
 */
export async function resolveProjectExtensionsDirectory(
  projectFile: string,
  readTextOrNull: (p: string) => string | undefined = readTextOrNullFs,
  evaluate: (projectFile: string) => Promise<string | undefined> = evaluateViaMsBuild,
): Promise<string | undefined> {
  const projectDirectory = path.dirname(projectFile);

  let moved = EXTENSIONS_PATH_OVERRIDE.test(readTextOrNull(projectFile) ?? '');
  for (let dir = projectDirectory; !moved;) {
    moved = EXTENSIONS_PATH_OVERRIDE.test(
      readTextOrNull(path.join(dir, 'Directory.Build.props')) ?? '',
    );
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  if (!moved) return path.join(projectDirectory, 'obj');

  const evaluated = (await evaluate(projectFile))?.trim();
  if (!evaluated) return undefined;
  return path.isAbsolute(evaluated) ? evaluated : path.resolve(projectDirectory, evaluated);
}

/** The stub path for `projectFile` inside `directory`. */
export function stubPathFor(directory: string, projectFile: string): string {
  return path.join(directory, path.basename(projectFile) + STUB_FILE_SUFFIX);
}

/**
 * Writes (or refreshes) the stub for one project; leaves an up-to-date stub untouched so its timestamp
 * doesn't disturb incremental builds. Resolves to the stub path, or `undefined` when skipped/failed —
 * never throws. Exported for testing.
 */
export async function writeStub(
  projectFile: string,
  bundleTargetsPath: string,
  readTextOrNull: (p: string) => string | undefined = readTextOrNullFs,
  evaluate: (projectFile: string) => Promise<string | undefined> = evaluateViaMsBuild,
): Promise<string | undefined> {
  try {
    if (!projectFile.toLowerCase().endsWith('.csproj')) return undefined;
    const directory = await resolveProjectExtensionsDirectory(
      projectFile,
      readTextOrNull,
      evaluate,
    );
    if (!directory) {
      logWarn(
        `testOutcomes: could not determine MSBuildProjectExtensionsPath for '${projectFile}'; no MTP reporter stub written.`,
      );
      return undefined;
    }

    const stub = stubPathFor(directory, projectFile);
    const xml = buildStubXml(bundleTargetsPath);
    if (readTextOrNullFs(stub) === xml) return stub;

    fs.mkdirSync(directory, { recursive: true });
    fs.writeFileSync(stub, xml, 'utf8');
    return stub;
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    logWarn(`testOutcomes: could not write the MTP reporter stub for '${projectFile}' — ${msg}`);
    return undefined;
  }
}

/** Deletes the stub for `projectFile` if present (feature turned off). Never throws. Exported for testing. */
export async function removeStub(
  projectFile: string,
  readTextOrNull: (p: string) => string | undefined = readTextOrNullFs,
  evaluate: (projectFile: string) => Promise<string | undefined> = evaluateViaMsBuild,
): Promise<boolean> {
  try {
    const directory = await resolveProjectExtensionsDirectory(
      projectFile,
      readTextOrNull,
      evaluate,
    );
    if (!directory) return false;
    const stub = stubPathFor(directory, projectFile);
    if (!fs.existsSync(stub)) return false;
    fs.rmSync(stub, { force: true });
    return true;
  } catch {
    return false;
  }
}

/**
 * Writes a stub for every MTP-capable C# project under `workspaceFolderPaths` (only those — no file is
 * written into a project that can't use it). Resolves to the number of stubs in place; never throws.
 */
export async function writeStubsForWorkspace(
  workspaceFolderPaths: readonly string[],
  bundleTargetsPath: string,
  isCapable: (projectFile: string) => Promise<boolean> = isMtpCapable,
): Promise<number> {
  try {
    if (!fs.existsSync(bundleTargetsPath)) {
      logWarn(
        `testOutcomes: bundled MTP reporter '${bundleTargetsPath}' not found; no MTP reporter stubs written.`,
      );
      return 0;
    }

    const projectFiles = workspaceFolderPaths.flatMap((folder) => enumerateProjectFiles(folder));
    const capable = await Promise.all(projectFiles.map((projectFile) => isCapable(projectFile)));
    const targets = projectFiles.filter((_, i) => capable[i]);
    const written = await Promise.all(
      targets.map((projectFile) => writeStub(projectFile, bundleTargetsPath)),
    );
    const count = written.filter((s) => s !== undefined).length;
    logInfo(
      `testOutcomes: MTP reporter stubs in place for ${count} of ${targets.length} MTP-capable project(s).`,
    );
    return count;
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    logWarn(`testOutcomes: writing MTP reporter stubs failed — ${msg}`);
    return 0;
  }
}

/**
 * Extension-activation entry point, gated on the same `reqnroll.testOutcomes.enabled` opt-in as
 * `activateTestOutcomes`. When enabled, writes the workspace's stubs; when disabled, removes any this
 * extension wrote earlier, so turning the feature off leaves no file of ours behind. Never throws.
 */
export async function activateMtpProjectStubs(
  context: Pick<vscode.ExtensionContext, 'extensionMode' | 'extensionPath'>,
): Promise<void> {
  const workspaceFolderPaths = (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath);
  if (workspaceFolderPaths.length === 0) return;

  const enabled = vscode.workspace
    .getConfiguration('reqnroll')
    .get<boolean>('testOutcomes.enabled', false);
  if (!enabled) {
    const projectFiles = workspaceFolderPaths.flatMap((folder) => enumerateProjectFiles(folder));
    await Promise.all(projectFiles.map((projectFile) => removeStub(projectFile)));
    return;
  }

  const reporterDirectory = resolveMtpReporterDirectory(context);
  if (!reporterDirectory) {
    logWarn(
      'testOutcomes: reqnroll.testOutcomes.enabled is true, but the bundled MTP reporter was not found; no MTP reporter stubs written.',
    );
    return;
  }

  await writeStubsForWorkspace(
    workspaceFolderPaths,
    path.join(reporterDirectory, MTP_REPORTER_BUNDLE_TARGETS_FILE_NAME),
  );
}

/** `dotnet msbuild <project> -getProperty:MSBuildProjectExtensionsPath` (a single property prints the bare value); `undefined` on any failure. */
export function evaluateViaMsBuild(projectFile: string): Promise<string | undefined> {
  return new Promise((resolve) => {
    const child = execFile(
      'dotnet',
      ['msbuild', projectFile, '-getProperty:MSBuildProjectExtensionsPath', '-nologo'],
      { timeout: MSBUILD_EVAL_TIMEOUT_MS, maxBuffer: 1024 * 1024 },
      (error, stdout) => resolve(error ? undefined : stdout.trim() || undefined),
    );
    child.on('error', () => {
      /* handled in callback */
    });
  });
}

function readTextOrNullFs(filePath: string): string | undefined {
  try {
    return fs.existsSync(filePath) ? fs.readFileSync(filePath, 'utf8') : undefined;
  } catch {
    return undefined;
  }
}
