import * as fs from 'fs';
import * as path from 'path';

/**
 * Ad hoc, narrow detection of whether a project is Microsoft.Testing.Platform (MTP)-capable —
 * issue #715 plan §5.7, ported identically from the Rider plugin's `RunTestRunner.detectDotnetTestMode`
 * (Kotlin) and the Visual Studio extension's `MtpProjectDetection.cs`. VS Code needs only this one
 * signal (like VS): C# Dev Kit always drives an MTP-capable project through its own testing-platform
 * pipeline regardless of `dotnet test`'s own CLI redirect mode, so the three-way VsTest/MtpCompat/
 * MtpNative state Rider's own `dotnet test` command line needs is irrelevant here.
 *
 * A plain regex text-scan rather than a real MSBuild evaluation (unlike `msbuildEvaluator.ts`'s
 * `-getProperty` evaluation elsewhere in this extension) — deliberately, to keep this identical
 * across all three IDE ports and avoid a `dotnet msbuild` subprocess per project at activation time,
 * when this only needs to run once, over every `.csproj` in the workspace, before any test runs.
 */
const MTP_CAPABLE_PATTERN =
  /<(EnableMSTestRunner|EnableNUnitRunner|UseMicrosoftTestingPlatformRunner|IsTestingPlatformApplication)>\s*true\s*</i;

export function isMtpCapable(
  projectFilePath: string,
  readTextOrNull: (path: string) => string | undefined = readTextOrNullFs,
): boolean {
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

  return false;
}

function readTextOrNullFs(filePath: string): string | undefined {
  try {
    return fs.existsSync(filePath) ? fs.readFileSync(filePath, 'utf8') : undefined;
  } catch {
    return undefined;
  }
}
