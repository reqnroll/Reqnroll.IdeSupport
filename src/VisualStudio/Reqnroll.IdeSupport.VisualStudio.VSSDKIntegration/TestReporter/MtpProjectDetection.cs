using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.VisualStudio.TestReporter;

/// <summary>
/// Ad hoc, narrow detection of whether a project is Microsoft.Testing.Platform (MTP)-capable — issue
/// #715 plan §5.7, ported from the Rider plugin's <c>RunTestRunner.detectDotnetTestMode</c> (Kotlin).
/// VS needs only this one signal, not the full VsTest/MtpCompat/MtpNative three-way state Rider's own
/// <c>dotnet test</c> command line needs: per plan §5.7, VS's Test Explorer always drives an
/// MTP-capable project through its own "testing platform server mode", a pipeline unrelated to
/// <c>dotnet test</c>'s own CLI mode selection — so <c>TestingPlatformDotnetTestSupport</c>/native-mode
/// <c>global.json</c> don't change anything about how *VS itself* launches a test, only about how
/// <c>dotnet test</c> would.
/// </summary>
/// <remarks>
/// A plain text scan of the project file itself and every <c>Directory.Build.props</c> found walking
/// up to the nearest <c>.git</c>/<c>.sln</c> — not a full MSBuild evaluation (misses e.g. a
/// condition-guarded property, or one set only via an <c>Import</c>), but enough to catch the common
/// case Microsoft's own docs recommend: setting the property once in a repo-root
/// <c>Directory.Build.props</c>. Kept independent of any <c>Microsoft.VisualStudio.*</c> type (same
/// reasoning as <c>TestLoggerActivationRules</c>'s own doc comment) so it stays unit-testable without
/// a VS install.
/// </remarks>
public static class MtpProjectDetection
{
    private static readonly Regex MtpCapablePattern = new(
        @"<(EnableMSTestRunner|EnableNUnitRunner|UseMicrosoftTestingPlatformRunner|IsTestingPlatformApplication)>\s*true\s*<",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True if <paramref name="projectFilePath"/> itself, or any <c>Directory.Build.props</c> found walking up to the nearest <c>.git</c>/<c>.sln</c>, declares an MTP-capable test framework.</summary>
    public static bool IsMtpCapable(string projectFilePath)
    {
        if (ReadTextOrNull(projectFilePath) is { } projectXml && MtpCapablePattern.IsMatch(projectXml))
            return true;

        var dir = Path.GetDirectoryName(projectFilePath);
        while (!string.IsNullOrEmpty(dir))
        {
            var propsPath = Path.Combine(dir, "Directory.Build.props");
            if (ReadTextOrNull(propsPath) is { } propsXml && MtpCapablePattern.IsMatch(propsXml))
                return true;

            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                break; // Workspace root reached; stop climbing.

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, dir, StringComparison.Ordinal))
                break;
            dir = parent;
        }
        return false;
    }

    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // Unreadable (permissions, mid-write): treat as "no evidence", not a crash.
        }
    }
}
