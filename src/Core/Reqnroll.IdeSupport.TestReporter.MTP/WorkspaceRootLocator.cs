namespace Reqnroll.IdeSupport.TestReporter.MTP;

/// <summary>
/// Walks up from a starting directory to the nearest ancestor that looks like a workspace root — a
/// <c>.sln</c>/<c>.slnx</c> file, or a <c>.git</c> entry (a directory for a normal clone, a file for a
/// worktree). Mirrors the IDE's own opened-folder identity closely enough that the result is expected
/// to equal (or, in a multi-folder workspace, be nested under) the <c>workspaceRoot</c> the LSP server
/// published in its session breadcrumb (<see cref="SessionBreadcrumbMatcher"/>) — see issue #715 plan
/// §4.
/// </summary>
internal static class WorkspaceRootLocator
{
    /// <summary>
    /// Pure walk-up: <paramref name="looksLikeRoot"/> is injected so the directory-listing logic
    /// itself (a thin wrapper over <see cref="Directory.EnumerateFiles"/>/<see cref="File.Exists"/>)
    /// stays out of the unit-tested decision logic. Returns <see langword="null"/> if no ancestor
    /// (including <paramref name="startDirectory"/> itself) matches before reaching the filesystem
    /// root.
    /// </summary>
    internal static string? FindNearestRoot(string startDirectory, Func<string, bool> looksLikeRoot)
    {
        var dir = startDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (looksLikeRoot(dir))
                return dir;

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, dir, StringComparison.Ordinal))
                return null; // Reached the filesystem root without a match.
            dir = parent;
        }
        return null;
    }

    /// <summary>The real filesystem check used in production: a <c>.sln</c>/<c>.slnx</c> file, or a <c>.git</c> file/directory, directly inside <paramref name="dir"/>.</summary>
    internal static bool LooksLikeRoot(string dir)
    {
        try
        {
            if (Directory.EnumerateFiles(dir, "*.sln").Any() || Directory.EnumerateFiles(dir, "*.slnx").Any())
                return true;
            var gitPath = Path.Combine(dir, ".git");
            return File.Exists(gitPath) || Directory.Exists(gitPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // An unreadable ancestor (permissions, a removed network share) just isn't a match.
        }
    }
}
