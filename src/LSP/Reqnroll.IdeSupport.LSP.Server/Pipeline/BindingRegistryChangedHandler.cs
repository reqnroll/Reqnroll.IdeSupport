using System.Diagnostics;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Handles the three binding-registry-change events (issue #577) by re-parsing feature files
/// that belong to the affected project, then publishing a <see cref="MatchCacheChangedNotification"/>
/// for each open file so that semantic tokens are refreshed against the new binding registry.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Handle(BindingRegistryReplacedNotification, CancellationToken)"/> (full connector
/// run, deferred baseline rescan, or membership-baseline arrival): all feature files owned by the
/// project are scanned — including files not currently open in the editor — so that the binding
/// match cache is workspace-complete for Find Step Definition Usages / Find All References. The
/// file list is obtained from the membership index (I1) when a baseline has been received;
/// otherwise it falls back to a folder glob for backwards compatibility with clients that do not
/// send <c>reqnroll/projectFiles</c>.
/// </para>
/// <para>
/// <see cref="Handle(BindingRegistryPatchedNotification, CancellationToken)"/> (incremental
/// Roslyn per-file patch on a <c>.cs</c> edit, or a membership delta): only the currently open
/// feature files owned by the project are re-parsed immediately; closed files are rescanned after
/// a debounce.
/// </para>
/// <para>
/// <see cref="Handle(ProjectBindingFilesRemovedNotification, CancellationToken)"/>: purges stale
/// entries for binding files that left the project, independent of either reparse path.
/// </para>
/// </remarks>
public class BindingRegistryChangedHandler :
    INotificationHandler<BindingRegistryReplacedNotification>,
    INotificationHandler<BindingRegistryPatchedNotification>,
    INotificationHandler<ProjectBindingFilesRemovedNotification>
{
    private readonly IDocumentBufferService         _documentBufferService;
    private readonly ICSharpFileTextCache           _csharpFileTextCache;
    private readonly IGherkinDocumentTaggerService   _taggerService;
    private readonly ILspWorkspaceScopeManager       _scopeManager;
    private readonly ILanguageServerFacade            _languageServer;
    private readonly ClientIdeContext                 _clientIde;
    private readonly IFeatureDocumentReparser         _reparser;
    private readonly ICSharpBindingDiscoveryService   _csharpDiscoveryService;
    private readonly IFeatureRescanDebouncer          _rescanDebouncer;
    private readonly IParseCoordinator         _parseCoordinator;
    private readonly IIdeSupportLogger                  _logger;
    private readonly IOperationDurationRecorder         _recorder;
    private readonly IFileSystemForIDE                  _fileSystem;

    /// <summary>Initializes a new instance of the <see cref="BindingRegistryChangedHandler"/> class.</summary>
    public BindingRegistryChangedHandler(
        IDocumentBufferService documentBufferService,
        ICSharpFileTextCache csharpFileTextCache,
        IGherkinDocumentTaggerService taggerService,
        ILspWorkspaceScopeManager scopeManager,
        ILanguageServerFacade languageServer,
        ClientIdeContext clientIde,
        IFeatureDocumentReparser reparser,
        ICSharpBindingDiscoveryService csharpDiscoveryService,
        IFeatureRescanDebouncer rescanDebouncer,
        IParseCoordinator parseCoordinator,
        IIdeSupportLogger logger,
        IFileSystemForIDE fileSystem,
        IOperationDurationRecorder? recorder = null)
    {
        _documentBufferService  = documentBufferService;
        _csharpFileTextCache    = csharpFileTextCache;
        _taggerService          = taggerService;
        _scopeManager           = scopeManager;
        _languageServer         = languageServer;
        _clientIde              = clientIde;
        _reparser               = reparser;
        _csharpDiscoveryService = csharpDiscoveryService;
        _rescanDebouncer        = rescanDebouncer;
        _parseCoordinator       = parseCoordinator;
        _logger                 = logger;
        _fileSystem             = fileSystem;
        _recorder               = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>
    /// Handles a full registry replacement: re-runs Roslyn source-level discovery to refresh
    /// bindings from open/edited <c>.cs</c> files that may not be reflected in the just-loaded
    /// compiled DLL, scans every closed feature file in the project, reparses every open one, and
    /// asks the client to refresh its code lens.
    /// </summary>
    public async Task Handle(
        BindingRegistryReplacedNotification notification,
        CancellationToken cancellationToken)
    {
        // Performance Verification (Layer 4): time the binding-registry reconciliation —
        // the pipeline behind several cross-project binding-ownership bugs (issue #113).
        // Manual Stopwatch (not Measure's using-scope) because the file/step counts below — the
        // issue #471 investigation's "what grew" tag — are only known once the scan/reparse calls
        // return, not at the top of this method.
        var startTimestamp = Stopwatch.GetTimestamp();
        var scannedFileCount = 0;
        var reparsedFileCount = 0;
        try
        {
            // The Connector provides bindings from the compiled DLL, which may be stale if the
            // user renamed/edited bindings without rebuilding. Source-level discovery replaces
            // the stale compiled entries with fresh source-level data, preventing "Step
            // definition not found" (and the inverse, a step still shown as bound to a renamed
            // binding) on files edited but not rebuilt.
            await RediscoverCsFilesAsync(notification.Project, cancellationToken)
                .ConfigureAwait(false);

            scannedFileCount = await ScanAllFeatureFilesAsync(notification.Project, cancellationToken)
                .ConfigureAwait(false);

            reparsedFileCount = await ReparseOpenFilesAsync(notification.Project, cancellationToken)
                .ConfigureAwait(false);

            // VS package auto-load / startup race avoidance, piece 2b: after the binding registry
            // is populated (Connector run complete), ask the client to refresh its code lens.
            // Without this, a .cs file that was the
            // foreground editor at startup keeps the (count-less) code lenses it rendered before the
            // server was ready, until the user navigates away and back to re-realize the view.
            await RequestCodeLensRefreshAsync(notification.Project, isFullReplacement: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _recorder.Record(
                LspMethodNames.InternalBindingRegistryReconcile,
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                detail: $"scannedFiles={scannedFileCount} reparsedFiles={reparsedFileCount}");
        }
    }

    /// <summary>
    /// Handles an incremental registry patch: schedules a debounced closed-file rescan (a burst of
    /// edits collapses into one rescan after they settle) and reparses every currently open
    /// feature file owned by the project immediately.
    /// </summary>
    public async Task Handle(
        BindingRegistryPatchedNotification notification,
        CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var reparsedFileCount = 0;
        try
        {
            // An incremental Roslyn patch that changed a binding's matched expression (added,
            // removed, or edited), or a membership delta. Closed feature files' cached usage
            // counts are now potentially stale, but re-running the whole-project rescan on every
            // keystroke would be wasteful, so it's debounced: a burst of triggers collapses into
            // one rescan after they settle.
            // (That debounced scan runs after this method returns, so its own file count is not
            // part of this measurement — ScanAllFeatureFilesAsync's own Measure/Record call
            // still logs it, just under a PERF line timestamped later.)
            var project = notification.Project;
            _rescanDebouncer.ScheduleRescan(project, async ct =>
            {
                await ScanAllFeatureFilesAsync(project, ct).ConfigureAwait(false);
                await RequestCodeLensRefreshAsync(project, isFullReplacement: false, ct).ConfigureAwait(false);
            });

            reparsedFileCount = await ReparseOpenFilesAsync(project, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _recorder.Record(
                LspMethodNames.InternalBindingRegistryReconcile,
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                detail: $"reparsedFiles={reparsedFileCount}");
        }
    }

    /// <summary>
    /// Handles binding files leaving a project's membership (issue #94) by purging their stale
    /// entries from the project's registry.
    /// </summary>
    public async Task Handle(
        ProjectBindingFilesRemovedNotification notification,
        CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await RemoveBindingFilesAsync(notification.Project, notification.Paths, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _recorder.Record(
                LspMethodNames.InternalBindingRegistryReconcile,
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                detail: $"removedFiles={notification.Paths.Count}");
        }
    }

    /// <summary>
    /// Purges the given binding-role <c>.cs</c> files from <paramref name="project"/>'s registry
    /// (issue #94). Called for files removed via a <c>reqnroll/projectFiles</c> delta -- e.g. the
    /// user deletes a step-definition file in the IDE. Visual Studio never sends
    /// <c>workspace/didChangeWatchedFiles</c> for this, so <see cref="WatchedFilesHandler"/>'s
    /// deletion path (the one place removal was previously wired up) never fires for it; this
    /// project-files-driven removal is what actually reaches VS. Uses
    /// <see cref="ICSharpBindingDiscoveryService.UpdateFromSourceForProjectAsync"/> (not
    /// <c>RemoveFileAsync</c>) because the membership index has already dropped the file by the
    /// time this runs, so owner-resolution would find nothing to remove; parsing empty text
    /// against the already-known project replaces the file's stale entries with none.
    /// </summary>
    private async Task RemoveBindingFilesAsync(
        LspReqnrollProject project,
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken)
    {
        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _csharpDiscoveryService
                    .UpdateFromSourceForProjectAsync(project, filePath, string.Empty, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInfo(
                    $"[Roslyn] Removed bindings for deleted file '{Path.GetFileName(filePath)}' " +
                    $"from project '{project.ProjectName}'.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    $"RemoveBindingFilesAsync: failed to remove bindings for '{filePath}': {ex.Message}");
            }
        }
    }

    /// <summary>Asks the client to re-pull C# step code lenses. See <see cref="CodeLensRefreshRequester"/> for the VS/non-VS branching and <see cref="Features.CodeLens.RefreshCodeLensParams.IsFullReplacement"/> for why <paramref name="isFullReplacement"/> matters.</summary>
    private Task RequestCodeLensRefreshAsync(
        LspReqnrollProject project, bool isFullReplacement, CancellationToken cancellationToken) =>
        CodeLensRefreshRequester.RequestRefreshAsync(
            _languageServer, _clientIde, _logger, project.ProjectName, isFullReplacement, cancellationToken);

    /// <summary>
    /// Hard cap on how many closed feature files one full-replacement scan will process (issue
    /// #580). On a workspace with more closed feature files than this, the excess files simply
    /// keep whatever match-cache state they already had (stale cached usage counts until the
    /// next full rebuild) rather than the scan growing unbounded with workspace size — this was
    /// previously the only unbounded loop in the reconciliation pipeline, confirmed live to peg
    /// a CPU core on a stress corpus with ~1,300 scenario nodes in a single file (a different
    /// axis of size than this cap addresses, but the same class of problem: nothing bounded the
    /// worst case).
    /// </summary>
    internal const int MaxClosedFeatureFilesPerScan = 2000;

    /// <summary>
    /// Wall-clock budget for one full-replacement scan (issue #580), independent of file count —
    /// bounds the cost even when file count is modest but reads are slow (e.g. a network drive).
    /// Backstop, not a substitute for <see cref="MaxClosedFeatureFilesPerScan"/>: the two bound
    /// different failure shapes (too many files vs. slow individual reads).
    /// </summary>
    internal static readonly TimeSpan MaxScanDuration = TimeSpan.FromSeconds(30);

    /// <summary>Returns the number of closed feature files actually attempted, for the caller's PERF-line size tag (issue #471 investigation) — may be less than the full closed-file set if the #580 cap or timeout below cut the scan short.</summary>
    private async Task<int> ScanAllFeatureFilesAsync(
        LspReqnrollProject project,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> allFeatureFiles;

        if (_scopeManager.HasBaselineForProject(project))
        {
            // I1: use the authoritative index — this correctly includes linked features
            // outside the project folder and excludes removed/conditional ones inside it.
            allFeatureFiles = _scopeManager.GetIndexedFeatureFiles(project);
        }
        else
        {
            // Legacy fallback: project has never sent reqnroll/projectFiles (e.g. VS Code
            // interim, or startup race before the first baseline arrives).
            var projectFolder = project.ProjectFolder;
            if (string.IsNullOrEmpty(projectFolder) || !_fileSystem.Directory.Exists(projectFolder))
                return 0;

            allFeatureFiles = _fileSystem.Directory
                .EnumerateFiles(projectFolder, "*.feature", SearchOption.AllDirectories)
                .ToList();
        }

        // Skip files already open — ReparseOpenFilesAsync will handle those via the buffer.
        var openUris = _documentBufferService.All
            .Select(b => b.Uri.GetFileSystemPath())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var closedFiles = allFeatureFiles
            .Where(f => !openUris.Contains(f))
            .ToList();

        if (closedFiles.Count > MaxClosedFeatureFilesPerScan)
        {
            _logger.LogWarning(
                $"ScanAllFeatureFiles: project '{project.ProjectName}' has {closedFiles.Count} " +
                $"closed feature file(s), exceeding the {MaxClosedFeatureFilesPerScan}-file scan " +
                "bound (issue #580) — scanning the first " +
                $"{MaxClosedFeatureFilesPerScan}; the remainder keep their existing cached usage " +
                "counts until the next full rebuild.");
            closedFiles = closedFiles.Take(MaxClosedFeatureFilesPerScan).ToList();
        }

        _logger.LogInfo(
            $"Full registry replacement — scanning {closedFiles.Count} closed feature file(s) " +
            $"for project '{project.ProjectName}'.");

        // Linked to the caller's own token so genuine external cancellation still propagates;
        // CancelAfter is this method's own backstop against a slow scan, not a replacement for
        // that (issue #580 — this loop previously had no way to bound its own wall-clock cost).
        // A cancellation here breaks the loop rather than throwing: the caller (Handle) still
        // needs to reparse open files and request a code-lens refresh afterward regardless of
        // whether the closed-file scan finished, timed out, or was externally cancelled.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(MaxScanDuration);
        var scanToken = timeoutCts.Token;

        var attempted = 0;
        foreach (var filePath in closedFiles)
        {
            if (scanToken.IsCancellationRequested)
            {
                var reason = cancellationToken.IsCancellationRequested ? "was cancelled" : "timed out";
                _logger.LogWarning(
                    $"ScanAllFeatureFiles: project '{project.ProjectName}' scan {reason} after " +
                    $"{attempted} of {closedFiles.Count} file(s) (issue #580).");
                break;
            }

            attempted++;
            try
            {
                var text = await _fileSystem.File.ReadAllTextAsync(filePath, scanToken).ConfigureAwait(false);
                var uri  = DocumentUri.FromFileSystemPath(filePath);
                await _taggerService.ScanClosedFileAsync(uri, text, project).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (scanToken.IsCancellationRequested)
            {
                var reason = cancellationToken.IsCancellationRequested ? "was cancelled" : "timed out";
                _logger.LogWarning(
                    $"ScanAllFeatureFiles: project '{project.ProjectName}' scan {reason} mid-read " +
                    $"after {attempted} of {closedFiles.Count} file(s) (issue #580).");
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning($"ScanAllFeatureFiles: could not scan '{filePath}': {ex.Message}");
            }
        }

        return attempted;
    }

    /// <summary>Returns the number of open feature files scheduled for reparse, for the caller's PERF-line size tag (issue #471 investigation).</summary>
    /// <remarks>
    /// Routes each buffer's reparse through <see cref="IParseCoordinator"/> instead of
    /// awaiting it inline (issue #471): this reconciliation is already reached via a detached,
    /// unawaited path (<c>BindingRegistryProviderRouter.OnProviderChanged</c>'s <c>_ =
    /// _mediator.Publish(...)</c>), so routing through the coordinator here isn't about freeing a
    /// dispatch lane -- it's so <c>FoldingRangeHandler</c>/<c>DocumentSymbolHandler</c>'s
    /// <c>WaitForReadyAsync</c> calls see a pending entry for a URI whether the reparse was
    /// triggered by a direct edit to that file or, as here, by a <c>.cs</c>-driven registry
    /// cascade reconciling it. Without this, the same no-refresh-capability race those two
    /// handlers guard against for direct edits would remain completely unaddressed for this path.
    /// As a result this now returns the number of files *scheduled*, not completed — the actual
    /// reparses run after this method (and its callers) return, so their duration is no longer
    /// part of this method's own timing.
    /// </remarks>
    private Task<int> ReparseOpenFilesAsync(
        LspReqnrollProject project,
        CancellationToken cancellationToken)
    {
        // Select open feature buffers that belong to the changed project.
        // Use the membership index when a baseline has been received (I1); fall back to
        // folder-prefix for projects that haven't sent reqnroll/projectFiles.
        var affectedBuffers = _documentBufferService.All
            .Where(b => IsOwnedByProject(b.Uri, project) && IsPrimaryOwner(b.Uri, project))
            .ToList();

        if (affectedBuffers.Count == 0)
        {
            _logger.LogVerbose(
                $"BindingRegistryChanged — no open feature files to reparse for '{project.ProjectName}'.");
            return Task.FromResult(0);
        }

        _logger.LogInfo(
            $"BindingRegistryChanged — scheduling reparse of {affectedBuffers.Count} open feature " +
            $"file(s) for project '{project.ProjectName}'.");

        foreach (var buffer in affectedBuffers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = buffer.Uri;
            var version = buffer.Version;
            _parseCoordinator.Schedule(uri, ct => _reparser.ReparseOpenDocumentAsync(uri, version, ct));
        }

        return Task.FromResult(affectedBuffers.Count);
    }

    /// <summary>
    /// Whether <paramref name="project"/> is the <em>primary</em> owner of <paramref name="uri"/> —
    /// the only owner whose registry change can affect this open document (issue #554).
    /// </summary>
    /// <remarks>
    /// A feature file linked into several projects has one owner per project, but an <em>open</em>
    /// document's match set is computed against its primary owner's registry and stored under the
    /// primary owner's key (see <c>GherkinDocumentTaggerService.ParseAsync</c>) — so a reparse
    /// driven by a non-primary owner's registry change recomputes a byte-identical match set into
    /// the identical cache key. That is pure waste, and until <c>ParseCoordinator</c> was fixed it
    /// was also the pair of same-URI parses that raced each other during startup. Nothing is lost
    /// by skipping it: the non-primary owner's own <c>(uri, project)</c> entry is not refreshed
    /// while the file is open either way, because <see cref="ScanAllFeatureFilesAsync"/> skips open
    /// files by design (the open buffer's pipeline owns that URI).
    /// Falls back to "yes" when no primary owner resolves at all, so a project whose baseline has
    /// not arrived still reparses its files rather than silently skipping them.
    /// </remarks>
    private bool IsPrimaryOwner(DocumentUri uri, LspReqnrollProject project)
    {
        var primary = _scopeManager.ResolvePrimaryOwner(uri);
        if (primary is null)
            return true;

        if (string.Equals(primary.ProjectFullName, project.ProjectFullName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(primary.TargetFrameworkMoniker, project.TargetFrameworkMoniker, StringComparison.Ordinal))
            return true;

        _logger.LogVerbose(
            $"BindingRegistryChanged — skipping reparse of '{uri}' for '{project.ProjectName}': " +
            $"'{primary.ProjectName}' is its primary owner and owns its match set.");
        return false;
    }

    private bool IsOwnedByProject(DocumentUri uri, LspReqnrollProject project)
    {
        if (_scopeManager.HasBaselineForProject(project))
        {
            // Index-driven ownership check (I1).
            var owners = _scopeManager.GetProjectsForUri(uri);
            return owners.Contains(project);
        }

        // Fallback: folder-prefix for projects without a baseline.
        return IsUnderProjectFolder(uri, project.ProjectFolder);
    }

    private static bool IsUnderProjectFolder(DocumentUri uri, string projectFolder)
        => PathUtils.IsUnderFolder(uri.GetFileSystemPath(), projectFolder);

    /// <summary>
    /// After a full registry replacement (which loads bindings from the compiled assembly),
    /// re-runs Roslyn source-level discovery on the project's <c>.cs</c> step-definition files
    /// so that source edited <em>since the last build</em> overrides the stale compiled bindings.
    /// <para>Covers two cases:</para>
    /// <list type="bullet">
    ///   <item>open, possibly-unsaved <c>.cs</c> buffers — reconciled unconditionally, since an
    ///   unsaved edit is never reflected in the DLL; and</item>
    ///   <item>closed <c>.cs</c> files on disk whose last-write time is newer than the output
    ///   assembly — i.e. edited then saved without a rebuild. This is the case that survives a VS
    ///   restart: the file is on disk but not open, so without this it would never override the
    ///   stale compiled binding.</item>
    /// </list>
    /// Files unchanged since the build are faithfully represented by the DLL and are skipped to
    /// bound the cost. Reconciliation is delegated to <see cref="ICSharpBindingDiscoveryService"/>,
    /// which patches the project's registry directly without consulting the membership index
    /// (the baseline may not have arrived yet at startup).
    /// </summary>
    private async Task RediscoverCsFilesAsync(LspReqnrollProject project, CancellationToken ct)
    {
        var filesToReconcile = CollectCsFilesToReconcile(project);
        if (filesToReconcile.Count == 0)
            return;

        _logger.LogInfo(
            $"[Connector startup] Roslyn-reconciling {filesToReconcile.Count} .cs file(s) for project " +
            $"'{project.ProjectName}' to override potentially stale compiled bindings.");

        foreach (var (filePath, text) in filesToReconcile)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // notify: false -- this method's own caller already reparses every open feature
                // file and publishes MatchCacheChangedNotification unconditionally right after
                // RediscoverCsFilesAsync returns. Without this, the notification
                // ApplyRoslynFileUpdateAsync raises when its Roslyn-parsed result differs at all
                // from the connector's just-loaded reflection-based extraction -- which it
                // essentially always does, real edit or not -- fired a second, fully independent
                // registry-change notification that redundantly reparsed the same feature files
                // again moments later (issue #471).
                await _csharpDiscoveryService
                    .UpdateFromSourceForProjectAsync(project, filePath, text, ct, notify: false)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    $"[Connector startup] Roslyn rediscovery failed for '{filePath}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Selects the <c>.cs</c> files to reconcile after a full replacement and pairs each with the
    /// source text to parse: every open project-owned <c>.cs</c> buffer (unsaved edits always win,
    /// using the buffer text), plus closed step-definition files newer than the compiled assembly
    /// (using on-disk text).
    /// </summary>
    private List<(string FilePath, string Text)> CollectCsFilesToReconcile(LspReqnrollProject project)
    {
        var projectFolder = project.ProjectFolder;
        if (string.IsNullOrEmpty(projectFolder))
            return [];

        // 1. Open, project-owned .cs files — unsaved edits override the DLL regardless of mtime.
        //    Ownership goes through ResolveOwners, which already encapsulates the correct
        //    fallback chain (index hit → owners; pending, no baseline yet → folder-prefix
        //    singleton; unowned → none) rather than reimplementing folder-prefix matching here
        //    directly — a bare path-prefix check is exactly what caused a real cross-project
        //    binding leak (issue confirmed live: Minimalnet481's bindings matched against
        //    Minimal's feature files, since "Minimalnet481" is a string-prefix match for
        //    "Minimal"). IDocumentBufferService never holds .cs content (Gherkin-only, by
        //    design) — this reads from ICSharpFileTextCache instead, which TextDocumentSyncHandler
        //    keeps live for every open .cs file regardless of what triggered the last edit.
        var openByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _csharpFileTextCache.All)
        {
            var path = entry.Uri.GetFileSystemPath();
            if (!string.IsNullOrEmpty(path)
                && path!.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(entry.Text)
                && _scopeManager.ResolveOwners(entry.Uri).Contains(project))
            {
                openByPath[path] = entry.Text;
            }
        }

        var result = openByPath.Select(kvp => (kvp.Key, kvp.Value)).ToList();

        // 2. Closed .cs step-def files edited since the last build (newer than the assembly).
        //    No assembly (never built) => nothing compiled can be stale => only the open buffers
        //    above are relevant.
        var assemblyWriteTimeUtc = GetAssemblyWriteTimeUtc(project);
        if (assemblyWriteTimeUtc is null)
            return result;

        foreach (var path in EnumerateProjectStepDefinitionFiles(project))
        {
            if (openByPath.ContainsKey(path))
                continue; // already covered by its open buffer above

            DateTime mtimeUtc;
            try { mtimeUtc = _fileSystem.File.GetLastWriteTimeUtc(path); }
            catch { continue; }

            if (mtimeUtc <= assemblyWriteTimeUtc.Value)
                continue; // unchanged since the build → the compiled binding is authoritative

            try
            {
                result.Add((path, _fileSystem.File.ReadAllText(path)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"[Connector startup] Could not read '{path}' for Roslyn rediscovery: {ex.Message}");
            }
        }

        return result;
    }

    private DateTime? GetAssemblyWriteTimeUtc(LspReqnrollProject project)
    {
        var assemblyPath = project.OutputAssemblyPath;
        if (string.IsNullOrEmpty(assemblyPath) || !_fileSystem.File.Exists(assemblyPath))
            return null;
        try { return _fileSystem.File.GetLastWriteTimeUtc(assemblyPath); }
        catch { return null; }
    }

    /// <summary>
    /// Enumerates the project's <c>.cs</c> step-definition files: the membership index when a
    /// baseline has been received (authoritative — includes linked files, excludes obj/bin),
    /// otherwise a folder glob that skips build output.
    /// </summary>
    private IReadOnlyCollection<string> EnumerateProjectStepDefinitionFiles(LspReqnrollProject project)
    {
        if (_scopeManager.HasBaselineForProject(project))
            return _scopeManager.GetBindingFilePathsForProject(project);

        var folder = project.ProjectFolder;
        if (string.IsNullOrEmpty(folder) || !_fileSystem.Directory.Exists(folder))
            return [];

        return _fileSystem.Directory
            .EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsInBuildOutput(p, folder))
            .ToList();
    }

    private static bool IsInBuildOutput(string path, string projectFolder)
    {
        var relative = path.Substring(projectFolder.Length).Replace('\\', '/');
        return relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }
}
