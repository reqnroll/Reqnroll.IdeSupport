import * as assert from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';
import {
  resolveWorkspaceFolder,
  findOwningProjectFile,
  ProjectManager,
} from '../../lsp/projectManager';
import { ReqnrollMethods } from '../../lsp/lspMethods';

suite('ProjectManager', () => {
  test('ReqnrollMethods defines the LSP method names ProjectManager sends', () => {
    // Exercises the real constants module, not a local copy — a rename in lspMethods.ts
    // (or a mismatch with CustomLspMethodNames.cs) would fail this test.
    assert.strictEqual(ReqnrollMethods.projectLoaded, 'reqnroll/projectLoaded');
    assert.strictEqual(ReqnrollMethods.projectUnloaded, 'reqnroll/projectUnloaded');
    assert.strictEqual(ReqnrollMethods.projectFiles, 'reqnroll/projectFiles');
  });

  test('should discover .csproj/.slnx/.sln files from workspace folders', async () => {
    const patterns = ['**/*.csproj', '**/*.slnx', '**/*.sln'];
    const found = new Set<string>();

    for (const pattern of patterns) {
      const matches = await vscode.workspace.findFiles(pattern, '**/node_modules/**');
      for (const uri of matches) found.add(uri.toString());
    }

    // node_modules is excluded, and matches from the three patterns must not collide
    // (a .csproj can't also be a .sln/.slnx), so no duplicate handling should be needed.
    for (const uriStr of found) {
      assert.ok(!uriStr.includes('node_modules'), `${uriStr} should have been excluded`);
    }

    // Assert that specific known project/solution files are discovered in this repo.
    const foundPaths = [...found].map((s) => {
      const parts = s.split('/');
      // Keep the relative path from the repo root (last 2-3 segments)
      return parts.slice(-3).join('/');
    });

    assert.ok(
      foundPaths.some((p) => p.endsWith('Reqnroll.IdeSupport.slnx')),
      'Expected Reqnroll.IdeSupport.slnx to be discovered',
    );
    // The extension itself lives under src/VSCode; assert that at least one .csproj from
    // src/Core or src/LSP is found, proving the recursive glob works beyond the VSCode dir.
    assert.ok(
      foundPaths.some((p) => p.includes('LSP.Server') && p.endsWith('.csproj')),
      'Expected at least one LSP .csproj to be discovered',
    );
    assert.ok(
      foundPaths.some((p) => p.includes('Common') && p.endsWith('.csproj')),
      'Expected at least one Common .csproj to be discovered',
    );
  });

  suite('resolveWorkspaceFolder', () => {
    test('returns the workspace folder that contains the project file', () => {
      const folders = [path.join('C:', 'work', 'RepoA'), path.join('C:', 'work', 'RepoB')];
      const projectFile = path.join(folders[1], 'src', 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, folders), folders[1]);
    });

    test('falls back to the first folder when none contain the project file', () => {
      const folders = [path.join('C:', 'work', 'RepoA'), path.join('C:', 'work', 'RepoB')];
      const projectFile = path.join('C:', 'elsewhere', 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, folders), folders[0]);
    });

    test('returns the project file itself when there are no workspace folders', () => {
      const projectFile = path.join('C:', 'work', 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, []), projectFile);
    });

    test('handles trailing separator mismatch between folder and projectFile', () => {
      const folder = path.join('C:', 'work', 'RepoA') + path.sep;
      const folders = [folder];
      const projectFile = path.join('C:', 'work', 'RepoA', 'src', 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, folders), folder);
    });

    test('matches a project file at the workspace root', () => {
      const folders = [path.join('C:', 'work', 'Repo')];
      const projectFile = path.join(folders[0], 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, folders), folders[0]);
    });

    test('prefers the deepest-nested workspace folder', () => {
      const parent = path.join('C:', 'work', 'Parent');
      const child = path.join('C:', 'work', 'Parent', 'Sub');
      const folders = [parent, child];
      const projectFile = path.join(child, 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, folders), child);
    });

    test('prefers the deepest-nested workspace folder regardless of array order', () => {
      const parent = path.join('C:', 'work', 'Parent');
      const child = path.join('C:', 'work', 'Parent', 'Sub');
      const folders = [child, parent]; // deepest folder listed first this time
      const projectFile = path.join(child, 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, folders), child);
    });

    test('does not let a sibling folder name that is a string-prefix of another collide', () => {
      const foo = path.join('C:', 'work', 'Foo');
      const fooBar = path.join('C:', 'work', 'FooBar');
      const folders = [foo, fooBar];
      const projectFile = path.join(fooBar, 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, folders), fooBar);
    });

    test('matches case-insensitively', () => {
      const folder = path.join('C:', 'work', 'Repo');
      const folders = [folder];
      const projectFile = path.join('c:', 'WORK', 'repo', 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, folders), folder);
    });
  });

  suite('findOwningProjectFile', () => {
    test('picks the deepest matching project when projects are nested', () => {
      const outer = path.join('C:', 'work', 'Outer.csproj');
      const inner = path.join('C:', 'work', 'Sub', 'Inner.csproj');
      const known = new Set([outer, inner]);
      const file = path.join('C:', 'work', 'Sub', 'Steps.cs');

      assert.strictEqual(findOwningProjectFile(file, known), inner);
    });

    test('returns undefined when no known project covers the file', () => {
      const known = new Set([path.join('C:', 'work', 'A.csproj')]);
      const file = path.join('C:', 'elsewhere', 'Steps.cs');

      assert.strictEqual(findOwningProjectFile(file, known), undefined);
    });

    test('ignores non-.csproj entries such as .sln/.slnx', () => {
      const csproj = path.join('C:', 'work', 'App.csproj');
      const known = new Set([path.join('C:', 'work', 'App.sln'), csproj]);
      const file = path.join('C:', 'work', 'Steps.cs');

      assert.strictEqual(findOwningProjectFile(file, known), csproj);
    });

    test('resolves an output assembly under bin/ to its owning project (v5 build-completion watcher)', () => {
      const csproj = path.join('C:', 'work', 'App.csproj');
      const known = new Set([csproj]);
      const dll = path.join('C:', 'work', 'bin', 'Debug', 'net8.0', 'App.dll');

      assert.strictEqual(findOwningProjectFile(dll, known), csproj);
    });
  });
});

/**
 * Regression tests for issue #1010: a debounced membership resend — or a resend that is already
 * in flight — must never re-announce a project after it has been unregistered, and a pending
 * timer must not fire after the manager has been disposed. `resendProjectFiles` calls
 * `sendProjectLoaded`, which sends even when MSBuild evaluation fails, so without a guard the
 * removed project is resurrected on the server's side.
 *
 * The manager is driven through its internals (a `.cs` file event arms the debounce; the private
 * `scheduleResend`/`resendProjectFiles`/`unregisterProject` methods are invoked directly) because
 * the real trigger is an OS-level file-watch event that is not reliably delivered inside a
 * headless Extension Host — see watcherExclude.test.ts.
 */
suite('ProjectManager resend cancellation (issue #1010)', () => {
  interface SentNotification {
    readonly method: string;
    readonly params: { readonly projectFile?: string };
  }

  function makeManager(sent: SentNotification[]): ProjectManager {
    // The manager only ever calls `sendNotification` on its client, so a minimal stub is enough.
    const client = {
      sendNotification: (method: string, params: { projectFile?: string }) => {
        sent.push({ method, params });
        return Promise.resolve();
      },
    };
    return new ProjectManager(client as unknown as ConstructorParameters<typeof ProjectManager>[0]);
  }

  function projectLoadedCount(sent: SentNotification[], projectFile: string): number {
    return sent.filter(
      (n) => n.method === ReqnrollMethods.projectLoaded && n.params.projectFile === projectFile,
    ).length;
  }

  function timersOf(manager: ProjectManager): Map<string, unknown> {
    return (manager as unknown as { _resendTimers: Map<string, unknown> })._resendTimers;
  }

  function armDebounce(manager: ProjectManager, projectFile: string, changedFile: string): void {
    (manager as unknown as { scheduleResend(u: vscode.Uri): void }).scheduleResend(
      vscode.Uri.file(changedFile),
    );
  }

  test('resendProjectFiles does not re-announce a project that is no longer known', async () => {
    const sent: SentNotification[] = [];
    const manager = makeManager(sent);
    try {
      const projectFile = path.join('C:', 'not-registered-1010', 'App.csproj');

      await (
        manager as unknown as { resendProjectFiles(p: string): Promise<void> }
      ).resendProjectFiles(projectFile);

      assert.strictEqual(
        projectLoadedCount(sent, projectFile),
        0,
        'resendProjectFiles must be a no-op for a project that is not registered',
      );
    } finally {
      manager.dispose();
    }
  });

  test('unregisterProject cancels a pending debounce resend so it cannot fire afterwards', async () => {
    const sent: SentNotification[] = [];
    const manager = makeManager(sent);
    try {
      const projectDir = path.join('C:', 'pending-1010');
      const projectFile = path.join(projectDir, 'App.csproj');
      const changedFile = path.join(projectDir, 'Steps.cs');

      // Seed the manager as if the project had already been registered, then arm the debounce
      // (a .cs file event) for its owning project.
      (manager as unknown as { _knownProjects: Set<string> })._knownProjects.add(projectFile);
      armDebounce(manager, projectFile, changedFile);
      assert.ok(
        timersOf(manager).has(projectFile),
        'expected a pending resend timer to be armed for the project',
      );

      await (
        manager as unknown as { unregisterProject(u: vscode.Uri): Promise<void> }
      ).unregisterProject(vscode.Uri.file(projectFile));

      const before = projectLoadedCount(sent, projectFile);

      // Wait well past the debounce window (RESEND_DEBOUNCE_MS = 500): the cancelled timer must
      // not fire and re-announce the removed project.
      await new Promise((resolve) => setTimeout(resolve, 1500));

      assert.strictEqual(
        projectLoadedCount(sent, projectFile),
        before,
        'a resend fired for a project that was unregistered',
      );
      assert.ok(
        !timersOf(manager).has(projectFile),
        'the pending resend timer should have been cleared on unregister',
      );
    } finally {
      manager.dispose();
    }
  });

  test('dispose cancels pending resend timers so they cannot fire afterwards', async () => {
    const sent: SentNotification[] = [];
    const manager = makeManager(sent);
    const projectDir = path.join('C:', 'dispose-1010');
    const projectFile = path.join(projectDir, 'App.csproj');
    const changedFile = path.join(projectDir, 'Steps.cs');

    (manager as unknown as { _knownProjects: Set<string> })._knownProjects.add(projectFile);
    armDebounce(manager, projectFile, changedFile);
    assert.ok(timersOf(manager).has(projectFile));

    manager.dispose();

    await new Promise((resolve) => setTimeout(resolve, 1500));

    assert.strictEqual(
      projectLoadedCount(sent, projectFile),
      0,
      'a resend fired after the manager was disposed',
    );
  });
});
