using System;
using System.IO;

namespace Reqnroll.IdeSupport.Common.ProjectSystem;

/// <summary>PathUtils</summary>
public static class PathUtils
{
    /// <summary>
    /// The canonical form of <paramref name="path"/> for identity comparison: a fully qualified
    /// path is resolved through <see cref="Path.GetFullPath(string)"/> so <c>.</c>, <c>..</c> and
    /// mixed separators collapse; anything else only has its separators unified and trailing
    /// separators trimmed.
    /// </summary>
    /// <remarks>
    /// The "only when fully qualified" condition is the important half, and it is why this exists
    /// rather than a bare <c>Path.GetFullPath</c> (issue #540). A source path recorded in a PDB
    /// built on another platform — <c>/workspaces/host-solution/Steps.cs</c> from a devcontainer,
    /// <c>/_/Steps.cs</c> from a deterministic CI build — is <see cref="Path.IsPathRooted(string)"/>
    /// on Windows but not <see cref="Path.IsPathFullyQualified(string)"/>, and handing it to
    /// <c>GetFullPath</c> silently rebases it onto the current process directory's drive:
    /// <c>/workspaces/host-solution/Steps.cs</c> becomes <c>C:\workspaces\host-solution\Steps.cs</c>.
    /// That is wrong twice over — the result depends on where the server process happens to be
    /// running from, so it is not a stable key, and it can manufacture a false match against a
    /// workspace that genuinely lives at that path. Leaving a non-fully-qualified path alone makes
    /// it compare unequal to every local absolute path, which is the truthful answer.
    /// </remarks>
    public static string NormalizeForComparison(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            if (IsFullyQualified(path!))
                return Path.GetFullPath(path!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Fall through to the separator-only normalization below: a path we cannot resolve is
            // still worth comparing literally, and no caller of this should have to guard a throw.
        }

        return path!.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Whether <paramref name="path"/> names a location that does not depend on the current
    /// directory — a drive-qualified or UNC path on Windows, a leading <c>/</c> elsewhere.
    /// </summary>
    /// <remarks>
    /// Hand-rolled because this assembly targets netstandard2.0, where
    /// <c>Path.IsPathFullyQualified</c> does not exist. Deliberately stricter than
    /// <see cref="Path.IsPathRooted(string)"/>, which is the whole point: on Windows
    /// <c>IsPathRooted("/workspaces/x")</c> is <see langword="true"/>, and that leading-slash
    /// "rooted but drive-relative" shape is exactly what a foreign PDB path looks like.
    /// </remarks>
    private static bool IsFullyQualified(string path)
    {
        var windowsStyle = Path.DirectorySeparatorChar == '\\';

        if (!windowsStyle)
            return path[0] == '/';

        // UNC ("\\server\share") or device ("\\?\C:\") paths.
        if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
            return true;

        // Drive-qualified: "C:\" or "C:/". "C:foo" is drive-*relative* and does not count.
        return path.Length >= 3
               && path[1] == ':'
               && IsSeparator(path[2])
               && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'));
    }

    private static bool IsSeparator(char c) => c == '\\' || c == '/';

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> identify the same file, comparing
    /// their <see cref="NormalizeForComparison"/> forms case-insensitively. An empty or whitespace
    /// path matches nothing, including another empty one.
    /// </summary>
    /// <remarks>
    /// The comparison is case-insensitive because the two discovery paths disagree on casing for
    /// the very same file: the reflection connector records the source path from the PDB (often
    /// with an upper-case drive letter) while Roslyn discovery derives it from an LSP document URI
    /// (which can carry a lower-case one). A case-sensitive compare treats those as different files
    /// and fails to replace a file's previous bindings, leaving a stale binding behind. This is
    /// technically over-permissive on case-sensitive filesystems; that trade has been in place since
    /// #518 and is not revisited here.
    /// </remarks>
    public static bool IsSamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        // Fast path. Normalization costs a Path.GetFullPath each, and this sits inside per-binding
        // loops (BindingLocationMatcher.CoversQuery walks every step definition in the registry);
        // two paths that are already textually identical are the common case and need none of it.
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(NormalizeForComparison(a), NormalizeForComparison(b),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="filePath"/> is <paramref name="folder"/> itself, or lives
    /// somewhere under it — with a directory-separator boundary check, unlike a bare
    /// <c>filePath.StartsWith(folder)</c>.
    /// </summary>
    /// <remarks>
    /// A plain string-prefix check treats a sibling folder whose name happens to extend the
    /// prefix as "inside" it — e.g. <c>@"C:\Repo\Minimalnet481\Foo.cs"</c> starts with
    /// <c>@"C:\Repo\Minimal"</c> even though <c>Minimalnet481</c> is a completely different
    /// folder than <c>Minimal</c>. Confirmed live: this let a sibling project's step-definition
    /// bindings bleed into another project's registry, producing false "ambiguous step" matches.
    /// </remarks>
    public static bool IsUnderFolder(string? filePath, string? folder)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(folder))
            return false;

        var normalizedFolder = folder!.TrimEnd('\\', '/');
        if (!filePath!.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase))
            return false;

        if (filePath.Length == normalizedFolder.Length)
            return true; // filePath IS the folder

        var boundaryChar = filePath[normalizedFolder.Length];
        return boundaryChar is '\\' or '/';
    }
}
