import * as crypto from 'crypto';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { logInfo, logWarn } from '../logging/appNotify';
import { isMtpCapable } from './mtpProjectDetection';
import { resolveMtpReporterDirectory, MTP_REPORTER_ASSEMBLY_FILE_NAME } from './mtpReporterPath';

/**
 * Ephemeral `CustomAfterMicrosoftCommonTargets` injection — issue #715 plan §5.6/§5.7, ported from
 * the Visual Studio extension's `MtpEphemeralInjection.cs` and the Rider plugin's
 * `writeEphemeralMtpTargetsFile`. Never touches any project file on disk: writes a throwaway
 * `.targets` file declaring a `<Reference>` (HintPath) to the bundled MTP reporter plus a
 * `<TestingPlatformBuilderHook>` item, which MTP's own `Microsoft.Testing.Platform.MSBuild`
 * package auto-registers for any project that imports it — every MSBuild-based build already
 * imports `CustomAfterMicrosoftCommonTargets` if it points at an existing file.
 *
 * Unlike VS's own leg (a VSIX package that owns MSBuild's own environment when it launches a
 * build), this extension has no build/test hook of its own at all: C# Dev Kit spawns `dotnet test`
 * in a child process of the *extension host*, inheriting `process.env` at spawn time (confirmed
 * against the `dotnet/vscode-csharp` source: `ProcessRunner`/`DotNetTestChannel`-style spawn calls
 * pass `env: process.env`). Setting `process.env.CustomAfterMicrosoftCommonTargets` here, as early
 * in extension activation as possible, is therefore the whole mechanism — genuinely unverified
 * without a live C# Dev Kit test run (issue #715 plan §7 risk #2): if some future C# Dev Kit
 * version spawns via a detached/isolated environment instead of inheriting the extension host's,
 * this injection silently does nothing (degrades to "no MTP outcomes," never a hard failure).
 */

export const CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE = 'CustomAfterMicrosoftCommonTargets';
const HOOK_GUID = 'a1d3c2f0-6b8e-4f2a-9c7d-3e5f8b1a4d6c';

const EXCLUDED_DIRECTORY_NAMES = new Set(['bin', 'obj', '.git', '.vs', '.vscode', 'node_modules']);

/**
 * Enumerates every `.csproj` under `root`, pruning build-output/VCS/editor/dependency directories.
 * Exported for testing (mirrors `MtpEphemeralInjection.EnumerateProjectFiles` on the VS side).
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
 * Writes the ephemeral `.targets` file declaring the reporter reference + builder hook, chain-
 * importing any pre-existing `CustomAfterMicrosoftCommonTargets` value so this injection composes
 * with whatever else (if anything) already uses that extensibility point. Exported for testing.
 */
export function writeTargetsFile(
  reporterDllPath: string,
  preExistingCustomAfterTargets?: string,
): string {
  const dir = path.join(
    os.tmpdir(),
    `reqnroll-mtp-inject-${crypto.randomBytes(16).toString('hex')}`,
  );
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, 'Reqnroll.IdeSupport.TestReporter.MTP.g.targets');

  const chainImport = preExistingCustomAfterTargets
    ? `  <Import Project="${preExistingCustomAfterTargets}" Condition="Exists('${preExistingCustomAfterTargets}')" />\n`
    : '';

  const xml =
    '<Project>\n' +
    chainImport +
    '  <ItemGroup>\n' +
    '    <Reference Include="Reqnroll.IdeSupport.TestReporter.MTP">\n' +
    `      <HintPath>${reporterDllPath}</HintPath>\n` +
    '    </Reference>\n' +
    `    <TestingPlatformBuilderHook Include="${HOOK_GUID}">\n` +
    '      <DisplayName>Reqnroll.IdeSupport.TestReporter.MTP</DisplayName>\n' +
    '      <TypeFullName>Reqnroll.IdeSupport.TestReporter.MTP.TestingPlatformBuilderHook</TypeFullName>\n' +
    '    </TestingPlatformBuilderHook>\n' +
    '  </ItemGroup>\n' +
    '</Project>\n';

  fs.writeFileSync(file, xml, 'utf8');
  return file;
}

/**
 * Scans every workspace folder for an MTP-capable project and, if one is found, sets
 * `process.env.CustomAfterMicrosoftCommonTargets` for the remainder of this extension-host
 * process's lifetime. Returns `true` when it enabled injection, `false` otherwise (no MTP-capable
 * project found, or the bundled reporter is missing) — never throws.
 */
export async function tryEnableForWorkspace(
  workspaceFolderPaths: readonly string[],
  reporterDllPath: string,
): Promise<boolean> {
  try {
    if (!fs.existsSync(reporterDllPath)) {
      logWarn(
        `testOutcomes: bundled MTP reporter '${reporterDllPath}' not found; not enabling ephemeral injection.`,
      );
      return false;
    }

    const projectFiles = workspaceFolderPaths.flatMap((folder) => enumerateProjectFiles(folder));
    const mtpCapableFlags = await Promise.all(
      projectFiles.map((projectFile) => isMtpCapable(projectFile)),
    );
    const mtpCapable = mtpCapableFlags.some((capable) => capable);
    if (!mtpCapable) {
      logInfo(
        'testOutcomes: no MTP-capable project found in the workspace; not enabling ephemeral injection.',
      );
      return false;
    }

    const preExisting = process.env[CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE];
    const targetsFile = writeTargetsFile(reporterDllPath, preExisting);
    process.env[CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE] = targetsFile;
    logInfo(
      `testOutcomes: MTP ephemeral injection enabled for this session — ${CUSTOM_AFTER_MICROSOFT_COMMON_TARGETS_VARIABLE}=${targetsFile}`,
    );
    return true;
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    logWarn(`testOutcomes: MTP ephemeral injection failed — ${msg}`);
    return false;
  }
}

/**
 * Extension-activation entry point. Gated on the same `reqnroll.testOutcomes.enabled` opt-in as
 * `activateTestOutcomes` (this is part of the same feature — MTP-mode outcome reporting rather
 * than the VSTest-logger path) and must run as early in `activate()` as possible, before any test
 * run C# Dev Kit might launch could plausibly start. Never throws.
 */
export async function activateMtpEphemeralInjection(
  context: Pick<vscode.ExtensionContext, 'extensionMode' | 'extensionPath'>,
): Promise<void> {
  const enabled = vscode.workspace
    .getConfiguration('reqnroll')
    .get<boolean>('testOutcomes.enabled', false);
  if (!enabled) return;

  const reporterDirectory = resolveMtpReporterDirectory(context);
  if (!reporterDirectory) {
    logWarn(
      'testOutcomes: reqnroll.testOutcomes.enabled is true, but the bundled MTP reporter was not found; skipping ephemeral injection.',
    );
    return;
  }

  const workspaceFolderPaths = (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath);
  if (workspaceFolderPaths.length === 0) return;

  await tryEnableForWorkspace(
    workspaceFolderPaths,
    path.join(reporterDirectory, MTP_REPORTER_ASSEMBLY_FILE_NAME),
  );
}
