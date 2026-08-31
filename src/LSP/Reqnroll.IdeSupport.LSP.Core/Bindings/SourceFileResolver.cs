#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// Maps a source-file path recorded by binding discovery onto a path that exists on this machine.
/// </summary>
/// <remarks>
/// <para>
/// The reflection connector reports whatever the PDB says, which is the absolute path from the
/// machine that compiled the assembly. That is routinely not a path this machine has (issue #540):
/// a devcontainer build records <c>/workspaces/host-solution/…</c>; a deterministic CI build
/// (<c>ContinuousIntegrationBuild=true</c>) records <c>/_/…</c>; an external binding assembly from
/// NuGet was built on someone else's machine by definition; and any solution built on one box and
/// opened on another has the same shape.
/// </para>
/// <para>
/// Implementations answer with a local path or <see langword="null"/>. <see langword="null"/> is a
/// real answer, not a failure — see <see cref="Documents.SourceLocation.IsResolved"/> for what
/// callers must then do with it.
/// </para>
/// </remarks>
public interface ISourceFileResolver
{
    /// <summary>
    /// Returns a path on this machine for <paramref name="recordedPath"/>, or <see langword="null"/>
    /// when no local file can be identified for it.
    /// </summary>
    string? Resolve(string? recordedPath);
}

/// <summary>
/// The default <see cref="ISourceFileResolver"/>: resolves against one project's folder, in the
/// order agreed for issue #540.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><description><b>Exact hit.</b> The recorded path exists — use it. Overwhelmingly the common
/// case, and the only step that runs for a locally built solution.</description></item>
/// <item><description><b>Prefix remap.</b> Drop leading segments off the recorded path one at a time
/// and re-root the remainder on the project folder, longest remainder first. One rule covers every
/// foreign-prefix case in the issue, because in all of them the tail after the foreign build root
/// <em>is</em> the project-relative path: <c>/workspaces/host-solution/Specs/Support/Hooks.cs</c>
/// and <c>/_/Support/Hooks.cs</c> both reduce to <c>Support/Hooks.cs</c> under a project folder
/// ending in <c>Specs</c>.</description></item>
/// <item><description><b>Unique name match.</b> Search the project folder (excluding build output)
/// for a file with the recorded name, and accept it only when exactly one exists. Ambiguity fails
/// through rather than guessing — a wrong navigation target is worse than none.</description></item>
/// <item><description><b>Unresolved.</b> Return <see langword="null"/>.</description></item>
/// </list>
/// <para>
/// Every answer is memoised, negatives included: a discovery run asks about the same handful of
/// files once per binding, and step 3 walks the project tree.
/// </para>
/// </remarks>
public sealed class ProjectSourceFileResolver : ISourceFileResolver
{
    private readonly string? _projectFolder;
    private readonly IFileSystemForIDE _fileSystem;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Built lazily and only if step 3 is ever reached, since it walks the project folder.
    private ILookup<string, string>? _filesByName;

    /// <summary>Creates a resolver scoped to one project folder.</summary>
    public ProjectSourceFileResolver(string? projectFolder, IFileSystemForIDE fileSystem)
    {
        _projectFolder = projectFolder;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc/>
    public string? Resolve(string? recordedPath)
    {
        if (string.IsNullOrWhiteSpace(recordedPath))
            return null;

        if (_cache.TryGetValue(recordedPath!, out var cached))
            return cached;

        var resolved = ResolveUncached(recordedPath!);
        _cache[recordedPath!] = resolved;
        return resolved;
    }

    private string? ResolveUncached(string recordedPath)
    {
        if (Exists(recordedPath))
            return recordedPath;

        if (string.IsNullOrEmpty(_projectFolder))
            return null;

        return TryPrefixRemap(recordedPath) ?? TryUniqueNameMatch(recordedPath);
    }

    private string? TryPrefixRemap(string recordedPath)
    {
        // Split on both separators: the recorded path's separators are the build machine's, not
        // ours, so a Linux-built path arrives with '/' even when we are running on Windows.
        var segments = recordedPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        // A recorded path is untrusted input as far as this method is concerned -- it is whatever
        // string the compiler baked into a PDB we did not produce. Re-rooting its segments onto the
        // project folder would otherwise honour a ".." segment and walk back out of the project,
        // turning "navigate to this binding" into "open an arbitrary file elsewhere on disk".
        // Only ".." can escape, so only ".." disqualifies the path; a "." segment is inert and is
        // simply dropped, which keeps a legitimate remap working rather than failing it over noise.
        if (segments.Any(segment => segment == ".."))
            return null;

        segments = segments.Where(segment => segment != ".").ToArray();

        // Start at 1, not 0: index 0 is the recorded path itself re-rooted whole, which only differs
        // from what Exists already rejected when the path was relative. Stop before the last segment
        // so this step never degenerates into a bare file-name match -- that is step 3's job, and it
        // insists on uniqueness where this step would silently take the first hit.
        for (var i = 1; i < segments.Length; i++)
        {
            var candidate = Path.Combine(_projectFolder!, Path.Combine(segments.Skip(i).ToArray()));
            if (IsUnderProjectFolder(candidate) && Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Belt-and-braces containment check on the re-rooted candidate, in case a segment shape the
    /// filter above does not anticipate still resolves outside the project folder.
    /// </summary>
    private bool IsUnderProjectFolder(string candidate) =>
        PathUtils.IsUnderFolder(
            PathUtils.NormalizeForComparison(candidate),
            PathUtils.NormalizeForComparison(_projectFolder));

    private string? TryUniqueNameMatch(string recordedPath)
    {
        string fileName;
        try { fileName = Path.GetFileName(recordedPath); }
        catch (ArgumentException) { return null; }

        if (string.IsNullOrEmpty(fileName))
            return null;

        _filesByName ??= BuildFileNameIndex();

        var matches = _filesByName[fileName].Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private ILookup<string, string> BuildFileNameIndex()
    {
        try
        {
            if (!_fileSystem.Directory.Exists(_projectFolder))
                return Enumerable.Empty<string>().ToLookup(p => p, StringComparer.OrdinalIgnoreCase);

            return _fileSystem.Directory
                .EnumerateFiles(_projectFolder, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsInBuildOutput(p, _projectFolder!))
                .ToLookup(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Enumerable.Empty<string>().ToLookup(p => p, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsInBuildOutput(string path, string projectFolder)
    {
        if (path.Length <= projectFolder.Length)
            return false;

        var relative = path.Substring(projectFolder.Length).Replace('\\', '/');
        return relative.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0
            || relative.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool Exists(string path)
    {
        // File.Exists is documented not to throw, but the abstraction underneath is a seam a test
        // double can throw through, and the path itself is whatever a foreign PDB recorded; a probe
        // that fails is a "no", never an escaped exception on the discovery path.
        try { return _fileSystem.File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        { return false; }
    }
}

/// <summary>
/// An <see cref="ISourceFileResolver"/> that only ever confirms a path that already exists —
/// no remapping. The default for callers with no project context (unit tests, and any importer
/// constructed without a resolver), so behaviour there is exactly what it was before #540.
/// </summary>
public sealed class LocalOnlySourceFileResolver : ISourceFileResolver
{
    private readonly IFileSystemForIDE _fileSystem;

    /// <summary>Creates a resolver that performs only an existence check.</summary>
    public LocalOnlySourceFileResolver(IFileSystemForIDE fileSystem) => _fileSystem = fileSystem;

    /// <inheritdoc/>
    public string? Resolve(string? recordedPath)
    {
        if (string.IsNullOrWhiteSpace(recordedPath))
            return null;

        try { return _fileSystem.File.Exists(recordedPath) ? recordedPath : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        { return null; }
    }
}
