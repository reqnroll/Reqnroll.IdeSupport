import * as path from 'path';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { evaluateProject, ProjectFileItem, ProjectProperties } from './msbuildEvaluator';
import { ReqnrollMethods } from './lspMethods';

// Mirrors ProjectFilesKind / ProjectFileRole in
// src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/ReqnrollProjectFilesParams.cs
const enum ProjectFilesKind {
  Baseline = 0,
  Delta = 1,
}
const enum ProjectFileRole {
  Feature = 0,
  Binding = 1,
}

// A burst of file-system events (e.g. a git checkout, a template extraction) collapses into a
// single re-evaluation per project rather than one `dotnet msbuild` run per event.
const RESEND_DEBOUNCE_MS = 500;

/**
 * Finds the workspace folder that contains the given project file path — the deepest/longest
 * matching folder wins, so a project inside a nested workspace folder (one folder open inside
 * another) resolves to its own, more specific folder rather than the outer one. Case-insensitive
 * and separator-bounded (a trailing path separator is appended to each folder before matching) so
 * a folder name that's a plain string-prefix of a sibling's name (e.g. `Foo` vs `FooBar`) can't
 * collide, mirroring findOwningProjectFile's matching below.
 * Falls back to the first workspace folder, or to the project file itself when there are no
 * workspace folders at all. Pure function (folders passed in) so it's directly testable without
 * a running Extension Host — see projectManager.test.ts.
 */
export function resolveWorkspaceFolder(projectFile: string, folders: readonly string[]): string {
  if (folders.length === 0) return projectFile;

  const projectFileLower = projectFile.toLowerCase();
  let best: string | undefined;
  let bestLen = -1;
  for (const folder of folders) {
    const prefix = folder.endsWith(path.sep) ? folder : folder + path.sep;
    if (projectFileLower.startsWith(prefix.toLowerCase()) && prefix.length > bestLen) {
      best = folder;
      bestLen = prefix.length;
    }
  }

  return best ?? folders[0];
}

/**
 * Nearest known .csproj whose directory contains `filePath` (deepest match wins). Pure function
 * (known projects passed in) so it's directly testable without a running Extension Host.
 */
export function findOwningProjectFile(
  filePath: string,
  knownProjects: ReadonlySet<string>,
): string | undefined {
  let best: string | undefined;
  let bestLen = 0;
  for (const projectFile of knownProjects) {
    if (!projectFile.endsWith('.csproj')) continue;
    const folder = path.dirname(projectFile) + path.sep;
    if (filePath.toLowerCase().startsWith(folder.toLowerCase()) && folder.length > bestLen) {
      best = projectFile;
      bestLen = folder.length;
    }
  }
  return best;
}

/**
 * Manages custom LSP notifications (reqnroll/projectLoaded, projectUnloaded, projectFiles)
 * for the VS Code extension.
 *
 * v1: sends projectLoaded with empty assembly/TFM/package fields — server
 *     falls back to folder-prefix file scanning.
 * v2: runs `dotnet msbuild` to populate OutputAssemblyPath, TFM, and
 *     package references, enabling reflection-based binding discovery.
 * v3: also sends reqnroll/projectFiles (baseline) so the server's per-file membership index
 *     (see LspWorkspaceScopeManager / MembershipState.cs) can leave every file `Pending` and
 *     folder-prefix-fallback state, matching what VS's VsProjectEventMonitor already provides.
 *     Without this, linked files outside a project's folder and files excluded via .csproj
 *     globs are handled by best-effort folder-prefix matching indefinitely.
 * v4: re-runs discovery when workspace folders are added/removed after activation (VS Code's
 *     own `workspace/didChangeWorkspaceFolders` LSP notification is already sent automatically
 *     by vscode-languageclient's WorkspaceFoldersFeature; this only covers *our* project
 *     discovery, which the library has no knowledge of).
 * v5 (added, then reverted — see below): forwarded output-assembly (re)build events to the server
 *     as a client-side `RelativePattern(outputDir, '*.dll')` watcher per project, sending a
 *     synthetic `workspace/didChangeWatchedFiles` notification directly. This was added by PR #26
 *     to fix issue #2 (a project registered before its first `dotnet build` — DLL missing —
 *     permanently failed Connector-based binding discovery with no retry), on the assumption that
 *     the server's own standard dynamic registration for `**\/bin/**\/*.dll`
 *     (`WatchedFilesHandler.cs`) wasn't reliably delivered by `vscode-languageclient`'s generic
 *     `FileSystemWatcherFeature` — specifically that VS Code's `files.watcherExclude` (which many
 *     users add for `bin/`/`obj/`) suppresses it.
 *
 *     Issue #31 (Q9) investigated that assumption directly. Two findings: (1) `files.watcherExclude`
 *     covering `bin/` does suppress *any* `createFileSystemWatcher`-based approach, including this
 *     client-side glue — narrowing the glob doesn't route around it, so the glue provided no
 *     resilience against the one confirmed failure mode. (2) Issue #2's original symptom does not
 *     reproduce today: an Extension-Host recreation (a real, unbuilt project registered before its
 *     first build, then built) recovered correctly with this client-side glue *disabled* — the
 *     canonical path (server dynamic registration + `vscode-languageclient`'s own watcher) is
 *     sufficient on its own under default settings. Confirmed again in live manual testing (real
 *     `dotnet build`s against a real multi-project solution, one incremental and one full/clean
 *     rebuild producing 150+ dependency-DLL noise) — both correctly detected and filtered by the
 *     server using canonical registration alone, per the server's own file log
 *     (`HandleOutputAssemblyChange: ... triggering discovery for '<project>'`). This class was
 *     reverted to that canonical path; VS's `VsProjectEventMonitor` doesn't need any of this — it
 *     hooks `DTE.Events.BuildEvents.OnBuildDone` directly.
 */
export class ProjectManager {
  private readonly _client: LanguageClient;
  private readonly _watcher: vscode.FileSystemWatcher;
  private readonly _fileWatcher: vscode.FileSystemWatcher;
  private readonly _knownProjects = new Set<string>();
  private readonly _resendTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private _disposables: vscode.Disposable[] = [];

  constructor(client: LanguageClient) {
    this._client = client;

    // Watch for project/solution file changes across all workspace folders. Their FileSystemWatcher.dispose()
    // (below) already tears down these listeners, so their per-listener Disposables aren't tracked separately.
    this._watcher = vscode.workspace.createFileSystemWatcher('**/*.{csproj,slnx,sln}');
    this._watcher.onDidCreate((uri) => void this.onProjectCreated(uri));
    this._watcher.onDidDelete((uri) => void this.onProjectDeleted(uri));

    // Re-evaluate a project's file membership when a .cs/.feature file is added or removed
    // under it, so the server's membership index doesn't go stale after the initial baseline
    // (e.g. a new step-definition file, or a deleted/renamed .feature file).
    this._fileWatcher = vscode.workspace.createFileSystemWatcher('**/*.{cs,feature}');
    this._fileWatcher.onDidCreate((uri) => this.scheduleResend(uri));
    this._fileWatcher.onDidDelete((uri) => this.scheduleResend(uri));

    // Re-run discovery when a workspace folder is added (e.g. a multi-root workspace gains a
    // folder with its own .csproj/.feature files), and drop projects under a removed folder.
    this._disposables.push(
      vscode.workspace.onDidChangeWorkspaceFolders(
        (event) => void this.onWorkspaceFoldersChanged(event),
      ),
    );

    // Discover any projects already present in the workspace
    void this.discoverExistingProjects();
  }

  /** The `.csproj`/`.slnx`/`.sln` files currently known to this manager, keyed by absolute path. */
  getKnownProjects(): ReadonlySet<string> {
    return this._knownProjects;
  }

  /** Releases watchers, pending timers, and event subscriptions. */
  dispose(): void {
    this._watcher.dispose();
    this._fileWatcher.dispose();
    for (const timer of this._resendTimers.values()) clearTimeout(timer);
    this._resendTimers.clear();
    for (const d of this._disposables) d.dispose();
    this._disposables = [];
    this._knownProjects.clear();
  }

  // ── Discovery ─────────────────────────────────────────────────────────

  /**
   * Scans all workspace folders for .csproj files and registers them.
   */
  private async discoverExistingProjects(): Promise<void> {
    const patterns = ['**/*.csproj', '**/*.slnx', '**/*.sln'];
    const uris = new Set<string>();

    for (const pattern of patterns) {
      const matches = await vscode.workspace.findFiles(pattern, '**/node_modules/**');
      for (const uri of matches) {
        uris.add(uri.toString());
      }
    }

    for (const uriStr of uris) {
      const uri = vscode.Uri.parse(uriStr);
      await this.registerProject(uri);
    }
  }

  // ── Event handlers ────────────────────────────────────────────────────

  private async onProjectCreated(uri: vscode.Uri): Promise<void> {
    await this.registerProject(uri);
  }

  private async onProjectDeleted(uri: vscode.Uri): Promise<void> {
    await this.unregisterProject(uri);
  }

  /**
   * Re-scans for newly added workspace folders (dedup-safe: registerProject skips projects
   * already known) and unregisters any known project that lived under a removed folder.
   */
  private async onWorkspaceFoldersChanged(
    event: vscode.WorkspaceFoldersChangeEvent,
  ): Promise<void> {
    if (event.added.length > 0) {
      await this.discoverExistingProjects();
    }

    for (const removed of event.removed) {
      const folder = removed.uri.fsPath + path.sep;
      const toUnload = [...this._knownProjects].filter((p) => p.startsWith(folder));
      for (const projectFile of toUnload) {
        await this.unregisterProject(vscode.Uri.file(projectFile));
      }
    }
  }

  /**
   * Debounces a full membership re-evaluation for the project (if any) that owns `uri`.
   * No-op for files under a project VS Code hasn't discovered yet — that project's own
   * registration will send a baseline that already includes the file.
   */
  private scheduleResend(uri: vscode.Uri): void {
    const projectFile = findOwningProjectFile(uri.fsPath, this._knownProjects);
    if (!projectFile) return;

    const existing = this._resendTimers.get(projectFile);
    if (existing) clearTimeout(existing);

    this._resendTimers.set(
      projectFile,
      setTimeout(() => {
        this._resendTimers.delete(projectFile);
        void this.resendProjectFiles(projectFile);
      }, RESEND_DEBOUNCE_MS),
    );
  }

  /**
   * Re-runs MSBuild evaluation for an already-registered project and resends both
   * `reqnroll/projectLoaded` and its `reqnroll/projectFiles` baseline. Used for `.cs`/`.feature`
   * additions/removals, where the file *membership* itself may have changed.
   */
  private async resendProjectFiles(projectFile: string): Promise<void> {
    const { props } = await this.sendProjectLoaded(projectFile);
    if (!props) return; // msbuild unavailable — index stays Pending, same as v1 fallback
    await this.sendProjectFilesBaseline(projectFile, props.targetFrameworkMoniker, props.files);
  }

  // ── Notification sending ──────────────────────────────────────────────

  /**
   * Sends reqnroll/projectLoaded (and, when MSBuild evaluation succeeds, reqnroll/projectFiles)
   * for the given project file. Falls back to empty projectLoaded fields and no projectFiles
   * baseline when msbuild is unavailable (v1 compat — the file stays folder-prefix `Pending`).
   * Duplicate-safe: skips if already registered.
   */
  private async registerProject(uri: vscode.Uri): Promise<void> {
    const projectFile = uri.fsPath;
    if (this._knownProjects.has(projectFile)) return;

    // Only .csproj files get MSBuild evaluation; .slnx/.sln just mark as known
    if (!projectFile.endsWith('.csproj')) {
      this._knownProjects.add(projectFile);
      return;
    }

    const result = await this.sendProjectLoaded(projectFile);
    if (!result.sent) return; // notification failed — do not mark known (matches pre-v5 behavior)
    this._knownProjects.add(projectFile);

    // v3: populate the server's per-file membership index, same data VS's
    // VsProjectEventMonitor.SendInitialProjectsAsync sends via TrySendProjectFilesAsync.
    if (result.props) {
      await this.sendProjectFilesBaseline(
        projectFile,
        result.props.targetFrameworkMoniker,
        result.props.files,
      );
    }
  }

  /**
   * Evaluates `projectFile` via MSBuild and sends `reqnroll/projectLoaded` with the result
   * (empty fields when msbuild is unavailable — v1 compat, file stays folder-prefix `Pending`).
   * Shared by {@link registerProject} (first discovery) and {@link resendProjectFiles} (v5:
   * re-evaluating after the output assembly is built, so `outputAssemblyPath` reaches the server
   * even though it didn't exist at initial registration time).
   *
   * `sent` is `false` only when the notification itself failed to send (e.g. the client isn't
   * running) — distinct from `props` being `null`, which means msbuild evaluation failed/was
   * unavailable but the (empty-field) notification still went out successfully.
   */
  private async sendProjectLoaded(
    projectFile: string,
  ): Promise<{ sent: boolean; props: ProjectProperties | null }> {
    const folders = (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath);
    const workspaceFolder = resolveWorkspaceFolder(projectFile, folders);
    const projectFolder = path.dirname(projectFile);

    const props = await evaluateProject(projectFile);

    const params = {
      workspaceFolder,
      projectFile,
      projectFolder,
      outputAssemblyPath: props?.outputAssemblyPath ?? '',
      targetFrameworkMoniker: props?.targetFrameworkMoniker ?? '',
      defaultNamespace: props?.defaultNamespace ?? '',
      packageReferences:
        props?.packageReferences.map((r) => ({
          packageId: r.packageId,
          version: r.version,
          installPath: '',
        })) ?? [],
    };

    try {
      await this._client.sendNotification(ReqnrollMethods.projectLoaded, params);
    } catch (err) {
      console.error(`ProjectManager: failed to send projectLoaded for ${projectFile}:`, err);
      return { sent: false, props: null };
    }

    return { sent: true, props };
  }

  /** Sends a reqnroll/projectFiles baseline (full snapshot) for one project. */
  private async sendProjectFilesBaseline(
    projectFile: string,
    targetFrameworkMoniker: string,
    files: readonly ProjectFileItem[],
  ): Promise<void> {
    const params = {
      projectFile,
      targetFrameworkMoniker,
      kind: ProjectFilesKind.Baseline,
      files: files.map((f) => ({
        path: f.path,
        role: f.role === 'feature' ? ProjectFileRole.Feature : ProjectFileRole.Binding,
        added: true,
      })),
    };

    try {
      await this._client.sendNotification(ReqnrollMethods.projectFiles, params);
    } catch (err) {
      console.error(`ProjectManager: failed to send projectFiles for ${projectFile}:`, err);
    }
  }

  /**
   * Sends reqnroll/projectUnloaded for the given project file.
   * No-op if not in the known set.
   */
  private async unregisterProject(uri: vscode.Uri): Promise<void> {
    const projectFile = uri.fsPath;
    if (!this._knownProjects.has(projectFile)) return;

    const params = { projectFile };

    try {
      await this._client.sendNotification(ReqnrollMethods.projectUnloaded, params);
      this._knownProjects.delete(projectFile);
    } catch (err) {
      console.error(`ProjectManager: failed to send projectUnloaded for ${projectFile}:`, err);
    }
  }
}
