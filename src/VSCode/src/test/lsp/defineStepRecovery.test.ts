import * as assert from 'assert';
import { execFile } from 'child_process';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as util from 'util';
import * as vscode from 'vscode';

const execFileAsync = util.promisify(execFile);

/**
 * Recreates the unbuilt-project binding-discovery scenario end-to-end, using the real bundled LSP
 * server and a real `dotnet build` (not the stubbed spec-suite fixtures, which announce a
 * pre-existing output assembly and so never exercise this gap): a project is discovered and
 * registered before it has ever been built, as in a freshly cloned repo opened before ever running
 * `dotnet build`. The fixture lives under `__defineStepE2E__` at the repo root, written to disk by
 * `suiteSetup` below.
 *
 * The fixture is two projects: `Fixture` (owns `Recovery.feature` and `RecoverySteps.cs`) and
 * `FixtureLib`, a class library `Fixture` references and lists under `bindingAssemblies` in its
 * `reqnroll.json`. `Recovery.feature` has three steps:
 *
 * - bound by `RecoverySteps.cs` in `Fixture` itself;
 * - bound by `LibrarySteps.cs` in `FixtureLib`;
 * - never bound by anything.
 *
 * Why three: while a project has no output assembly, `ConnectorBindingRegistryProvider` fills its
 * registry from the project's own source files instead (the source-level fallback). That makes the
 * own-project step bound and the never-bound step flagged *before* the build, so neither of them
 * can tell whether discovery from the compiled assembly ever ran. The source fallback cannot see
 * bindings declared in referenced assemblies, though: the `FixtureLib` step stays flagged until
 * the connector has loaded the freshly built `Fixture.dll` (and, through `bindingAssemblies`,
 * `FixtureLib.dll`). Its diagnostic disappearing is the signal that the build's output-assembly
 * event reached the server and discovery re-ran. The never-bound step's diagnostic staying put
 * proves the registry was replaced by a real discovery result rather than dropped back to
 * `Invalid` (which suppresses every mismatch diagnostic, see `DiagnosticsPublishHandler.cs`).
 *
 * No `.cs` file is opened in the editor, so live Roslyn discovery of open documents plays no part.
 *
 * The fixture must be self-contained to run on a clean checkout, so `suiteSetup`/`suiteTeardown`
 * create and remove it. It's created *after* the extension is already active (activation already
 * happened in an earlier suite within this same test run), so `ProjectManager`'s live
 * `**\/*.csproj` watcher — not the one-shot initial workspace scan — is what discovers and
 * registers it. That's "a new project added mid-session" rather than "already present at
 * activation," but it exercises the same registration-before-first-build precondition either way.
 *
 * Skipped on CI: relies on `workspace/didChangeWatchedFiles` actually being delivered for a real
 * `dotnet build`'s output, which — per `watcherExclude.test.ts`'s investigation — does not appear
 * to work at all inside this repo's `xvfb-run` + headless-Electron GitHub Actions runner, even
 * with a generous timeout. That's a CI-sandbox capability gap, not something this test or the
 * mechanism under test can route around. Runs locally / in manual Extension Host runs.
 */
const suiteFn = process.env.CI ? suite.skip : suite;

suiteFn('Define Step build-completion recovery', function () {
  this.timeout(330_000); // covers the 90 s registration wait, the 120 s build and the 90 s recovery wait

  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  if (!workspaceFolder) {
    throw new Error('This suite requires an open workspace folder');
  }

  const fixtureRoot = path.join(workspaceFolder.uri.fsPath, '__defineStepE2E__');
  const projectDir = path.join(fixtureRoot, 'Fixture');
  const libraryDir = path.join(fixtureRoot, 'FixtureLib');
  const csproj = path.join(projectDir, 'Fixture.csproj');
  const featureFile = path.join(projectDir, 'Recovery.feature');
  const outputDll = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'Fixture.dll');

  const OWN_PROJECT_LINE = 3; // "    When an unbuilt binding recovers"
  const LIBRARY_LINE = 4; // "    And a binding from a referenced assembly is found"
  const UNBOUND_LINE = 5; // "    And this step will never be bound"

  // CopyLocalLockFileAssemblies puts Reqnroll.dll next to Fixture.dll, as Microsoft.NET.Test.Sdk
  // does for a real test project. The server's Reqnroll-test-project check needs either that or
  // a Reqnroll package reference, and it has no package references for this project: it was
  // registered before its first restore, so there was no project.assets.json to read them from.
  const FIXTURE_CSPROJ = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Reqnroll" Version="3.2.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\\FixtureLib\\FixtureLib.csproj" />
  </ItemGroup>
</Project>
`;

  const FIXTURE_REQNROLL_JSON = `{
  "bindingAssemblies": [
    { "assembly": "FixtureLib" }
  ]
}
`;

  const FIXTURE_STEPS_CS = `using Reqnroll;

namespace Fixture
{
    [Binding]
    public class RecoverySteps
    {
        [When("an unbuilt binding recovers")]
        public void WhenAnUnbuiltBindingRecovers()
        {
        }
    }
}
`;

  const LIBRARY_CSPROJ = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Reqnroll" Version="3.2.0" />
  </ItemGroup>
</Project>
`;

  const LIBRARY_STEPS_CS = `using Reqnroll;

namespace FixtureLib
{
    [Binding]
    public class LibrarySteps
    {
        [When("a binding from a referenced assembly is found")]
        public void WhenABindingFromAReferencedAssemblyIsFound()
        {
        }
    }
}
`;

  const FIXTURE_FEATURE = `Feature: Recovery

Scenario: S
    When an unbuilt binding recovers
    And a binding from a referenced assembly is found
    And this step will never be bound
`;

  suiteSetup(async () => {
    await fs.rm(fixtureRoot, { recursive: true, force: true }); // clean slate, defensive
    await fs.mkdir(libraryDir, { recursive: true });
    await fs.writeFile(path.join(libraryDir, 'FixtureLib.csproj'), LIBRARY_CSPROJ);
    await fs.writeFile(path.join(libraryDir, 'LibrarySteps.cs'), LIBRARY_STEPS_CS);
    await fs.mkdir(projectDir, { recursive: true });
    await fs.writeFile(path.join(projectDir, 'reqnroll.json'), FIXTURE_REQNROLL_JSON);
    await fs.writeFile(path.join(projectDir, 'RecoverySteps.cs'), FIXTURE_STEPS_CS);
    await fs.writeFile(featureFile, FIXTURE_FEATURE);
    // The project file last: its creation is what the live .csproj watcher reacts to, and every
    // other fixture file must already be on disk when the project is evaluated and registered.
    await fs.writeFile(csproj, FIXTURE_CSPROJ);
  });

  suiteTeardown(async () => {
    await fs.rm(fixtureRoot, { recursive: true, force: true });
  });

  async function openFeatureDoc(): Promise<vscode.TextDocument> {
    const uri = vscode.Uri.file(featureFile);
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(doc);
    return doc;
  }

  function currentDiagnostics(): vscode.Diagnostic[] {
    return vscode.languages.getDiagnostics(vscode.Uri.file(featureFile));
  }

  function flagged(line: number): boolean {
    return currentDiagnostics().some((d) => d.range.start.line === line);
  }

  function describeDiagnostics(): string {
    return JSON.stringify(
      currentDiagnostics().map((d) => ({ line: d.range.start.line, message: d.message })),
    );
  }

  async function pollUntil(
    check: () => boolean,
    timeoutMs: number,
    intervalMs = 500,
  ): Promise<{ met: boolean; elapsedMs: number }> {
    const start = Date.now();
    while (Date.now() - start < timeoutMs) {
      if (check()) return { met: true, elapsedMs: Date.now() - start };
      await new Promise((resolve) => setTimeout(resolve, intervalMs));
    }
    return { met: check(), elapsedMs: Date.now() - start };
  }

  test('recreate: server recognizes a binding compiled by a build that happens after project registration', async () => {
    const dllExistsBeforeBuild = await fs.stat(outputDll).then(
      () => true,
      () => false,
    );
    assert.strictEqual(dllExistsBeforeBuild, false, 'Fixture must start unbuilt');

    const ext = vscode.extensions.getExtension('Reqnroll.reqnroll-ide-support-vscode')!;
    await ext.activate();

    await openFeatureDoc();

    // The project must be registered (with its output assembly path known) before the build
    // runs, or the server cannot match the build's DLL-created event to it. Registration of the
    // unbuilt project ends in the source-level fallback, which flags the never-bound step: wait
    // for that rather than for a fixed delay.
    const registered = await pollUntil(() => flagged(UNBOUND_LINE), 90_000);
    console.log(
      `[BEFORE BUILD] registered after ${registered.elapsedMs}ms (met=${registered.met}); ` +
        `diagnostics=${describeDiagnostics()}`,
    );
    assert.ok(
      registered.met,
      `Expected the unbuilt project to be registered with source-discovered bindings (a diagnostic ` +
        `on the never-bound step, line ${UNBOUND_LINE}) before the build. Diagnostics: ${describeDiagnostics()}`,
    );
    assert.ok(
      !flagged(OWN_PROJECT_LINE),
      `Expected the source-level fallback to bind the step declared in the project's own source ` +
        `(line ${OWN_PROJECT_LINE}) before the build. Diagnostics: ${describeDiagnostics()}`,
    );
    assert.ok(
      flagged(LIBRARY_LINE),
      `Expected the step bound only in the referenced assembly (line ${LIBRARY_LINE}) to be ` +
        `undefined before the build: the source-level fallback cannot see it. Diagnostics: ${describeDiagnostics()}`,
    );

    const buildStart = Date.now();
    await execFileAsync('dotnet', ['build', csproj, '-c', 'Debug'], {
      cwd: projectDir,
      timeout: 120_000,
    });
    console.log(`[BUILD] dotnet build completed in ${Date.now() - buildStart}ms`);

    const dllExistsAfterBuild = await fs.stat(outputDll).then(
      () => true,
      () => false,
    );
    assert.strictEqual(dllExistsAfterBuild, true, 'Build should have produced the fixture DLL');

    // Only discovery from the compiled assembly can bind the referenced-assembly step.
    const recovered = await pollUntil(() => !flagged(LIBRARY_LINE), 90_000);
    console.log(
      `[AFTER BUILD] referenced-assembly step bound after ${recovered.elapsedMs}ms (met=${recovered.met}); ` +
        `diagnostics=${describeDiagnostics()}`,
    );

    assert.ok(
      recovered.met,
      `Expected the step bound in the referenced assembly (line ${LIBRARY_LINE}) to lose its ` +
        `diagnostic once the build completed and discovery from the compiled assembly re-ran, but ` +
        `it was still flagged after ${recovered.elapsedMs}ms. Diagnostics: ${describeDiagnostics()}`,
    );
    assert.ok(
      flagged(UNBOUND_LINE),
      `Expected the never-bound step (line ${UNBOUND_LINE}) to stay flagged after discovery re-ran. ` +
        `Diagnostics: ${describeDiagnostics()}`,
    );
    assert.ok(
      !flagged(OWN_PROJECT_LINE),
      `Expected the step bound in the project's own source (line ${OWN_PROJECT_LINE}) to stay bound ` +
        `after discovery re-ran. Diagnostics: ${describeDiagnostics()}`,
    );
  });
});
