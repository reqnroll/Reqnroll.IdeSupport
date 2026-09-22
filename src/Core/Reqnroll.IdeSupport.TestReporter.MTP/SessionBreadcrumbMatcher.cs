using System.Text.Json;

namespace Reqnroll.IdeSupport.TestReporter.MTP;

/// <summary>One session breadcrumb, as written by the LSP server's <c>TestOutcomeSessionBreadcrumb</c> (issue #715 phase 1).</summary>
internal sealed record SessionBreadcrumb(string Endpoint, string? WorkspaceRoot);

/// <summary>
/// Reads the session breadcrumb files the LSP server writes under
/// <c>&lt;Reqnroll application dir&gt;\test-outcomes\sessions\*.json</c> and picks the best match for this
/// reporter's own resolved workspace root. Deepest-match-wins: among breadcrumbs whose
/// <c>workspaceRoot</c> is an ancestor of (or equal to) this reporter's root, the one with the longest
/// <c>workspaceRoot</c> wins — mirrors Rider's <c>RunTestRunner.findOwningProjectPath</c> tie-break
/// rule (issue #715 plan §7 risk #3: nested/multi-solution repos can have more than one candidate
/// session). A connect-and-verify step (actually opening the TCP connection) is the reporter's own
/// job, not this matcher's — a stale breadcrumb left by a crashed session is expected to fail to
/// connect, same as the VSTest logger's existing "can't reach IDE" degradation.
/// </summary>
internal static class SessionBreadcrumbMatcher
{
    /// <summary>Parses every <c>*.json</c> file in <paramref name="sessionsDirectory"/>; unreadable or malformed files are skipped, never thrown.</summary>
    internal static IReadOnlyList<SessionBreadcrumb> ReadAll(string sessionsDirectory)
    {
        var result = new List<SessionBreadcrumb>();
        if (!Directory.Exists(sessionsDirectory))
            return result;

        foreach (var file in Directory.EnumerateFiles(sessionsDirectory, "*.json"))
        {
            var breadcrumb = TryRead(file);
            if (breadcrumb is not null)
                result.Add(breadcrumb);
        }
        return result;
    }

    private static SessionBreadcrumb? TryRead(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("endpoint", out var endpointProperty) || endpointProperty.ValueKind != JsonValueKind.String)
                return null;
            var endpoint = endpointProperty.GetString();
            if (string.IsNullOrEmpty(endpoint))
                return null;

            string? workspaceRoot = null;
            if (root.TryGetProperty("workspaceRoot", out var workspaceRootProperty) && workspaceRootProperty.ValueKind == JsonValueKind.String)
                workspaceRoot = workspaceRootProperty.GetString();

            return new SessionBreadcrumb(endpoint, workspaceRoot);
        }
        catch (Exception)
        {
            // A breadcrumb mid-write, left over from an incompatible version, or any other per-file
            // failure: skip just this one file and let the caller's foreach keep going. Deliberately
            // catch-all rather than an allowlist of expected types (IOException/UnauthorizedAccessException/
            // JsonException) — ReadAll's whole point is tolerating one stale/corrupt breadcrumb alongside
            // good ones from other concurrently-open IDE windows, and a narrower filter would let an
            // unenumerated exception type escape this method, abort the foreach in ReadAll entirely, and
            // discard every other (possibly good) breadcrumb in the same call — not just this file. The
            // caller (ReqnrollMtpReporter.TryConnect) already swallows everything at its own boundary, but
            // that only prevents a crash; it doesn't restore the per-file isolation this loop is built around.
            return null;
        }
    }

    /// <summary>
    /// Deepest-match-wins selection: <see langword="null"/> if no breadcrumb's <c>workspaceRoot</c> is
    /// an ancestor of (or equal to) <paramref name="myWorkspaceRoot"/>, or if <paramref name="myWorkspaceRoot"/>
    /// itself is <see langword="null"/> (workspace root not resolvable — see <see cref="WorkspaceRootLocator"/>).
    /// </summary>
    internal static SessionBreadcrumb? FindBestMatch(string? myWorkspaceRoot, IEnumerable<SessionBreadcrumb> candidates)
    {
        if (string.IsNullOrEmpty(myWorkspaceRoot))
            return null;

        SessionBreadcrumb? best = null;
        var bestLength = -1;
        foreach (var candidate in candidates)
        {
            if (candidate.WorkspaceRoot is null) continue;
            if (!IsSameOrUnder(myWorkspaceRoot, candidate.WorkspaceRoot)) continue;
            if (candidate.WorkspaceRoot.Length > bestLength)
            {
                best = candidate;
                bestLength = candidate.WorkspaceRoot.Length;
            }
        }
        return best;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="folder"/> itself, or lives under it, with a
    /// directory-separator boundary check — a bare <c>StartsWith</c> would treat a sibling folder whose
    /// name happens to extend the prefix (e.g. <c>C:\Repo2</c> vs. <c>C:\Repo</c>) as "inside" it. A
    /// small local copy rather than a reference to <c>Reqnroll.IdeSupport.Common.ProjectSystem.PathUtils.IsUnderFolder</c>:
    /// that assembly pulls in Gherkin/Roslyn/Newtonsoft-sized dependencies this reporter — loaded into
    /// an arbitrary user test-host process — must not carry (same reasoning as
    /// <c>Reqnroll.IdeSupport.TestReporter.Common</c>'s own existence).
    /// </summary>
    private static bool IsSameOrUnder(string path, string folder)
    {
        var normalizedFolder = folder.TrimEnd('\\', '/');
        if (!path.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Length == normalizedFolder.Length)
            return true;
        var boundary = path[normalizedFolder.Length];
        return boundary is '\\' or '/';
    }
}
