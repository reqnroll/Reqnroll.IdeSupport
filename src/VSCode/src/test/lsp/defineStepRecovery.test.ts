import * as assert from 'assert';
import { execFile } from 'child_process';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as util from 'util';
import * as vscode from 'vscode';

const execFileAsync = util.promisify(execFile);

/**
 * Recreates issue #2's original scenario end-to-end, using the real bundled LSP server and a real
 * `dotnet build` (not the stubbed spec-suite fixtures, which announce a pre-existing output
 * assembly and so never exercise this gap): a project is discovered and registered before it has
 * ever been built, exactly as PR #26 describes ("a freshly cloned repo opened before ever running
 * `dotnet build`"). The fixture lives under `__defineStepE2E__` at the repo root, written to disk
 * by `suiteSetup` below.
 *
 * `Recovery.feature` has two steps: one genuinely bound by `RecoverySteps.cs` (compiled into the
 * DLL only once the build runs), and one deliberately never bound by anything. The `.cs` binding
 * file is never opened in the editor, forcing the server to rely solely on the reflection-based
 * `ConnectorBindingRegistryProvider` path (needs the compiled DLL), not live Roslyn source discovery.
 *
 * Why the never-bound step matters: `DiagnosticsPublishHandler.cs` deliberately suppresses ALL
 * binding-mismatch diagnostics while a project's `ProjectBindingRegistry` is `Invalid` ("the
 * Connector has not completed its first run yet... so the user doesn't see spurious 'undefined
 * step' warnings before bindings are loaded"). That means "zero diagnostics" is ambiguous — it's
 * true both before discovery ever succeeds (suppressed) AND after a successful run correctly finds
 * every step bound. The never-bound step breaks that ambiguity: its diagnostic can only appear once
 * the registry has actually left `Invalid`, so it's the real signal for "the server retried
 * discovery after the build and is no longer suppressing."
 *
 * The fixture must be self-contained to run on a clean CI checkout (not just a dev machine that
 * happened to have it lying around from a previous manual run), so `suiteSetup`/`suiteTeardown`
 * below create and remove it rather than assuming it already exists. It's created *after* the
 * extension is already active (idempotent — activation already happened in an earlier suite
 * within this same test run), so `ProjectManager`'s live `**\/*.csproj` watcher — not the one-shot
 * initial workspace scan — is what discovers and registers it. That's "a new project added
 * mid-session" rather than "already present at activation," but it exercises the same
 * registration-before-first-build precondition either way.
 */
suite('Define Step build-completion recovery (issue #2 root-cause recreation)', function () {
  this.timeout(240_000);

  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  if (!workspaceFolder) {
    throw new Error('This suite requires an open workspace folder');
  }

  const fixtureRoot = path.join(workspaceFolder.uri.fsPath, '__defineStepE2E__');
  const csproj = path.join(fixtureRoot, 'Fixture.csproj');
  const featureFile = path.join(fixtureRoot, 'Recovery.feature');
  const outputDll = path.join(fixtureRoot, 'bin', 'Debug', 'net10.0', 'Fixture.dll');

  const BOUND_LINE = 3; // "    When an unbuilt binding recovers"
  const UNBOUND_LINE = 4; // "    And this step will never be bound"

  const FIXTURE_CSPROJ = `<Project Sdk="Microsoft.NET.Sdk">
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

  const FIXTURE_FEATURE = `Feature: Recovery

Scenario: S
    When an unbuilt binding recovers
    And this step will never be bound
`;

  suiteSetup(async () => {
    await fs.rm(fixtureRoot, { recursive: true, force: true }); // clean slate, defensive
    await fs.mkdir(fixtureRoot, { recursive: true });
    await fs.writeFile(csproj, FIXTURE_CSPROJ);
    await fs.writeFile(path.join(fixtureRoot, 'RecoverySteps.cs'), FIXTURE_STEPS_CS);
    await fs.writeFile(featureFile, FIXTURE_FEATURE);
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

  async function pollUntil(
    check: () => boolean,
    timeoutMs: number,
    intervalMs = 1500,
  ): Promise<{ met: boolean; elapsedMs: number }> {
    const start = Date.now();
    while (Date.now() - start < timeoutMs) {
      if (check()) return { met: true, elapsedMs: Date.now() - start };
      await new Promise((resolve) => setTimeout(resolve, intervalMs));
    }
    return { met: false, elapsedMs: Date.now() - start };
  }

  test('recreate: server recognizes a binding compiled by a build that happens after project registration', async () => {
    const dllExistsBeforeBuild = await fs.stat(outputDll).then(
      () => true,
      () => false,
    );
    assert.strictEqual(
      dllExistsBeforeBuild,
      false,
      "Fixture must start unbuilt to recreate issue #2's precondition",
    );

    const ext = vscode.extensions.getExtension('reqnroll.reqnroll-ide-support')!;
    await ext.activate();

    await openFeatureDoc();
    // Let the fixture .csproj's live-watcher discovery + registerProject's async `dotnet msbuild`
    // evaluation settle before racing it with the build below. Now that the fixture is created
    // mid-session (self-contained, see suiteSetup) rather than present at initial workspace scan,
    // this is a real dependency chain — the server needs the project's OutputAssemblyPath
    // registered *before* WatchedFilesHandler can match the DLL-created event to it — not just a
    // nice-to-have buffer, so it's generous on purpose.
    await new Promise((resolve) => setTimeout(resolve, 15_000));

    const before = currentDiagnostics();
    console.log(
      `[#2 BEFORE BUILD] diagnostics=${JSON.stringify(before.map((d) => ({ line: d.range.start.line, message: d.message })))}`,
    );
    // Expected (by design, per DiagnosticsPublishHandler.cs): zero diagnostics for BOTH lines —
    // mismatch diagnostics are suppressed while the registry is Invalid, not because either step
    // is considered bound.

    const buildStart = Date.now();
    await execFileAsync('dotnet', ['build', csproj, '-c', 'Debug'], {
      cwd: fixtureRoot,
      timeout: 90_000,
    });
    console.log(`[#2 BUILD] dotnet build completed in ${Date.now() - buildStart}ms`);

    const dllExistsAfterBuild = await fs.stat(outputDll).then(
      () => true,
      () => false,
    );
    assert.strictEqual(dllExistsAfterBuild, true, 'Build should have produced the fixture DLL');

    // Real recovery signal: the never-bound step's diagnostic can only appear once the registry
    // has left Invalid — i.e. once discovery actually re-ran after the build.
    const recovered = await pollUntil(
      () => currentDiagnostics().some((d) => d.range.start.line === UNBOUND_LINE),
      90_000,
    );

    const after = currentDiagnostics();
    console.log(
      `[#2 AFTER BUILD] registry left Invalid after ${recovered.elapsedMs}ms (met=${recovered.met}); ` +
        `diagnostics=${JSON.stringify(after.map((d) => ({ line: d.range.start.line, message: d.message })))}`,
    );

    assert.ok(
      recovered.met,
      `Expected a diagnostic for the never-bound step (line ${UNBOUND_LINE}) to appear once the ` +
        `build completed and discovery retried, but the registry was still suppressing diagnostics ` +
        `after ${recovered.elapsedMs}ms.`,
    );

    // Once recovered, the genuinely-bound step must NOT be flagged (proves the compiled binding
    // was actually found, not just that discovery ran and found nothing).
    assert.ok(
      !after.some((d) => d.range.start.line === BOUND_LINE),
      `Expected the genuinely-bound step (line ${BOUND_LINE}) to have no diagnostic once ` +
        `discovery found the compiled RecoverySteps binding. Diagnostics: ${JSON.stringify(after)}`,
    );
  });
});
