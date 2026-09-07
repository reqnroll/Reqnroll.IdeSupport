namespace ReqnrollConnector.Logging;

/// <summary>
/// Resolves the per-OS Reqnroll log directory and prunes stale log files in it, for
/// <see cref="FileLogger"/> (issue #628).
/// </summary>
/// <remarks>
/// This deliberately duplicates <c>Reqnroll.IdeSupport.Common.Logging.ReqnrollLogPaths</c> rather
/// than referencing that assembly: the Connector is a short-lived, per-discovery-run executable
/// multi-targeting eight frameworks from net462 through net10.0 (see <c>Connector.csproj</c>'s
/// comments on why), and Common pulls in Newtonsoft.Json, System.IO.Abstractions, and several
/// Microsoft.Extensions.* packages that this project has otherwise never needed - not a footprint
/// worth adding to every one of those eight builds just to share ~15 lines of directory logic.
/// Keep this in sync by hand with <c>ReqnrollLogPaths</c> if that per-OS convention ever changes.
/// </remarks>
internal static class ConnectorLogPaths
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(10);

    /// <summary>Resolves the Reqnroll log directory for the current OS and prunes stale log files in it.</summary>
    public static string ResolveLogDirectory()
    {
        var dir = ResolveDirectoryForCurrentPlatform();
        PruneOldLogFiles(dir);
        return dir;
    }

    private static string ResolveDirectoryForCurrentPlatform()
    {
#if NET6_0_OR_GREATER
        // RuntimeInformation.OSDescription needs .NET Framework 4.7.1+ - unavailable on this
        // project's net462/net472 targets - so the per-OS branch only compiles for modern .NET.
        return ResolveLogDirectory(
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
#else
        // The net462/net472/net481 builds only ever run on Windows, so LocalApplicationData
        // (which .NET only mismaps on macOS - see ReqnrollLogPaths' remarks) is correct as-is here.
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reqnroll");
#endif
    }

    /// <summary>
    /// Pure per-OS resolution, taking explicit platform/LOCALAPPDATA/home values rather than
    /// reading them directly, so every branch is unit-testable regardless of which OS the test
    /// host itself runs on. Mirrors <c>ReqnrollLogPaths.ResolveLogDirectory</c> (.NET host),
    /// <c>ReqnrollDebugLogger.logDirectory</c> (Rider), and <c>resolveLogDirectory</c> (VS Code).
    /// </summary>
    internal static string ResolveLogDirectory(string platformDescription, string? localAppData, string userProfile)
    {
        var platform = platformDescription.ToLowerInvariant();
        // Check macOS/Darwin before Windows: "darwin" contains the substring "win".
        if (platform.Contains("mac") || platform.Contains("darwin") || platform.Contains("osx"))
            return Path.Combine(userProfile, "Library", "Logs", "Reqnroll");
        if (platform.Contains("windows"))
            return Path.Combine(localAppData ?? userProfile, "Reqnroll");
        return Path.Combine(userProfile, ".local", "share", "Reqnroll");
    }

    /// <summary>Deletes <c>reqnroll-*</c> files older than 10 days from <paramref name="logDirectory"/>.</summary>
    public static void PruneOldLogFiles(string logDirectory)
    {
        try
        {
            if (!Directory.Exists(logDirectory)) return;

            var cutoffUtc = DateTime.UtcNow - MaxAge;
            foreach (var file in Directory.GetFiles(logDirectory, "reqnroll-*"))
            {
                var fi = new FileInfo(file);
                if (fi.LastWriteTimeUtc < cutoffUtc)
                    fi.Delete();
            }
        }
        catch
        {
            // Best-effort — pruning must never break the connector.
        }
    }
}
