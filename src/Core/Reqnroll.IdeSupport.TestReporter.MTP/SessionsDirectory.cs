using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Reqnroll.IdeSupport.TestReporter.MTP;

/// <summary>
/// Resolves <c>&lt;Reqnroll application dir&gt;\test-outcomes\sessions</c> — the directory the LSP
/// server's <c>TestOutcomeSessionBreadcrumb</c> (issue #715 phase 1) writes session breadcrumb
/// files into.
/// </summary>
/// <remarks>
/// This deliberately duplicates <c>Reqnroll.IdeSupport.Common.Logging.ReqnrollLogPaths</c>'s per-OS
/// resolution rather than referencing that assembly: this reporter loads into an arbitrary user
/// test-host process, and Common pulls in Gherkin/Roslyn/Newtonsoft-sized dependencies not worth
/// adding to that process just to share ~15 lines of directory logic — the same reasoning already
/// applied to the Connector's <c>ConnectorLogPaths</c>. Keep this in sync by hand with
/// <c>ReqnrollLogPaths</c>/<c>ConnectorLogPaths</c> if that per-OS convention ever changes.
/// <para>
/// This intentionally mirrors <see cref="Reqnroll.IdeSupport.Common.Logging.ReqnrollLogPaths.ResolveApplicationDirectory()"/>,
/// not <c>ResolveLogDirectory()</c> — session breadcrumbs are discovery state for the reporter, not
/// logs, and must keep pointing at the same location <c>TestOutcomeSessionBreadcrumb</c> resolves on
/// the server side, which issue #726 (relocating log files under a <c>logs</c> subfolder) left
/// unchanged for exactly this reason.
/// </para>
/// </remarks>
internal static class SessionsDirectory
{
    /// <summary>Overrides the resolved directory outright when set — a test seam for spawning a real MTP test host against a hermetic temp directory, mirroring <c>REQNROLL_TESTLOGGER_FILE</c>'s role for the VSTest logger.</summary>
    internal const string OverrideEnvironmentVariable = "REQNROLL_MTP_SESSIONS_DIR";

    /// <summary>Resolves the sessions directory for the current OS, honoring <see cref="OverrideEnvironmentVariable"/> if set.</summary>
    internal static string Resolve()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        return Resolve(
            RuntimeInformation.OSDescription,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Pure per-OS resolution, taking explicit platform/LOCALAPPDATA/home values so every branch is unit-testable regardless of which OS the test host runs on.</summary>
    internal static string Resolve(string platformDescription, string? localAppData, string userProfile)
    {
        var platform = platformDescription.ToLowerInvariant();
        // Check macOS/Darwin before Windows: "darwin" contains the substring "win".
        var reqnrollDir = platform.Contains("mac") || platform.Contains("darwin") || platform.Contains("osx")
            ? Path.Combine(userProfile, "Library", "Logs", "Reqnroll")
            : platform.Contains("windows")
                ? Path.Combine(localAppData ?? userProfile, "Reqnroll")
                : Path.Combine(userProfile, ".local", "share", "Reqnroll");

        return Path.Combine(reqnrollDir, "test-outcomes", "sessions");
    }
}
