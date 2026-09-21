using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
/// up to the nearest <c>.git</c>/<c>.sln</c> is the fast path, but it misses a project made
/// MTP-capable only through an <em>imported</em> <c>.props</c>/<c>.targets</c> file — e.g. referencing
/// the full <c>xunit.v3</c> runner package pulls in <c>Microsoft.Testing.Platform.MSBuild</c>, which
/// sets <c>IsTestingPlatformApplication=true</c> from its own props, never appearing as literal text
/// anywhere a scan would look (issue #722, live-verified against a real project: the VS experimental
/// instance silently fell back to the classic VSTest logger for such a project). When the text scan
/// finds nothing but the project text itself already looks like a test project (references
/// <c>Microsoft.NET.Test.Sdk</c>), this falls back to a real <c>dotnet msbuild -getProperty</c>
/// evaluation — mirroring the Rider plugin's own fix for the identical gap
/// (<c>RunTestRunner.evaluateMtpPropertiesViaMsbuild</c>) and VS Code's <c>msbuildEvaluator.ts</c>.
/// Gating the subprocess on that literal "looks like a test project" signal keeps a solution-wide scan
/// (<see cref="MtpEphemeralInjection"/> calls this once per <c>.csproj</c> in the solution) from
/// shelling out to <c>dotnet msbuild</c> for every ordinary non-test project. Kept independent of any
/// <c>Microsoft.VisualStudio.*</c> type (same reasoning as <c>TestLoggerActivationRules</c>'s own doc
/// comment) so it stays unit-testable without a VS install.
/// </remarks>
public static class MtpProjectDetection
{
    private static readonly Regex MtpCapablePattern = new(
        @"<(EnableMSTestRunner|EnableNUnitRunner|UseMicrosoftTestingPlatformRunner|IsTestingPlatformApplication)>\s*true\s*<",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TestSdkPattern = new(
        @"<PackageReference\s+Include\s*=\s*""Microsoft\.NET\.Test\.Sdk""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] MtpCapablePropertyNames =
        { "EnableMSTestRunner", "EnableNUnitRunner", "UseMicrosoftTestingPlatformRunner", "IsTestingPlatformApplication" };

    private static readonly TimeSpan MsBuildEvalTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// True if <paramref name="projectFilePath"/> itself, or any <c>Directory.Build.props</c> found
    /// walking up to the nearest <c>.git</c>/<c>.sln</c>, declares an MTP-capable test framework — or,
    /// failing that, a real MSBuild evaluation resolves one of the MTP opt-in properties to
    /// <c>true</c> (see the type's remarks).
    /// </summary>
    public static bool IsMtpCapable(string projectFilePath) => IsMtpCapable(projectFilePath, EvaluateViaMsBuild);

    /// <summary><paramref name="evaluateMtp"/> is injected for testability — production callers use <see cref="IsMtpCapable(string)"/>, which defaults to <see cref="EvaluateViaMsBuild"/>.</summary>
    internal static bool IsMtpCapable(string projectFilePath, Func<string, bool?> evaluateMtp)
    {
        var projectXml = ReadTextOrNull(projectFilePath);
        if (projectXml != null && MtpCapablePattern.IsMatch(projectXml))
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

        // Nothing literal. Only pay for a real MSBuild evaluation when the project already looks like
        // a test project — otherwise a solution-wide scan would shell out to `dotnet msbuild` for
        // every ordinary project.
        if (projectXml != null && TestSdkPattern.IsMatch(projectXml))
            return evaluateMtp(projectFilePath) == true;

        return false;
    }

    /// <summary>
    /// Evaluates <paramref name="projectFilePath"/>'s real, MSBuild-resolved MTP opt-in properties via
    /// <c>dotnet msbuild -getProperty:...</c>. Returns <c>null</c> — callers treat this as "no
    /// evidence" — if the process can't start, times out, exits non-zero, or its output isn't the
    /// expected <c>{"Properties": {...}}</c> shape; this must never throw or block package
    /// initialization on a broken/unrestorable project.
    /// </summary>
    internal static bool? EvaluateViaMsBuild(string projectFilePath)
    {
        try
        {
            var arguments = $"msbuild {QuoteArgument(projectFilePath)} " +
                $"{QuoteArgument($"-getProperty:{string.Join(",", MtpCapablePropertyNames)}")} -nologo";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            var stdout = new System.Text.StringBuilder();
            // Async line-reader pattern (not sync ReadToEnd) so an un-drained stderr pipe filling its
            // OS buffer can't block the child process from exiting while this waits on stdout.
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, _) => { }; // Drained, not read — evaluation errors aren't actionable here.

            if (!process.Start())
                return null;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit((int)MsBuildEvalTimeout.TotalMilliseconds))
            {
                try { process.Kill(); } catch { /* best effort */ }
                return null;
            }
            if (process.ExitCode != 0)
                return null;

            using var document = JsonDocument.Parse(stdout.ToString());
            if (!document.RootElement.TryGetProperty("Properties", out var properties))
                return null;

            foreach (var name in MtpCapablePropertyNames)
            {
                if (properties.TryGetProperty(name, out var value) &&
                    bool.TryParse(value.GetString(), out var isTrue) && isTrue)
                    return true;
            }
            return false;
        }
        catch (Exception)
        {
            return null; // dotnet unavailable, malformed output, etc. — fall back to "no evidence".
        }
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

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
