using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.TestReporter;

/// <summary>
/// Connects a project to the Microsoft.Testing.Platform (MTP) reporter by writing a project-local
/// stub, <c>obj\&lt;Project&gt;.csproj.reqnroll-ide.targets</c>, that imports the bundled
/// <c>Reqnroll.IdeSupport.TestReporter.MTP.targets</c> (issue #741).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why obj\</b>: <c>Microsoft.Common.targets</c> imports
/// <c>$(MSBuildProjectExtensionsPath)$(MSBuildProjectFile).*.targets</c>, and
/// <c>MSBuildProjectExtensionsPath</c> defaults to <c>obj\</c> — the same mechanism NuGet's own
/// <c>obj\&lt;Project&gt;.csproj.nuget.g.targets</c> uses. So the stub affects that one project only:
/// no per-user <c>ImportAfter</c> file (the previous design, which affected every build for the Windows
/// user — see <see cref="TryRemoveLegacyImportAfterFile"/>), no environment variable, no edit to the
/// user's project file. obj\ is normally git-ignored, so the stub is never committed; <c>dotnet clean</c>
/// leaves it (it is not a FileWrites item); deleting obj\ removes it and the next solution/project load
/// writes it again.
/// </para>
/// <para>
/// <b>Reqnroll projects only.</b> A stub is written only for a project whose NuGet restore output
/// (<c>project.assets.json</c>, in the same directory as the stub) lists a package or project whose
/// name contains "Reqnroll" — the name rule the LSP server's <c>ReqnrollProjectDetector</c> uses, but
/// over the whole restore graph, so a project that gets Reqnroll through an in-house meta-package
/// qualifies too. A restored project that lists none has its stub removed; a project with no restore
/// output yet is left alone until NuGet's restore-finished signal triggers another pass.
/// </para>
/// <para>
/// <b>No MTP detection here.</b> The imported <c>.targets</c> gates itself at build time on the real,
/// evaluated properties (a resolved <c>Reqnroll.dll</c> reference, language, test host, MTP opt-in, TFM,
/// LangVersion, resolved Microsoft.Testing.Platform version) and adds nothing to a project that fails
/// any gate. That replaces the ad hoc text scans that issue #722 showed miss projects made MTP-capable
/// through imported props.
/// </para>
/// <para>
/// <b>Known gap (issue #741 risk 4a)</b>: VS may keep using a project evaluation made before the stub
/// existed, so the first build after a stub is first written (a brand-new project, or a deleted obj\)
/// can run without the reporter; later builds pick it up.
/// </para>
/// </remarks>
public static class MtpProjectStubs
{
    public const string StubFileSuffix = ".reqnroll-ide.targets";
    internal const string LegacyImportAfterFileName = "Reqnroll.IdeSupport.TestReporter.MTP.g.targets";

    /// <summary>A property that moves obj\ (or the project-extensions path) away from its default; its presence means "ask MSBuild" instead of assuming <c>obj\</c>.</summary>
    private static readonly Regex ExtensionsPathOverride = new(
        @"<\s*(BaseIntermediateOutputPath|MSBuildProjectExtensionsPath|UseArtifactsOutput|ArtifactsPath)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>NuGet's restore output, written to the same directory as the stub.</summary>
    internal const string AssetsFileName = "project.assets.json";

    /// <summary>The package-name marker the LSP server's <c>ReqnrollProjectDetector</c> also uses.</summary>
    private const string ReqnrollNameMarker = "Reqnroll";

    /// <summary>A <c>"Name/Version":</c> key of project.assets.json's <c>targets</c> and <c>libraries</c> sections.</summary>
    private static readonly Regex AssetsLibraryKey = new(
        @"""(?<name>[^""/\\]+)/\d[^""/]*""\s*:",
        RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, string?> EvaluatedDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The stub: the bundle path in a property, imported through that property. The path is MSBuild-escaped
    /// (<c>%</c> <c>$</c> <c>@</c> <c>;</c>) and XML-escaped, and never appears inside a quoted
    /// condition literal: the extension's install path contains the Windows user name, and a name such
    /// as O'Brien, or a path with <c>&amp;</c> or <c>$</c>, must not turn the stub into a file that breaks
    /// every build of the project.
    /// </summary>
    public static string BuildStubXml(string bundleTargetsPath) =>
        "<Project>" + Environment.NewLine +
        "  <!-- Written by the Reqnroll IDE extension (issue #741): connects this project to the Reqnroll" + Environment.NewLine +
        "       Microsoft.Testing.Platform test-outcome reporter. Project-local and inert when the extension" + Environment.NewLine +
        "       is not installed. Opt out with <ReqnrollIdeSupportDisableMtpReporter>true</ReqnrollIdeSupportDisableMtpReporter>. -->" + Environment.NewLine +
        "  <PropertyGroup>" + Environment.NewLine +
        $"    <_ReqnrollIdeMtpReporterBundle>{EscapeForMSBuildXml(bundleTargetsPath)}</_ReqnrollIdeMtpReporterBundle>" + Environment.NewLine +
        "  </PropertyGroup>" + Environment.NewLine +
        "  <Import Project=\"$(_ReqnrollIdeMtpReporterBundle)\" Condition=\"Exists('$(_ReqnrollIdeMtpReporterBundle)')\" />" + Environment.NewLine +
        "</Project>" + Environment.NewLine;

    /// <summary>MSBuild escaping (<c>%</c> first) then XML text escaping.</summary>
    internal static string EscapeForMSBuildXml(string value) =>
        value.Replace("%", "%25").Replace("$", "%24").Replace("@", "%40").Replace(";", "%3B")
             .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// The directory MSBuild imports <c>$(MSBuildProjectFile).*.targets</c> from. Fast path:
    /// <c>&lt;project dir&gt;\obj\</c>, unless the project file or a <c>Directory.Build.props</c> above it
    /// mentions a property that moves it — then <paramref name="evaluateProjectExtensionsPath"/> (a real
    /// MSBuild evaluation) decides, and null means "unknown, don't write".
    /// </summary>
    public static string? ResolveProjectExtensionsDirectory(
        string projectFile,
        Func<string, string?> readTextOrNull,
        Func<string, string?> evaluateProjectExtensionsPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectFile);
        if (string.IsNullOrEmpty(projectDirectory)) return null;

        if (!MentionsExtensionsPathOverride(projectFile, projectDirectory!, readTextOrNull))
            return Path.Combine(projectDirectory!, "obj");

        var evaluated = evaluateProjectExtensionsPath(projectFile);
        if (string.IsNullOrWhiteSpace(evaluated)) return null;
        return Path.IsPathRooted(evaluated) ? evaluated!.Trim() : Path.GetFullPath(Path.Combine(projectDirectory!, evaluated!.Trim()));
    }

    private static bool MentionsExtensionsPathOverride(string projectFile, string projectDirectory, Func<string, string?> readTextOrNull)
    {
        var projectXml = readTextOrNull(projectFile);
        if (projectXml is not null && ExtensionsPathOverride.IsMatch(projectXml)) return true;

        for (var dir = new DirectoryInfo(projectDirectory); dir is not null; dir = dir.Parent)
        {
            var props = readTextOrNull(Path.Combine(dir.FullName, "Directory.Build.props"));
            if (props is not null && ExtensionsPathOverride.IsMatch(props)) return true;
        }
        return false;
    }

    /// <summary>Writes (or refreshes) the stub for one project. Returns the stub path written or already current, or null when skipped/failed — every failure is logged and swallowed.</summary>
    public static string? TryWriteStub(
        string projectFile,
        string bundleTargetsPath,
        IIdeSupportLogger logger,
        Func<string, string?>? readTextOrNull = null,
        Func<string, string?>? evaluateProjectExtensionsPath = null)
    {
        try
        {
            if (!projectFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(projectFile))
                return null;

            var directory = ResolveProjectExtensionsDirectory(
                projectFile,
                readTextOrNull ?? ReadTextOrNull,
                evaluateProjectExtensionsPath ?? (p => EvaluatedDirectories.GetOrAdd(p, EvaluateProjectExtensionsPathViaDotnet)));
            if (directory is null)
            {
                logger.LogVerbose($"{nameof(MtpProjectStubs)}: could not determine MSBuildProjectExtensionsPath for '{projectFile}'; no MTP reporter stub written.");
                return null;
            }

            var stub = Path.Combine(directory, Path.GetFileName(projectFile) + StubFileSuffix);
            var xml = BuildStubXml(bundleTargetsPath);
            if (File.Exists(stub) && File.ReadAllText(stub) == xml)
                return stub; // Unchanged: don't touch its timestamp.

            Directory.CreateDirectory(directory);
            File.WriteAllText(stub, xml);
            logger.LogVerbose($"{nameof(MtpProjectStubs)}: wrote MTP reporter stub {stub}");
            return stub;
        }
        catch (Exception ex)
        {
            logger.LogException(ex, $"{nameof(MtpProjectStubs)}: could not write the MTP reporter stub for '{projectFile}'");
            return null;
        }
    }

    /// <summary>
    /// Brings one project's stub in line with whether it uses Reqnroll (see the class remarks): written
    /// or refreshed for a Reqnroll project, removed from a restored project that does not use Reqnroll,
    /// untouched while the project has no restore output yet. Every failure is logged and swallowed.
    /// </summary>
    public static MtpStubSyncResult TrySyncStub(
        string projectFile,
        string bundleTargetsPath,
        IIdeSupportLogger logger,
        Func<string, string?>? readTextOrNull = null,
        Func<string, string?>? evaluateProjectExtensionsPath = null)
    {
        try
        {
            if (!projectFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(projectFile))
                return MtpStubSyncResult.Skipped;

            readTextOrNull ??= ReadTextOrNull;
            var directory = ResolveProjectExtensionsDirectory(
                projectFile,
                readTextOrNull,
                evaluateProjectExtensionsPath ?? (p => EvaluatedDirectories.GetOrAdd(p, EvaluateProjectExtensionsPathViaDotnet)));
            if (directory is null)
                return MtpStubSyncResult.Skipped;

            switch (DetectReqnrollUsage(readTextOrNull(Path.Combine(directory, AssetsFileName))))
            {
                case true:
                    return TryWriteStub(projectFile, bundleTargetsPath, logger, readTextOrNull, _ => directory) is null
                        ? MtpStubSyncResult.Skipped
                        : MtpStubSyncResult.Written;
                case false:
                    var stub = Path.Combine(directory, Path.GetFileName(projectFile) + StubFileSuffix);
                    if (File.Exists(stub))
                    {
                        File.Delete(stub);
                        logger.LogVerbose($"{nameof(MtpProjectStubs)}: removed the MTP reporter stub {stub}; the project does not use Reqnroll.");
                    }
                    return MtpStubSyncResult.NotReqnroll;
                default:
                    return MtpStubSyncResult.NotRestored;
            }
        }
        catch (Exception ex)
        {
            logger.LogException(ex, $"{nameof(MtpProjectStubs)}: could not update the MTP reporter stub for '{projectFile}'");
            return MtpStubSyncResult.Skipped;
        }
    }

    /// <summary>
    /// Whether a project's <c>project.assets.json</c> text lists a package or project reference whose
    /// name contains "Reqnroll" (case-insensitive) anywhere in its restore graph; null when there is
    /// no restore output yet, so the answer is unknown.
    /// </summary>
    internal static bool? DetectReqnrollUsage(string? projectAssetsJson)
    {
        if (projectAssetsJson is null) return null;
        foreach (Match match in AssetsLibraryKey.Matches(projectAssetsJson))
            if (match.Groups["name"].Value.IndexOf(ReqnrollNameMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    /// <summary>Syncs the stub of every project in <paramref name="projectFiles"/> (<see cref="TrySyncStub"/>); returns how many have a stub in place.</summary>
    public static int SyncStubs(IEnumerable<string> projectFiles, string bundleTargetsPath, IIdeSupportLogger logger)
    {
        var count = 0;
        foreach (var projectFile in projectFiles)
            if (TrySyncStub(projectFile, bundleTargetsPath, logger) == MtpStubSyncResult.Written)
                count++;
        return count;
    }

    /// <summary>
    /// Deletes the per-user <c>%LOCALAPPDATA%\Microsoft\MSBuild\Current\Microsoft.Common.targets\ImportAfter\Reqnroll.IdeSupport.TestReporter.MTP.g.targets</c>
    /// an earlier version of this extension wrote, which affected every MSBuild build for the Windows
    /// user. Returns true when it removed one.
    /// </summary>
    public static bool TryRemoveLegacyImportAfterFile(IIdeSupportLogger logger, string? importAfterDirectory = null)
    {
        try
        {
            importAfterDirectory ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "MSBuild", "Current", "Microsoft.Common.targets", "ImportAfter");
            var legacy = Path.Combine(importAfterDirectory, LegacyImportAfterFileName);
            if (!File.Exists(legacy)) return false;

            File.Delete(legacy);
            logger.LogInfo($"{nameof(MtpProjectStubs)}: removed the legacy per-user MTP reporter registration {legacy}");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogException(ex, $"{nameof(MtpProjectStubs)}: could not remove the legacy per-user MTP reporter registration");
            return false;
        }
    }

    private static string? ReadTextOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary><c>dotnet msbuild &lt;project&gt; -getProperty:MSBuildProjectExtensionsPath</c> — only for projects that move obj\. Null on any failure or timeout.</summary>
    internal static string? EvaluateProjectExtensionsPathViaDotnet(string projectFile)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", $"msbuild \"{projectFile}\" -getProperty:MSBuildProjectExtensionsPath -nologo")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectFile) ?? Environment.CurrentDirectory,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            // Event-based reads so the timeout below is real: a blocking ReadToEnd would wait on a hung
            // process forever, before WaitForExit ever started counting.
            var output = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { /* already exited */ }
                return null;
            }
            process.WaitForExit(); // Flushes the asynchronous output handlers.
            if (process.ExitCode != 0) return null;
            // A single -getProperty prints the bare value.
            string value;
            lock (output) value = output.ToString().Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>What <see cref="MtpProjectStubs.TrySyncStub"/> did for one project.</summary>
public enum MtpStubSyncResult
{
    /// <summary>Not a C# project, its obj\ location is unknown, or an error was logged.</summary>
    Skipped,
    /// <summary>A Reqnroll project: its stub is written and current.</summary>
    Written,
    /// <summary>A restored project that does not use Reqnroll: it has no stub (any earlier one was removed).</summary>
    NotReqnroll,
    /// <summary>No restore output yet, so Reqnroll use is unknown: left as it was.</summary>
    NotRestored,
}
