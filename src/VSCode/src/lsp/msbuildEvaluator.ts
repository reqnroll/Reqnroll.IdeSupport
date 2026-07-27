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
export async function evaluateProject(projectFile: string): Promise<ProjectProperties | null> {
  try {
    const evaluation = await getMsbuildEvaluation(projectFile);
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
}

/** One entry from `-getItem:Compile;None;Content` (only the metadata we asked MSBuild to resolve). */
interface MsbuildItem {
  Identity: string;
  FullPath?: string;
}

interface MsbuildEvaluation {
  properties: MsbuildProperties;
  // Keyed by item type (Compile/None/Content); absent when the project has none of that type.
  items: Partial<Record<'Compile' | 'None' | 'Content', MsbuildItem[]>>;
}

async function getMsbuildEvaluation(projectFile: string): Promise<MsbuildEvaluation | null> {
  return new Promise((resolve) => {
    const args = [
      'msbuild',
      projectFile,
      '-p:DesignTimeBuild=true',
      '-nologo',
      '-getProperty:TargetFrameworkMoniker;OutputPath;AssemblyName;RootNamespace;ProjectAssetsFile',
      // Compile (.cs bindings) + None/Content (.feature files are typically included as one of
      // these, depending on how the project references the Reqnroll/SpecFlow tooling).
      '-getItem:Compile;None;Content',
    ];

    const child = execFile(
      'dotnet',
      args,
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

        try {
          const parsed = JSON.parse(stdout) as {
            Properties: MsbuildProperties;
            Items?: Partial<Record<'Compile' | 'None' | 'Content', MsbuildItem[]>>;
          };
          const p = parsed.Properties;

          if (!p.TargetFrameworkMoniker || !p.OutputPath || !p.AssemblyName) {
            console.error(`MsbuildEvaluator: missing required properties for ${projectFile}`);
            resolve(null);
            return;
          }

          resolve({ properties: p, items: parsed.Items ?? {} });
        } catch {
          console.error(
            `MsbuildEvaluator: failed to parse msbuild output for ${projectFile}: ${stdout.slice(0, 300)}`,
          );
          resolve(null);
        }
      },
    );

    // Suppress error on EPIPE / child process crashes — handled in callback
    child.on('error', () => {
      /* handled in callback */
    });
  });
}

/**
 * Reduces raw `Compile`/`None`/`Content` MSBuild items to the `.cs`/`.feature` files the
 * project's membership index cares about, deduplicated by resolved absolute path (the same
 * file can appear under more than one item type, e.g. a linked file).
 */
function toProjectFileItems(
  items: Partial<Record<'Compile' | 'None' | 'Content', MsbuildItem[]>>,
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

  return result;
}

// ── Output assembly path ─────────────────────────────────────────────────

function buildOutputPath(projectFile: string, props: MsbuildProperties): string {
  // OutputPath is relative to the project directory (e.g. bin\Debug\net10.0\)
  // AssemblyName is the file name without extension
  const projectDir = path.dirname(projectFile);
  const relativeOutput = props.OutputPath.replace(/\\$/, ''); // strip trailing backslash
  return path.resolve(projectDir, relativeOutput, `${props.AssemblyName}.dll`);
}

// ── Package references from project.assets.json ──────────────────────────

interface AssetsFile {
  targets?: Record<string, Record<string, { type?: string }>>;
  libraries?: Record<string, { type?: string }>;
}

function readPackageReferences(assetsFilePath: string, tfm: string): PackageRef[] {
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
function findTargetKey(assets: AssetsFile, tfm: string): string | undefined {
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
function tfmToShort(tfm: string): string {
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
