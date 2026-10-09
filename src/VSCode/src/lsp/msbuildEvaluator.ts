import * as fs from 'fs';
import * as path from 'path';
import { execFile } from 'child_process';

/**
 * Result of evaluating a .csproj via dotnet msbuild.
 */
export interface ProjectProperties {
  readonly outputAssemblyPath: string;
  readonly targetFrameworkMoniker: string;
  readonly defaultNamespace: string;
  readonly packageReferences: readonly PackageRef[];
  readonly files: readonly ProjectFileItem[];
}

/** A NuGet package reference resolved from `project.assets.json`. */
export interface PackageRef {
  readonly packageId: string;
  readonly version: string;
}

/** One file the project's MSBuild evaluation attributes to it, with link targets resolved. */
export interface ProjectFileItem {
  readonly path: string;
  readonly role: 'feature' | 'binding';
}

/**
 * Evaluates a .csproj using `dotnet msbuild -getProperty`/`-getItem` and reads
 * `project.assets.json` for package references.
 *
 * Returns `null` when `dotnet` is unavailable or evaluation fails
 * (caller falls back to v1 folder-prefix behaviour).
 */
export async function evaluateProject(
  projectFile: string,
  runMsbuild: MsbuildRunner = runDotnetMsbuild,
): Promise<ProjectProperties | null> {
  try {
    const evaluation = await getMsbuildEvaluation(projectFile, runMsbuild);
    if (!evaluation) return null;
    const { properties: props, items } = evaluation;

    const outputAssemblyPath = buildOutputPath(projectFile, props);
    const packageReferences = readPackageReferences(
      props.ProjectAssetsFile,
      props.TargetFrameworkMoniker,
    );
    const files = toProjectFileItems(items);

    return {
      outputAssemblyPath,
      targetFrameworkMoniker: props.TargetFrameworkMoniker,
      defaultNamespace: props.RootNamespace,
      packageReferences,
      files,
    };
  } catch (err) {
    console.error(`MsbuildEvaluator: evaluation failed for ${projectFile}:`, err);
    return null;
  }
}

// ── MSBuild property/item evaluation ─────────────────────────────────────

interface MsbuildProperties {
  TargetFrameworkMoniker: string;
  OutputPath: string;
  AssemblyName: string;
  RootNamespace: string;
  ProjectAssetsFile: string;
  // Used only to detect a multi-targeted project's outer evaluation.
  TargetFramework?: string;
  TargetFrameworks?: string;
}

/** One entry from `-getItem:Compile;None;Content;ReqnrollFeatureFiles` (only the metadata we asked MSBuild to resolve). */
interface MsbuildItem {
  Identity: string;
  FullPath?: string;
}

type MsbuildItemType = 'Compile' | 'None' | 'Content' | 'ReqnrollFeatureFiles';

interface MsbuildEvaluation {
  properties: MsbuildProperties;
  // Keyed by item type; absent when the project has none of that type.
  items: Partial<Record<MsbuildItemType, MsbuildItem[]>>;
}

/**
 * Runs `dotnet <args>` and resolves with its stdout, or `null` when the process fails.
 * Injectable so the evaluation logic can be tested without a .NET SDK.
 */
export type MsbuildRunner = (
  args: readonly string[],
  projectFile: string,
) => Promise<string | null>;

async function getMsbuildEvaluation(
  projectFile: string,
  runMsbuild: MsbuildRunner,
): Promise<MsbuildEvaluation | null> {
  let parsed = await runMsbuildEvaluation(projectFile, runMsbuild);
  if (!parsed) return null;

  // A multi-targeted project (<TargetFrameworks>, no single <TargetFramework>) only yields a
  // TargetFrameworkMoniker, a TFM-specific OutputPath, a ProjectAssetsFile and its Compile items
  // from an inner (per-TFM) evaluation. The server tracks one project (and one TFM) per project
  // file, so re-evaluate for a single deterministic TFM: the first one listed, which is also the
  // one Visual Studio treats as the active target framework by default.
  const outer = parsed.Properties;
  if (!outer.TargetFrameworkMoniker && !outer.TargetFramework) {
    const targetFramework = firstTargetFramework(outer.TargetFrameworks);
    if (targetFramework) {
      parsed = await runMsbuildEvaluation(projectFile, runMsbuild, targetFramework);
      if (!parsed) return null;
    }
  }

  const p = parsed.Properties;
  if (!p.TargetFrameworkMoniker || !p.OutputPath || !p.AssemblyName) {
    console.error(`MsbuildEvaluator: missing required properties for ${projectFile}`);
    return null;
  }

  return { properties: p, items: parsed.Items ?? {} };
}

interface MsbuildOutput {
  Properties: MsbuildProperties;
  Items?: Partial<Record<MsbuildItemType, MsbuildItem[]>>;
}

/** The first non-empty entry of a `;`-separated `TargetFrameworks` value, if any. */
function firstTargetFramework(targetFrameworks: string | undefined): string | undefined {
  return (targetFrameworks ?? '')
    .split(';')
    .map((tfm) => tfm.trim())
    .find(Boolean);
}

async function runMsbuildEvaluation(
  projectFile: string,
  runMsbuild: MsbuildRunner,
  targetFramework?: string,
): Promise<MsbuildOutput | null> {
  const stdout = await runMsbuild(buildMsbuildArgs(projectFile, targetFramework), projectFile);
  if (stdout === null) return null;
  return parseMsbuildOutput(projectFile, stdout);
}

function parseMsbuildOutput(projectFile: string, stdout: string): MsbuildOutput | null {
  try {
    return JSON.parse(stdout) as MsbuildOutput;
  } catch {
    console.error(
      `MsbuildEvaluator: failed to parse msbuild output for ${projectFile}: ${stdout.slice(0, 300)}`,
    );
    return null;
  }
}

function buildMsbuildArgs(projectFile: string, targetFramework?: string): string[] {
  return [
    'msbuild',
    projectFile,
    '-p:DesignTimeBuild=true',
    ...(targetFramework ? [`-p:TargetFramework=${targetFramework}`] : []),
    '-nologo',
    '-getProperty:TargetFrameworkMoniker;OutputPath;AssemblyName;RootNamespace;ProjectAssetsFile;TargetFramework;TargetFrameworks',
    // Compile (.cs bindings) + None/Content (.feature files, for projects that don't use
    // Reqnroll's own MSBuild generation tooling) + ReqnrollFeatureFiles. Reqnroll.Tools.MsBuild.
    // Generation.props (pulled in transitively by Reqnroll.MsTest/Reqnroll.xUnit/etc.) appends
    // `**/*.feature` to $(DefaultItemExcludes), which removes .feature files from the default
    // None/Content globs entirely for any project using it — they're tracked instead via this
    // private `ReqnrollFeatureFiles` item, statically populated in that package's .props (so
    // `-getItem` sees it without running a build target — unlike `EmbeddedResource`, which the
    // same package only populates inside a Target that a bare item-evaluation never runs).
    // Without this, every project using that (very common) package reports zero feature files
    // in its reqnroll/projectFiles baseline, permanently orphaning its .feature files from the
    // server's membership index (confirmed live: feature-file CodeLens stuck reporting no
    // matches). Querying an item name that doesn't exist for a given project (e.g. one that
    // doesn't reference the package) is safe — MSBuild just returns an empty array for it.
    '-getItem:Compile;None;Content;ReqnrollFeatureFiles',
  ];
}

function runDotnetMsbuild(args: readonly string[], projectFile: string): Promise<string | null> {
  return new Promise((resolve) => {
    const child = execFile(
      'dotnet',
      [...args],
      {
        timeout: 30_000,
        maxBuffer: 1024 * 1024,
        env: { ...process.env, MSYS_NO_PATHCONV: '1' },
      },
      (error, stdout, _stderr) => {
        if (error) {
          console.error(
            `MsbuildEvaluator: dotnet msbuild failed for ${projectFile}: ${error.message}`,
          );
          resolve(null);
          return;
        }
        resolve(stdout);
      },
    );

    // Suppress error on EPIPE / child process crashes — handled in callback
    child.on('error', () => {
      /* handled in callback */
    });
  });
}

/**
 * Reduces raw `Compile`/`None`/`Content`/`ReqnrollFeatureFiles` MSBuild items to the
 * `.cs`/`.feature` files the project's membership index cares about, deduplicated by resolved
 * absolute path (the same file can appear under more than one item type, e.g. a linked file, or
 * a project using Reqnroll's MSBuild generation tooling where None/Content never carry .feature
 * files at all — see the comment at the `-getItem` call site).
 */
export function toProjectFileItems(
  items: Partial<Record<MsbuildItemType, MsbuildItem[]>>,
): ProjectFileItem[] {
  const seen = new Set<string>();
  const result: ProjectFileItem[] = [];

  const addAll = (entries: MsbuildItem[] | undefined, role: 'feature' | 'binding', ext: string) => {
    for (const entry of entries ?? []) {
      const resolved = entry.FullPath ?? entry.Identity;
      if (!resolved.toLowerCase().endsWith(ext)) continue;
      const key = resolved.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      result.push({ path: resolved, role });
    }
  };

  addAll(items.Compile, 'binding', '.cs');
  addAll(items.None, 'feature', '.feature');
  addAll(items.Content, 'feature', '.feature');
  addAll(items.ReqnrollFeatureFiles, 'feature', '.feature');

  return result;
}

// ── Output assembly path ─────────────────────────────────────────────────

export function buildOutputPath(projectFile: string, props: MsbuildProperties): string {
  // OutputPath is relative to the project directory (e.g. bin\Debug\net10.0\). MSBuild's
  // built-in targets emit this with literal backslashes regardless of host OS, so it must be
  // split into segments rather than handed to `path` as a single (possibly POSIX) path piece.
  // AssemblyName is the file name without extension.
  const projectDir = path.dirname(projectFile);
  const relativeSegments = props.OutputPath.split(/[\\/]/).filter(Boolean);
  return path.resolve(projectDir, ...relativeSegments, `${props.AssemblyName}.dll`);
}

// ── Package references from project.assets.json ──────────────────────────

interface AssetsFile {
  targets?: Record<string, Record<string, { type?: string }>>;
  libraries?: Record<string, { type?: string }>;
}

export function readPackageReferences(assetsFilePath: string, tfm: string): PackageRef[] {
  if (!assetsFilePath || !fs.existsSync(assetsFilePath)) {
    return [];
  }

  try {
    const assets = JSON.parse(fs.readFileSync(assetsFilePath, 'utf-8')) as AssetsFile;

    // `project.assets.json` has a `libraries` object where keys are "id/version"
    // Filter to NuGet packages (type: "package") in the current TFM target
    const targetKey = findTargetKey(assets, tfm);
    if (!targetKey) return [];

    const target = assets.targets?.[targetKey];
    if (!target) return [];

    return Object.entries(target)
      .filter(
        ([entryKey, value]) =>
          value?.type === 'package' && !entryKey.startsWith('Microsoft.NETCore.'),
      )
      .map(([key]) => {
        const slash = key.lastIndexOf('/');
        return {
          packageId: key.slice(0, slash),
          version: key.slice(slash + 1),
        };
      });
  } catch {
    return [];
  }
}

/**
 * Finds the TFM target key in project.assets.json that best matches
 * the given TargetFrameworkMoniker (e.g. ".NETCoreApp,Version=v8.0" → "net8.0").
 */
export function findTargetKey(assets: AssetsFile, tfm: string): string | undefined {
  const targets = assets.targets;
  if (!targets) return undefined;

  // The assets file keys are short TFMs like "net8.0", "netstandard2.0", "net481"
  const shortTfm = tfmToShort(tfm);

  if (shortTfm && targets[shortTfm]) return shortTfm;

  // Fallback: return the first available target
  return Object.keys(targets)[0];
}

/**
 * Converts a full TargetFrameworkMoniker to a short name.
 * ".NETCoreApp,Version=v8.0" → "net8.0"
 * ".NETStandard,Version=v2.0" → "netstandard2.0"
 * ".NETFramework,Version=v4.8.1" → "net481"
 */
export function tfmToShort(tfm: string): string {
  const match = tfm.match(
    /\.NET(?:CoreApp|Standard|Framework|Portable),Version=v(\d+(?:\.\d+)?(?:\.\d+)?)/i,
  );
  if (!match) return tfm.toLowerCase().replace(/[^a-z0-9.]/g, '');

  const versionParts = match[1].split('.');
  const major = versionParts[0];
  const minor = versionParts[1] ?? '0';
  const patch = versionParts[2];

  if (tfm.includes('NETFramework')) {
    // Each version component is a single digit for every real .NET Framework release, so they
    // concatenate directly with no separator or padding: 4.5 → net45, 4.5.1 → net451,
    // 4.8 → net48, 4.8.1 → net481. The patch digit is only appended when present and non-zero.
    const suffix = patch && patch !== '0' ? patch : '';
    return `net${major}${minor}${suffix}`;
  }
  if (tfm.includes('NETStandard')) {
    return `netstandard${major}.${minor}`;
  }
  return `net${major}.${minor}`;
}
