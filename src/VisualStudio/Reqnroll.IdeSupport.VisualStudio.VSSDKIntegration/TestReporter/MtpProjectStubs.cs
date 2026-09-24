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
/// <b>No MTP detection here.</b> The imported <c>.targets</c> gates itself at build time on the real,
/// evaluated properties (language, test host, MTP opt-in, TFM, LangVersion, resolved
/// Microsoft.Testing.Platform version) and adds nothing to a project that fails any gate, so a stub is
/// written for every C# project in the solution. That replaces the ad hoc text scans that issue #722
/// showed miss projects made MTP-capable through imported props.
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

    private static readonly ConcurrentDictionary<string, string?> EvaluatedDirectories = new(StringComparer.OrdinalIgnoreCase);

    public static string BuildStubXml(string bundleTargetsPath) =>
        "<Project>" + Environment.NewLine +
        "  <!-- Written by the Reqnroll IDE extension (issue #741): connects this project to the Reqnroll" + Environment.NewLine +
        "       Microsoft.Testing.Platform test-outcome reporter. Project-local and inert when the extension" + Environment.NewLine +
        "       is not installed. Opt out with <ReqnrollIdeSupportDisableMtpReporter>true</ReqnrollIdeSupportDisableMtpReporter>. -->" + Environment.NewLine +
        $"  <Import Project=\"{bundleTargetsPath}\" Condition=\"Exists('{bundleTargetsPath}')\" />" + Environment.NewLine +
        "</Project>" + Environment.NewLine;

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

    /// <summary>Writes stubs for every C# project in <paramref name="projectFiles"/>; returns how many are in place.</summary>
    public static int WriteStubs(IEnumerable<string> projectFiles, string bundleTargetsPath, IIdeSupportLogger logger)
    {
        var count = 0;
        foreach (var projectFile in projectFiles)
            if (TryWriteStub(projectFile, bundleTargetsPath, logger) is not null)
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
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000) || process.ExitCode != 0) return null;
            // A single -getProperty prints the bare value.
            var value = output.Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
