import * as assert from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';
import { State } from 'vscode-languageclient';
import { LanguageClient, StateChangeEvent } from 'vscode-languageclient/node';
import {
  resolveWorkspaceFolder,
  findOwningProjectFile,
  ProjectManager,
} from '../../lsp/projectManager';
import { ReqnrollMethods } from '../../lsp/lspMethods';
import { ProjectProperties } from '../../lsp/msbuildEvaluator';

/**
 * A LanguageClient stand-in exposing only what ProjectManager touches: `state`/`onDidChangeState`
 * (driven by {@link move}, mirroring vscode-languageclient 10.x's restart sequence) and
 * `sendNotification` (recorded).
 */
function createFakeClient(initial: State) {
  let state = initial;
  const listeners: ((e: StateChangeEvent) => void)[] = [];
  const sent: { method: string; projectFile: string }[] = [];
  const client = {
    get state() {
      return state;
    },
    onDidChangeState: (l: (e: StateChangeEvent) => void) => {
      listeners.push(l);
      return { dispose: () => listeners.splice(listeners.indexOf(l), 1) };
    },
    sendNotification: (method: string, params: { projectFile: string }) => {
      sent.push({ method, projectFile: params.projectFile });
      return Promise.resolve();
    },
  } as unknown as LanguageClient;
  return {
    client,
    sent,
    move: (to: State) => {
      const oldState = state;
      state = to;
      for (const l of [...listeners]) l({ oldState, newState: to });
    },
  };
}

/** What vscode-languageclient emits when its default error handler restarts a crashed server. */
function crashAndRestart(move: (to: State) => void): void {
  move(State.Stopped);
  move(State.Starting);
  move(State.Running);
}

// Round-tripped through Uri.fsPath (as ProjectManager does) so the drive-letter case matches.
const csproj = vscode.Uri.file(path.join('C:', 'work', 'App', 'App.csproj')).fsPath;
const sln = vscode.Uri.file(path.join('C:', 'work', 'App.sln')).fsPath;

const props: ProjectProperties = {
  outputAssemblyPath: path.join('C:', 'work', 'App', 'bin', 'App.dll'),
  targetFrameworkMoniker: '.NETCoreApp,Version=v8.0',
  defaultNamespace: 'App',
  packageReferences: [],
  files: [{ path: path.join('C:', 'work', 'App', 'A.feature'), role: 'feature' }],
};

/** Lets pending promise continuations (discovery, evaluation, sends) run. */
async function settle(): Promise<void> {
  for (let i = 0; i < 20; i++) await new Promise((r) => setImmediate(r));
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

  // Issue #997: vscode-languageclient restarts a crashed server on the same LanguageClient
  // (Running -> Stopped -> Starting -> Running), so the new server must be re-sent project state.
  suite('server restart (issue #997)', () => {
    const managers: ProjectManager[] = [];
    teardown(() => {
      for (const m of managers.splice(0)) m.dispose();
    });

    function createManager(
      client: LanguageClient,
      evaluate: () => Promise<ProjectProperties | null> = () => Promise.resolve(props),
    ): ProjectManager {
      const manager = new ProjectManager(client, {
        findProjectFiles: () => Promise.resolve([vscode.Uri.file(csproj), vscode.Uri.file(sln)]),
        evaluateProject: evaluate,
      });
      managers.push(manager);
      return manager;
    }

    const projectLoaded = (sent: { method: string }[]) =>
      sent.filter((s) => s.method === ReqnrollMethods.projectLoaded).length;
    const projectFiles = (sent: { method: string }[]) =>
      sent.filter((s) => s.method === ReqnrollMethods.projectFiles).length;

    test('initial discovery sends projectLoaded and projectFiles once per .csproj', async () => {
      const { client, sent } = createFakeClient(State.Running);
      createManager(client);
      await settle();

      assert.strictEqual(projectLoaded(sent), 1);
      assert.strictEqual(projectFiles(sent), 1);
    });

    test('a restart re-sends projectLoaded and projectFiles exactly once', async () => {
      const { client, sent, move } = createFakeClient(State.Running);
      const manager = createManager(client);
      await settle();
      sent.length = 0;

      crashAndRestart(move);
      await settle();

      assert.deepStrictEqual(
        sent.map((s) => s.method),
        [ReqnrollMethods.projectLoaded, ReqnrollMethods.projectFiles],
      );
      assert.ok(sent.every((s) => s.projectFile === csproj));
      assert.deepStrictEqual([...manager.getKnownProjects()].sort(), [csproj, sln].sort());
    });

    test('each of several restarts re-sends project state once', async () => {
      const { client, sent, move } = createFakeClient(State.Running);
      createManager(client);
      await settle();
      sent.length = 0;

      crashAndRestart(move);
      await settle();
      crashAndRestart(move);
      await settle();

      assert.strictEqual(projectLoaded(sent), 2);
      assert.strictEqual(projectFiles(sent), 2);
    });

    test('the first Running (manager created before the client started) does not re-send', async () => {
      const { client, sent, move } = createFakeClient(State.Starting);
      createManager(client);
      await settle();

      move(State.Running);
      await settle();

      assert.strictEqual(projectLoaded(sent), 1);
      assert.strictEqual(projectFiles(sent), 1);
    });

    test('a discovery in flight when the server restarts does not double-send', async () => {
      const { client, sent, move } = createFakeClient(State.Running);
      let release: (() => void) | undefined;
      let calls = 0;
      createManager(client, () => {
        calls += 1;
        // Hold the first (pre-restart) evaluation until after the restart has re-discovered.
        if (calls === 1) {
          return new Promise((resolve) => (release = () => resolve(props)));
        }
        return Promise.resolve(props);
      });
      await settle();

      crashAndRestart(move);
      await settle();
      release!();
      await settle();

      assert.strictEqual(projectLoaded(sent), 1);
      assert.strictEqual(projectFiles(sent), 1);
    });
  });
});
