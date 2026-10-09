using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using System.Collections.Concurrent;
using System.IO;
using System.Collections.Immutable;

namespace Reqnroll.IdeSupport.LSP.Core.Workspace;

/// <summary>
/// Represents one <c>.csproj</c> Reqnroll project within an <see cref="LspProjectScope"/>
/// (workspace folder).  This is the LSP server's equivalent of the VS extension's
/// <c>VsProjectScope</c> — one instance per <c>.csproj</c>, implementing
/// <see cref="IProjectScope"/> so the shared connector and settings infrastructure
/// works without modification.
/// </summary>
/// <remarks>
/// Properties are populated from the <see cref="ReqnrollProjectLoadedParams"/> notification
/// sent by the IDE glue component.  They may be updated in-place when the IDE sends a
/// fresh notification for the same project (e.g. after a rebuild changes the output path).
/// </remarks>
public sealed class LspReqnrollProject : IProjectScope, IDisposable
{
    private readonly IIdeScope _ideScope;

    /// <summary>Creates a project scope from the IDE's initial project-loaded notification.</summary>
    public LspReqnrollProject(ReqnrollProjectLoadedParams info, IIdeScope ideScope)
    {
        _ideScope = ideScope;
        ProjectFullName = info.ProjectFile;
        ProjectFolder   = info.ProjectFolder;
        ProjectName     = Path.GetFileNameWithoutExtension(info.ProjectFile);
        Update(info);
    }

    // ── IProjectScope ─────────────────────────────────────────────────────────

    /// <summary>The server-level <see cref="IIdeScope"/> this project belongs to.</summary>
    public IIdeScope IdeScope => _ideScope;

    /// <inheritdoc/>  e.g. "MyApp.Tests"
    public string ProjectName { get; }

    /// <inheritdoc/>  Full path to the .csproj file.
    public string ProjectFullName { get; }

    /// <inheritdoc/>  Directory containing the .csproj.
    public string ProjectFolder { get; }

    /// <summary>
    /// The workspace folder the client reported this project under
    /// (<see cref="ReqnrollProjectLoadedParams.WorkspaceFolder"/>): the solution folder in VS and
    /// Rider, the opened folder in VS Code. Not part of <see cref="IProjectScope"/>; empty when the
    /// client sent none. It is not guaranteed to contain <see cref="ProjectFolder"/>.
    /// </summary>
    public string WorkspaceFolder { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public ConcurrentDictionary<Type, object> Properties { get; } = new();

    /// <inheritdoc/>
    public string OutputAssemblyPath { get; private set; } = string.Empty;

    /// <inheritdoc/>  Full moniker, e.g. ".NETCoreApp,Version=v8.0"
    public string TargetFrameworkMoniker { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public string TargetFrameworkMonikers { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public string PlatformTargetName { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public string DefaultNamespace { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public IEnumerable<NuGetPackageReference> PackageReferences { get; private set; }
        = ImmutableArray<NuGetPackageReference>.Empty;

    /// <inheritdoc/>
    public int? GetFeatureFileCount()
    {
        try
        {
            return _ideScope.FileSystem.Directory
                .GetFiles(ProjectFolder, "*.feature", SearchOption.AllDirectories)
                .Length;
        }
        catch { return null; }
    }

    /// <inheritdoc/>
    public string[] GetProjectFiles(string extension)
    {
        try
        {
            return _ideScope.FileSystem.Directory
                .GetFiles(ProjectFolder, $"*{extension}", SearchOption.AllDirectories);
        }
        catch { return Array.Empty<string>(); }
    }

    // ── Internal mutation ─────────────────────────────────────────────────────

    /// <summary>
    /// Refreshes mutable properties from a fresh <see cref="ReqnrollProjectLoadedParams"/>
    /// notification for the same project (same <see cref="ProjectFullName"/>).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a discovery-relevant input changed — the
    /// <see cref="OutputAssemblyPath"/> or <see cref="TargetFrameworkMoniker"/> — so the caller
    /// knows to trigger binding re-discovery; otherwise <see langword="false"/>.
    /// </returns>
    public bool Update(ReqnrollProjectLoadedParams info)
    {
        var discoveryInputChanged =
            !string.Equals(OutputAssemblyPath, info.OutputAssemblyPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(TargetFrameworkMoniker, info.TargetFrameworkMoniker, StringComparison.Ordinal);

        WorkspaceFolder         = info.WorkspaceFolder ?? string.Empty;
        OutputAssemblyPath      = info.OutputAssemblyPath;
        TargetFrameworkMoniker  = info.TargetFrameworkMoniker;
        TargetFrameworkMonikers = info.TargetFrameworkMoniker;
        DefaultNamespace        = string.IsNullOrEmpty(info.DefaultNamespace)
            ? ProjectName
            : info.DefaultNamespace;
        PackageReferences = (info.PackageReferences ?? [])
            .Select(p => new NuGetPackageReference(
                p.PackageId,
                new NuGetVersion(p.Version, p.Version),
                p.InstallPath))
            .ToArray();

        return discoveryInputChanged;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>Disposes any disposable values stored in <see cref="Properties"/> and clears the bag.</summary>
    public void Dispose()
    {
        foreach (var disposable in Properties.Values.OfType<IDisposable>())
            disposable.Dispose();
        Properties.Clear();
    }

    /// <summary>Returns the project name, for logging and debugging.</summary>
    public override string ToString() => ProjectName;
}
