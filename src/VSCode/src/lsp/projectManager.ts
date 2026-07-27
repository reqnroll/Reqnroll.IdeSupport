import * as path from 'path';
import * as vscode from 'vscode';
import {
  DidChangeWatchedFilesNotification,
  FileChangeType,
  LanguageClient,
} from 'vscode-languageclient/node';
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
 * Finds the workspace folder that contains the given project file path.
 * Falls back to the first workspace folder, or to the project file itself when there are no
 * workspace folders at all. Pure function (folders passed in) so it's directly testable without
 * a running Extension Host — see projectManager.test.ts.
 */
export function resolveWorkspaceFolder(projectFile: string, folders: readonly string[]): string {
  if (folders.length === 0) return projectFile;

  for (const folder of folders) {
    if (projectFile.startsWith(folder)) {
      return folder;
    }
  }

  return folders[0];
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
 * v5: forwards output-assembly (re)build events to the server. Connector-based binding
 *     discovery (`ConnectorBindingRegistryProvider`, server-side) reflects over the project's
 *     `OutputAssemblyPath` DLL; if that DLL doesn't exist yet when the initial
 *     `reqnroll/projectLoaded` baseline is sent (e.g. a freshly cloned repo opened before its
 *     first `dotnet build`), discovery fails once with "Output assembly not found". The server
 *     already declares a standard `workspace/didChangeWatchedFiles` registration for
 *     `**\/bin/**\/*.dll` (`WatchedFilesHandler.cs`) specifically to retry discovery once the
 *     assembly appears — but whether each IDE's LSP client actually *delivers* those dynamically
 *     registered watched-file events reliably is an open question (Q9 in
 *     docs/LSP-IDE-Support-Open-Questions.md); VS Code's `files.watcherExclude` commonly excludes
 *     `bin/`/`obj/` from the file watching a dynamically-registered `FileSystemWatcherFeature`
 *     relies on. Rather than resending the full `reqnroll/projectLoaded` + baseline (which re-runs
 *     `dotnet msbuild` and duplicates work the server can already do with the `OutputAssemblyPath`
 *     it was given at initial registration — that path is computed from MSBuild properties and is
 *     correct even before the file exists), this watcher sends the *same standard*
 *     `workspace/didChangeWatchedFiles` notification directly, landing on the server's existing
 *     handler with no extra round trip. VS's `VsProjectEventMonitor` doesn't need this fallback —
 *     it hooks `DTE.Events.BuildEvents.OnBuildDone` directly.
 */
export class ProjectManager {
  private readonly _client: LanguageClient;
  private readonly _watcher: vscode.FileSystemWatcher;
  private readonly _fileWatcher: vscode.FileSystemWatcher;
  private readonly _outputWatchers = new Map<string, vscode.FileSystemWatcher>();
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

  /** Releases watchers, pending timers, and event subscriptions. */
  dispose(): void {
    this._watcher.dispose();
    this._fileWatcher.dispose();
    for (const watcher of this._outputWatchers.values()) watcher.dispose();
    this._outputWatchers.clear();
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
   * additions/removals, where the file *membership* itself may have changed — not for output
   * assembly rebuilds (see {@link notifyOutputAssemblyChanged}), which don't need a fresh MSBuild
   * evaluation since `OutputAssemblyPath` doesn't change just because the DLL was rebuilt.
   */
  private async resendProjectFiles(projectFile: string): Promise<void> {
    const { props } = await this.sendProjectLoaded(projectFile);
    if (!props) return; // msbuild unavailable — index stays Pending, same as v1 fallback
    await this.sendProjectFilesBaseline(projectFile, props.targetFrameworkMoniker, props.files);

    // v5: arm the output-assembly watcher if this resend is what first discovered
    // outputAssemblyPath (e.g. initial registration ran before `dotnet restore`). Guarded so a
    // project resent more than once (this path isn't one-shot like registerProject) doesn't leak
    // a duplicate watcher.
    if (props.outputAssemblyPath && !this._outputWatchers.has(projectFile)) {
      this.watchProjectOutputPath(projectFile, props.outputAssemblyPath);
    }
  }

  /**
   * Forwards a `bin/**` DLL create/change event to the server as a standard
   * `workspace/didChangeWatchedFiles` notification (v5, see class doc). No-ops for assemblies
   * that don't belong to a known project (dependency DLLs, other tools' output). Deliberately
   * does *not* re-run MSBuild or resend `reqnroll/projectLoaded`/`reqnroll/projectFiles` — the
   * server's `WatchedFilesHandler` already has the project's `OutputAssemblyPath` from its
   * original registration (computed from MSBuild properties, valid whether or not the file
   * exists yet) and can retry discovery from just the URI + change type.
   */
  private notifyOutputAssemblyChanged(uri: vscode.Uri, changeType: FileChangeType): void {
    if (!findOwningProjectFile(uri.fsPath, this._knownProjects)) return;

    void this._client
      .sendNotification(DidChangeWatchedFilesNotification.type, {
        changes: [{ uri: uri.toString(), type: changeType }],
      })
      .catch((err: unknown) => {
        console.error(
          `ProjectManager: failed to notify output assembly change for ${uri.fsPath}:`,
          err,
        );
      });
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

      // v5: Forward output-assembly build events to the server as a standard
      // workspace/didChangeWatchedFiles notification. Instead of a workspace-wide
      // **/bin/**/*.dll glob that fires on every project's bin/ output (including
      // unrelated dependency DLLs), watch only this project's output directory.
      if (result.props.outputAssemblyPath) {
        this.watchProjectOutputPath(projectFile, result.props.outputAssemblyPath);
      }
    }
  }

  private watchProjectOutputPath(projectFile: string, outputAssemblyPath: string): void {
    const outputDir = path.dirname(outputAssemblyPath);

    // Use a RelativePattern scoped to the project's output directory instead of a
    // workspace-wide **/bin/**/*.dll glob that fires on every project's DLL output.
    const watcher = vscode.workspace.createFileSystemWatcher(
      new vscode.RelativePattern(outputDir, '*.dll'),
    );
    watcher.onDidCreate((uri) => this.notifyOutputAssemblyChanged(uri, FileChangeType.Created));
    watcher.onDidChange((uri) => this.notifyOutputAssemblyChanged(uri, FileChangeType.Changed));

    this._outputWatchers.set(projectFile, watcher);
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

    // Dispose the scoped output-assembly watcher for this project
    const watcher = this._outputWatchers.get(projectFile);
    if (watcher) {
      watcher.dispose();
      this._outputWatchers.delete(projectFile);
    }

    const params = { projectFile };

    try {
      await this._client.sendNotification(ReqnrollMethods.projectUnloaded, params);
      this._knownProjects.delete(projectFile);
    } catch (err) {
      console.error(`ProjectManager: failed to send projectUnloaded for ${projectFile}:`, err);
    }
  }
}
