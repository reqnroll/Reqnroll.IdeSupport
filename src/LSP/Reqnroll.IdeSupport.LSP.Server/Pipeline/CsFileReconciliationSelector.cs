using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>A <c>.cs</c> file selected for Roslyn reconciliation, paired with the text to parse.</summary>
internal sealed record CsFileToReconcile(string FilePath, string Text);

/// <summary>
/// Selects the <c>.cs</c> files <see cref="BindingRegistryChangedHandler"/> should
/// Roslyn-reconcile after a full registry replacement (issue #592), separating the selection
/// policy from the reconciliation call itself:
/// <list type="bullet">
///   <item>every open, project-owned <c>.cs</c> buffer — unsaved edits always win over the
///   compiled DLL, using the buffer text from <see cref="ICSharpFileTextCache"/>; and</item>
///   <item>closed step-definition files on disk whose last-write time is newer than the
///   project's compiled output assembly — i.e. edited then saved without a rebuild.</item>
/// </list>
/// </summary>
internal sealed class CsFileReconciliationSelector
{
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly ICSharpFileTextCache _csharpFileTextCache;
    private readonly IFileSystemForIDE _fileSystem;
    private readonly IIdeSupportLogger _logger;

    /// <summary>Initializes a new instance of the <see cref="CsFileReconciliationSelector"/> class.</summary>
    public CsFileReconciliationSelector(
        ILspWorkspaceScopeManager scopeManager,
        ICSharpFileTextCache csharpFileTextCache,
        IFileSystemForIDE fileSystem,
        IIdeSupportLogger logger)
    {
        _scopeManager = scopeManager;
        _csharpFileTextCache = csharpFileTextCache;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Whether a file whose on-disk content was last written at <paramref name="fileWriteTimeUtc"/>
    /// was edited since the assembly at <paramref name="assemblyWriteTimeUtc"/> was built — i.e.
    /// the compiled binding may no longer reflect it.
    /// </summary>
    internal static bool WasEditedSinceBuild(DateTime fileWriteTimeUtc, DateTime assemblyWriteTimeUtc)
        => fileWriteTimeUtc > assemblyWriteTimeUtc;

    /// <summary>
    /// Selects the <c>.cs</c> files to reconcile for <paramref name="project"/> and pairs each
    /// with the source text to parse: every open project-owned <c>.cs</c> buffer (unsaved edits
    /// always win, using the buffer text), plus closed step-definition files newer than the
    /// compiled assembly (using on-disk text).
    /// </summary>
    public List<CsFileToReconcile> Collect(LspReqnrollProject project)
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

        var result = openByPath.Select(kvp => new CsFileToReconcile(kvp.Key, kvp.Value)).ToList();

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

            if (!WasEditedSinceBuild(mtimeUtc, assemblyWriteTimeUtc.Value))
                continue; // unchanged since the build → the compiled binding is authoritative

            try
            {
                result.Add(new CsFileToReconcile(path, _fileSystem.File.ReadAllText(path)));
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
