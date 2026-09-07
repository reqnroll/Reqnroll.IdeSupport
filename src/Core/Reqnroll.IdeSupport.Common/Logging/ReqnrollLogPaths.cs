using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>
/// Resolves the per-OS Reqnroll log directory and prunes stale log files in it — shared by every
/// .NET process (VS extension, LSP server, Connector) so they agree on one directory per OS with
/// the VS Code extension's <c>resolveLogDirectory</c> (<c>lspInspectorLogger.ts</c>) and the Rider
/// plugin's <c>ReqnrollDebugLogger.logDirectory</c> (issue #625).
/// </summary>
/// <remarks>
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> already matches the desired
/// directory on Windows (<c>%LOCALAPPDATA%</c>) and Linux (<c>~/.local/share</c>), but on macOS
/// .NET resolves it to <c>~/.local/share</c> too instead of the platform's actual log
/// location, <c>~/Library/Logs</c> — hence the explicit per-OS switch below rather than relying
/// on it everywhere.
/// </remarks>
public static class ReqnrollLogPaths
{
    private const string ApplicationFolderName = "Reqnroll";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(10);

    /// <summary>Resolves the Reqnroll log directory for the current OS and prunes stale log files in it.</summary>
    public static string ResolveLogDirectory()
    {
        var dir = ResolveLogDirectory(
            RuntimeInformation.OSDescription,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        PruneOldLogFiles(dir);
        return dir;
    }

    /// <summary>
    /// Pure per-OS resolution, taking explicit platform/LOCALAPPDATA/home values rather than
    /// reading them directly, so every branch is unit-testable without depending on the OS the
    /// tests happen to run on. Mirrors the Rider plugin's <c>ReqnrollDebugLogger.logDirectory</c>
    /// and the VS Code extension's <c>resolveLogDirectory</c>.
    /// </summary>
    internal static string ResolveLogDirectory(string platformDescription, string? localAppData, string userProfile)
    {
        var platform = platformDescription.ToLowerInvariant();
        // Check macOS/Darwin before Windows: "darwin" contains the substring "win", so a
        // Windows check of just "win" (rather than "windows") would misclassify it.
        if (platform.Contains("mac") || platform.Contains("darwin") || platform.Contains("osx"))
            return Path.Combine(userProfile, "Library", "Logs", ApplicationFolderName);
        if (platform.Contains("windows"))
            return Path.Combine(localAppData ?? userProfile, ApplicationFolderName);
        // Linux and anything else POSIX-ish.
        return Path.Combine(userProfile, ".local", "share", ApplicationFolderName);
    }

    /// <summary>
    /// Deletes <c>reqnroll-*</c> files older than 10 days from <paramref name="logDirectory"/>.
    /// Called from <see cref="ResolveLogDirectory()"/> so pruning happens whenever any sink
    /// (file logger, telemetry debug log, crash dump) resolves the directory, instead of being
    /// tied to one specific logger's construction.
    /// </summary>
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
            // Best-effort — pruning must never break logging.
        }
    }
}
