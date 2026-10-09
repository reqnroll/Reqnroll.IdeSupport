import * as assert from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import {
  ProjectManager,
  resolveWorkspaceFolder,
  findOwningProjectFile,
} from '../../lsp/projectManager';
import { ReqnrollMethods } from '../../lsp/lspMethods';

const MSBUILD_EVALUATOR_PATH = require.resolve('../../lsp/msbuildEvaluator');

interface RecordedNotification {
  method: unknown;
  params: Record<string, unknown>;
}

function fakeClient(): { client: LanguageClient; notifications: RecordedNotification[] } {
  const notifications: RecordedNotification[] = [];
  const client = {
    sendNotification: (method: unknown, params: unknown) => {
      notifications.push({ method, params: params as Record<string, unknown> });
      return Promise.resolve();
    },
  } as unknown as LanguageClient;
  return { client, notifications };
}

function countNotifications(
  notifications: RecordedNotification[],
  method: unknown,
  projectFile: string,
): number {
  return notifications.filter((n) => n.method === method && n.params.projectFile === projectFile)
    .length;
}

async function waitFor(predicate: () => boolean, timeoutMs = 5000): Promise<void> {
  const start = Date.now();
  while (!predicate()) {
    if (Date.now() - start > timeoutMs) throw new Error('timed out waiting for condition');
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
}

type UriListener = (uri: vscode.Uri) => unknown;
interface WatcherListeners {
  create?: UriListener;
  change?: UriListener;
  delete?: UriListener;
}

const STUBBED_WORKSPACE_MEMBERS = [
  'createFileSystemWatcher',
  'findFiles',
  'onDidChangeWorkspaceFolders',
] as const;

/**
 * Replaces the slice of `vscode.workspace` ProjectManager touches at construction time with
 * deterministic stand-ins, and swaps the MSBuild evaluator for a fast null-returning stub, so a
 * ProjectManager can be driven with synthetic watcher events without a real file system, a real
 * `dotnet msbuild` run, or workspace folders. Mirrors the save/restore-via-Object.defineProperty
 * technique used by manualDocumentSync.test.ts for vscode.workspace's event emitters.
 */
async function withStubbedProjectEnvironment(
  fn: (env: {
    fireCreate: (uri: vscode.Uri) => void;
    fireChange: (uri: vscode.Uri) => void;
    fireDelete: (uri: vscode.Uri) => void;
  }) => Promise<void>,
): Promise<void> {
  const originalMembers = STUBBED_WORKSPACE_MEMBERS.map(
    (name) => [name, Object.getOwnPropertyDescriptor(vscode.workspace, name)!] as const,
  );

  const projectWatcherListeners: WatcherListeners = {};

  const makeWatcher = (pattern: vscode.GlobPattern): vscode.FileSystemWatcher => {
    // ProjectManager arms two watchers: the project/solution one (*.csproj) this test drives,
    // and a *.cs/*.feature one whose events it does not need to fire.
    const listeners: WatcherListeners =
      typeof pattern === 'string' && pattern.includes('csproj') ? projectWatcherListeners : {};
    return {
      onDidCreate: (listener: UriListener) => {
        listeners.create = listener;
        return { dispose: () => undefined };
      },
      onDidChange: (listener: UriListener) => {
        listeners.change = listener;
        return { dispose: () => undefined };
      },
      onDidDelete: (listener: UriListener) => {
        listeners.delete = listener;
        return { dispose: () => undefined };
      },
      dispose: () => undefined,
    } as unknown as vscode.FileSystemWatcher;
  };

  Object.defineProperty(vscode.workspace, 'createFileSystemWatcher', {
    configurable: true,
    value: makeWatcher,
  });
  Object.defineProperty(vscode.workspace, 'findFiles', {
    configurable: true,
    value: () => Promise.resolve([] as vscode.Uri[]),
  });
  Object.defineProperty(vscode.workspace, 'onDidChangeWorkspaceFolders', {
    configurable: true,
    value: () => ({ dispose: () => undefined }),
  });

  // eslint-disable-next-line @typescript-eslint/no-require-imports -- reach the live module object so the swapped export is what projectManager.ts's compiled call site reads
  const msbuildEvaluator = require(MSBUILD_EVALUATOR_PATH) as {
    evaluateProject: (projectFile: string) => Promise<unknown>;
  };
  const originalEvaluate = msbuildEvaluator.evaluateProject;
  msbuildEvaluator.evaluateProject = () => Promise.resolve(null);

  try {
    await fn({
      fireCreate: (uri) => projectWatcherListeners.create?.(uri),
      fireChange: (uri) => projectWatcherListeners.change?.(uri),
      fireDelete: (uri) => projectWatcherListeners.delete?.(uri),
    });
  } finally {
    msbuildEvaluator.evaluateProject = originalEvaluate;
    for (const [name, descriptor] of originalMembers) {
      Object.defineProperty(vscode.workspace, name, descriptor);
    }
  }
}

function fakeProjectUri(projectFile: string): vscode.Uri {
  return { fsPath: projectFile, toString: () => `file://${projectFile}` } as vscode.Uri;
}

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

  suite('project/solution file watcher (issue #1009)', () => {
    test('re-evaluates the owning project when a .csproj is edited', async function () {
      this.timeout(15000);

      await withStubbedProjectEnvironment(async ({ fireCreate, fireChange }) => {
        const { client, notifications } = fakeClient();
        const projectFile = path.join('C:', 'work', 'App.csproj');
        const uri = fakeProjectUri(projectFile);

        const manager = new ProjectManager(client);
        try {
          fireCreate(uri);
          await waitFor(
            () =>
              countNotifications(notifications, ReqnrollMethods.projectLoaded, projectFile) === 1,
          );

          // The bug (issue #1009): no onDidChange handler is wired for project/solution files,
          // so editing the .csproj sends nothing and the server's membership/binding data for
          // this project stays stale until some other event or a window reload.
          fireChange(uri);
          await waitFor(
            () =>
              countNotifications(notifications, ReqnrollMethods.projectLoaded, projectFile) === 2,
          );

          assert.strictEqual(
            countNotifications(notifications, ReqnrollMethods.projectLoaded, projectFile),
            2,
            'editing a .csproj should resend reqnroll/projectLoaded for its owning project',
          );
        } finally {
          manager.dispose();
        }
      });
    });

    test('still registers on create and unregisters on delete', async function () {
      this.timeout(15000);

      await withStubbedProjectEnvironment(async ({ fireCreate, fireDelete }) => {
        const { client, notifications } = fakeClient();
        const projectFile = path.join('C:', 'work', 'Added.csproj');
        const uri = fakeProjectUri(projectFile);

        const manager = new ProjectManager(client);
        try {
          fireCreate(uri);
          await waitFor(
            () =>
              countNotifications(notifications, ReqnrollMethods.projectLoaded, projectFile) === 1,
          );

          fireDelete(uri);
          await waitFor(
            () =>
              countNotifications(notifications, ReqnrollMethods.projectUnloaded, projectFile) === 1,
          );

          assert.strictEqual(
            countNotifications(notifications, ReqnrollMethods.projectUnloaded, projectFile),
            1,
          );
        } finally {
          manager.dispose();
        }
      });
    });
  });
});
