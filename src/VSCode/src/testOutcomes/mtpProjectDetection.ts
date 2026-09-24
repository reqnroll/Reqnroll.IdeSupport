import * as fs from 'fs';
import * as path from 'path';
import { execFile } from 'child_process';

/**
 * Ad hoc, narrow detection of whether a project is Microsoft.Testing.Platform (MTP)-capable —
 * issue #715 plan §5.7, ported identically from the Rider plugin's `RunTestRunner.detectDotnetTestMode`
 * (Kotlin) and the Visual Studio extension's `MtpProjectDetection.cs`. VS Code needs only this one
 * signal (like VS): C# Dev Kit always drives an MTP-capable project through its own testing-platform
 * pipeline regardless of `dotnet test`'s own CLI redirect mode, so the three-way VsTest/MtpCompat/
 * MtpNative state Rider's own `dotnet test` command line needs is irrelevant here.
 *
 * A plain text scan of the project file itself and every `Directory.Build.props` found walking up
 * to the nearest `.git` is the fast path, but it misses a project made MTP-capable only through an
 * *imported* `.props`/`.targets` file — e.g. referencing the full `xunit.v3` runner package pulls in
 * `Microsoft.Testing.Platform.MSBuild`, which sets `IsTestingPlatformApplication=true` from its own
 * props, never appearing as literal text anywhere a scan would look (issue #722, live-verified
 * against `xUnit.v3.MTP.Sample`: this silently fell back to the classic VSTest logger path instead
 * of the MTP reporter). When the text scan finds nothing but the project text itself already looks
 * like a test project (references `Microsoft.NET.Test.Sdk`), this falls back to a real
 * `dotnet msbuild -getProperty` evaluation — mirroring the VS extension's own fix for the identical
 * gap (`MtpProjectDetection.EvaluateViaMsBuild`) and the Rider plugin's
 * `RunTestRunner.evaluateMtpPropertiesViaMsbuild`. Gating the subprocess on that literal "looks like
 * a test project" signal keeps a solution-wide scan (`mtpProjectStubs.ts` calls this once per
 * `.csproj` in the workspace) from shelling out to `dotnet msbuild` for every ordinary non-test
 * project.
 *
 * Unlike the C#/Kotlin ports, the MSBuild-evaluation fallback here is asynchronous rather than a
 * blocking subprocess wait: this extension has no background thread to block on, only the single
 * extension-host event loop, and a synchronous multi-second `dotnet msbuild` call at activation time
 * would freeze the whole UI (observed live: "Extension host ... is unresponsive" is exactly what a
 * long synchronous call here would produce).
 */
const MTP_CAPABLE_PATTERN =
  /<(EnableMSTestRunner|EnableNUnitRunner|UseMicrosoftTestingPlatformRunner|IsTestingPlatformApplication)>\s*true\s*</i;

const TEST_SDK_PATTERN = /<PackageReference\s+Include\s*=\s*"Microsoft\.NET\.Test\.Sdk"/i;

const MTP_CAPABLE_PROPERTY_NAMES = [
  'EnableMSTestRunner',
  'EnableNUnitRunner',
  'UseMicrosoftTestingPlatformRunner',
  'IsTestingPlatformApplication',
] as const;

const MSBUILD_EVAL_TIMEOUT_MS = 20_000;

/**
 * True if `projectFilePath` itself, or any `Directory.Build.props` found walking up to the nearest
 * `.git`, declares an MTP-capable test framework — or, failing that, a real MSBuild evaluation
 * resolves one of the MTP opt-in properties to `true` (see the module doc comment).
 *
 * `evaluateMtp` is injected for testability — production callers use the default, which shells out
 * to `dotnet msbuild`. Returns `false` (never throws) if the MSBuild evaluation is unavailable,
 * times out, or resolves to "no evidence".
 */
export async function isMtpCapable(
  projectFilePath: string,
  readTextOrNull: (path: string) => string | undefined = readTextOrNullFs,
  evaluateMtp: (path: string) => Promise<boolean | null> = evaluateViaMsBuild,
): Promise<boolean> {
  const projectXml = readTextOrNull(projectFilePath);
  if (projectXml && MTP_CAPABLE_PATTERN.test(projectXml)) return true;

  let dir = path.dirname(projectFilePath);
  for (;;) {
    const propsPath = path.join(dir, 'Directory.Build.props');
    const propsXml = readTextOrNull(propsPath);
    if (propsXml && MTP_CAPABLE_PATTERN.test(propsXml)) return true;

    if (fs.existsSync(path.join(dir, '.git'))) break; // Workspace root reached; stop climbing.

    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }

  // Nothing literal. Only pay for a real MSBuild evaluation when the project already looks like a
  // test project — otherwise a solution-wide scan would shell out to `dotnet msbuild` for every
  // ordinary project.
  if (projectXml && TEST_SDK_PATTERN.test(projectXml)) {
    return (await evaluateMtp(projectFilePath)) === true;
  }

  return false;
}

/**
 * Evaluates `projectFilePath`'s real, MSBuild-resolved MTP opt-in properties via
 * `dotnet msbuild -getProperty:...`. Resolves to `null` — callers treat this as "no evidence" — if
 * the process can't start, times out, exits non-zero, or its output isn't the expected
 * `{"Properties": {...}}` shape; this must never throw or block extension activation on a
 * broken/unrestorable project.
 */
export function evaluateViaMsBuild(projectFilePath: string): Promise<boolean | null> {
  return new Promise((resolve) => {
    const args = [
      'msbuild',
      projectFilePath,
      `-getProperty:${MTP_CAPABLE_PROPERTY_NAMES.join(',')}`,
      '-nologo',
    ];

    const child = execFile(
      'dotnet',
      args,
      { timeout: MSBUILD_EVAL_TIMEOUT_MS, maxBuffer: 1024 * 1024 },
      (error, stdout) => {
        if (error) {
          resolve(null);
          return;
        }

        try {
          const parsed = JSON.parse(stdout) as { Properties?: Record<string, string> };
          const properties = parsed.Properties;
          if (!properties) {
            resolve(null);
            return;
          }

          for (const name of MTP_CAPABLE_PROPERTY_NAMES) {
            if (properties[name]?.toLowerCase() === 'true') {
              resolve(true);
              return;
            }
          }
          resolve(false);
        } catch {
          resolve(null);
        }
      },
    );

    // Suppress unhandled 'error' on process spawn failure (e.g. dotnet not on PATH) — handled via
    // the exec callback's `error` parameter above.
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
