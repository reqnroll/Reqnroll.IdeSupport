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
 * For every MTP-capable C# project in the workspace that uses Reqnroll (see {@link detectReqnrollUsage}),
 * writes `obj/<Project>.csproj.reqnroll-ide.targets`,
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

/** NuGet's restore output, written to the same directory as the stub. */
export const ASSETS_FILE_NAME = 'project.assets.json';

/** A `"Name/Version":` key of project.assets.json's `targets` and `libraries` sections. */
const ASSETS_LIBRARY_KEY = /"([^"/\\]+)\/\d[^"/]*"\s*:/g;

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

/**
 * MSBuild escaping (`%` first) then XML text escaping. The extension's install path contains the user
 * name; a name such as O'Brien, or a path with `&` or `$`, must not produce a stub that breaks every
 * build. Exported for testing.
 */
export function escapeForMsbuildXml(value: string): string {
  return value
    .replace(/%/g, '%25')
    .replace(/\$/g, '%24')
    .replace(/@/g, '%40')
    .replace(/;/g, '%3B')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

/**
 * The stub's content: the bundle path in a property (escaped, and never inside a quoted condition
 * literal), imported through that property, and nothing else. Exported for testing.
 */
export function buildStubXml(bundleTargetsPath: string): string {
  return (
    '<Project>\n' +
    '  <!-- Written by the Reqnroll IDE extension (issue #741): connects this project to the Reqnroll\n' +
    '       Microsoft.Testing.Platform test-outcome reporter. Project-local and inert when the extension\n' +
    '       is not installed. Opt out with <ReqnrollIdeSupportDisableMtpReporter>true</ReqnrollIdeSupportDisableMtpReporter>. -->\n' +
    '  <PropertyGroup>\n' +
    `    <_ReqnrollIdeMtpReporterBundle>${escapeForMsbuildXml(bundleTargetsPath)}</_ReqnrollIdeMtpReporterBundle>\n` +
    '  </PropertyGroup>\n' +
    `  <Import Project="$(_ReqnrollIdeMtpReporterBundle)" Condition="Exists('$(_ReqnrollIdeMtpReporterBundle)')" />\n` +
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
 * Whether a project's `project.assets.json` text lists a package or project reference whose name
 * contains "Reqnroll" (case-insensitive) anywhere in its restore graph — the name rule the LSP server's
 * `ReqnrollProjectDetector` uses, over transitive references too, so a project that gets Reqnroll
 * through an in-house meta-package qualifies. `undefined` when there is no restore output yet, so the
 * answer is unknown. Mirrors `MtpProjectStubs.DetectReqnrollUsage` in the Visual Studio extension.
 * Exported for testing.
 */
export function detectReqnrollUsage(projectAssetsJson: string | undefined): boolean | undefined {
  if (projectAssetsJson === undefined) return undefined;
  for (const match of projectAssetsJson.matchAll(ASSETS_LIBRARY_KEY)) {
    if (match[1].toLowerCase().includes('reqnroll')) return true;
  }
  return false;
}

export type StubSyncResult =
  'written' | 'notReqnroll' | 'notMtpCapable' | 'notRestored' | 'skipped';

/**
 * Brings one project's stub in line with whether it uses Reqnroll: written or refreshed for an
 * MTP-capable Reqnroll project, removed from a restored project that does not use Reqnroll, untouched
 * while the project has no restore output yet. The cheap Reqnroll check runs first, so `isCapable`
 * (which can shell out to `dotnet msbuild`, issue #722) runs only for Reqnroll projects. Never throws.
 * Exported for testing.
 */
export async function syncStub(
  projectFile: string,
  bundleTargetsPath: string,
  isCapable: (projectFile: string) => Promise<boolean> = isMtpCapable,
  readTextOrNull: (p: string) => string | undefined = readTextOrNullFs,
  evaluate: (projectFile: string) => Promise<string | undefined> = evaluateViaMsBuild,
): Promise<StubSyncResult> {
  try {
    if (!projectFile.toLowerCase().endsWith('.csproj')) return 'skipped';
    const directory = await resolveProjectExtensionsDirectory(
      projectFile,
      readTextOrNull,
      evaluate,
    );
    if (!directory) return 'skipped';

    switch (detectReqnrollUsage(readTextOrNull(path.join(directory, ASSETS_FILE_NAME)))) {
      case true:
        if (!(await isCapable(projectFile))) return 'notMtpCapable';
        return (await writeStub(projectFile, bundleTargetsPath, readTextOrNull, () =>
          Promise.resolve(directory),
        )) === undefined
          ? 'skipped'
          : 'written';
      case false: {
        const stub = stubPathFor(directory, projectFile);
        if (fs.existsSync(stub)) {
          fs.rmSync(stub, { force: true });
          logInfo(
            `testOutcomes: removed the MTP reporter stub ${stub}; the project does not use Reqnroll.`,
          );
        }
        return 'notReqnroll';
      }
      default:
        return 'notRestored';
    }
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    logWarn(`testOutcomes: could not update the MTP reporter stub for '${projectFile}' — ${msg}`);
    return 'skipped';
  }
}

/**
 * Syncs the stub ({@link syncStub}) of every C# project among `projectFiles`: only a project that uses
 * Reqnroll and can use the reporter gets a file of ours. Resolves to the number of stubs in place;
 * never throws.
 */
export async function syncStubsForProjects(
  projectFiles: readonly string[],
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

    const results = await Promise.all(
      projectFiles.map((projectFile) => syncStub(projectFile, bundleTargetsPath, isCapable)),
    );
    const count = results.filter((r) => r === 'written').length;
    logInfo(
      `testOutcomes: MTP reporter stubs in place for ${count} MTP-capable Reqnroll project(s) of ${projectFiles.length} project(s).`,
    );
    return count;
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    logWarn(`testOutcomes: writing MTP reporter stubs failed — ${msg}`);
    return 0;
  }
}

/** {@link syncStubsForProjects} for every C# project under `workspaceFolderPaths`. */
export function syncStubsForWorkspace(
  workspaceFolderPaths: readonly string[],
  bundleTargetsPath: string,
  isCapable: (projectFile: string) => Promise<boolean> = isMtpCapable,
): Promise<number> {
  return syncStubsForProjects(
    workspaceFolderPaths.flatMap((folder) => enumerateProjectFiles(folder)),
    bundleTargetsPath,
    isCapable,
  );
}

/**
 * The C# projects whose default `obj/` holds `assetsFile` — the projects in its parent's parent
 * directory. Exported for testing.
 */
export function projectsForAssetsFile(assetsFile: string): string[] {
  const projectDirectory = path.dirname(path.dirname(assetsFile));
  try {
    return fs
      .readdirSync(projectDirectory, { withFileTypes: true })
      .filter((e) => e.isFile() && e.name.toLowerCase().endsWith('.csproj'))
      .map((e) => path.join(projectDirectory, e.name));
  } catch {
    return [];
  }
}

/**
 * Extension-activation entry point, gated on the same `reqnroll.testOutcomes.enabled` opt-in as
 * `activateTestOutcomes`. When enabled, syncs the workspace's stubs, then re-syncs a project each time
 * NuGet writes its `obj/project.assets.json` — when a freshly cloned project is first restored (and
 * its Reqnroll use becomes known), or a Reqnroll package is added or removed. When disabled, removes any
 * stub this extension wrote earlier, so turning the feature off leaves no file of ours behind. Never throws.
 */
export async function activateMtpProjectStubs(
  context: Pick<vscode.ExtensionContext, 'extensionMode' | 'extensionPath' | 'subscriptions'>,
): Promise<void> {
  const workspaceFolderPaths = (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath);
  if (workspaceFolderPaths.length === 0) return;

  const enabled = vscode.workspace
    .getConfiguration('reqnroll')
    .get<boolean>('testOutcomes.enabled', false);
  if (!enabled) {
    // Cleanup must stay cheap for the (default) disabled case: only the obj/ fast path, never a
    // `dotnet msbuild` evaluation per project that moves its obj/.
    const projectFiles = workspaceFolderPaths.flatMap((folder) => enumerateProjectFiles(folder));
    await Promise.all(
      projectFiles.map((projectFile) =>
        removeStub(projectFile, undefined, () => Promise.resolve(undefined)),
      ),
    );
    return;
  }

  const reporterDirectory = resolveMtpReporterDirectory(context);
  if (!reporterDirectory) {
    logWarn(
      'testOutcomes: reqnroll.testOutcomes.enabled is true, but the bundled MTP reporter was not found; no MTP reporter stubs written.',
    );
    return;
  }

  const bundleTargetsPath = path.join(reporterDirectory, MTP_REPORTER_BUNDLE_TARGETS_FILE_NAME);
  await syncStubsForWorkspace(workspaceFolderPaths, bundleTargetsPath);

  // Best effort: a files.watcherExclude covering obj/ suppresses these events, and a project that
  // moves its obj/ is not watched; either then gets its stub at the next activation.
  const restoreWatcher = vscode.workspace.createFileSystemWatcher(`**/obj/${ASSETS_FILE_NAME}`);
  const onRestored = (uri: vscode.Uri): void => {
    void syncStubsForProjects(projectsForAssetsFile(uri.fsPath), bundleTargetsPath);
  };
  restoreWatcher.onDidCreate(onRestored);
  restoreWatcher.onDidChange(onRestored);
  context.subscriptions.push(restoreWatcher);
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
