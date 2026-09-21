using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.TestReporter;

/// <summary>
/// Ephemerally compiles the bundled <c>Reqnroll.IdeSupport.TestReporter.MTP</c> into every
/// Microsoft.Testing.Platform (MTP)-capable project built in this <c>devenv.exe</c> session — issue
/// #715 plan §5.6 — via the <c>CustomAfterMicrosoftCommonTargets</c> MSBuild extensibility point,
/// instead of the VSTest logger's runsettings-based per-run injection (<c>ReqnrollTestLoggerRunSettingsService</c>),
/// which under MTP is either silently ignored or, in the native <c>dotnet test</c> mode, not
/// applicable at all (VS's own Test Explorer never shells out to <c>dotnet test</c> in the first
/// place — plan §5.7: MTP-capable projects always run through VS's own "testing platform server
/// mode").
/// </summary>
/// <remarks>
/// <para>
/// <b>Set once per session, not per run</b> — unlike Rider's <c>ProcessBuilder</c>-per-invocation
/// model (issue #715 phase 4, Rider leg), VS's Test Explorer builds are not individually controlled
/// by this extension, so the only lever available is a process-wide environment variable, set as
/// early as possible (package load) so it's in place before any build happens in this session — same
/// reasoning as the existing autoload pattern (<c>ProvideAutoLoadAttribute</c> on
/// <c>ReqnrollPluginPackage</c>).
/// </para>
/// <para>
/// <b>Solution-wide gating.</b> The injected <c>&lt;Reference&gt;</c> is harmless but wasteful for a
/// project that doesn't use it, so this only sets the environment variable at all when at least one
/// project under the solution looks MTP-capable (<see cref="MtpProjectDetection"/>) — the common
/// all-VSTest solution never pays for it. A mixed solution still injects the reference into every
/// project built in this session, VSTest-only siblings included — an accepted, documented
/// imprecision (plan §5.7's "mixed solutions fall out for free" is about per-run detection like
/// Rider's/VS Code's; VS's coarser env-var-per-session model can't scope this exactly without giving
/// it a way to intercept individual Test Explorer builds, which it does not have).
/// </para>
/// <para>
/// <b>Unverified (plan §7 risk #1)</b>: whether VS's in-process MSBuild evaluation honors a
/// process-level environment variable set by the VSIX at load time is not live-tested. If it doesn't,
/// this degrades to exactly today's shipped behavior for MTP-capable projects (the pre-#700
/// reflection-bridge fallback) — not a regression, just no improvement, same framing the plan uses
/// throughout for this risk.
/// </para>
/// </remarks>
public static class MtpEphemeralInjection
{
    /// <summary>The MSBuild extensibility property this mechanism relies on (plan §5.6).</summary>
    public const string CustomAfterMicrosoftCommonTargetsVariable = "CustomAfterMicrosoftCommonTargets";

    /// <summary>
    /// Random, permanent identifier for this hook registration (plan §5.6/§7's "Include GUID is a
    /// random identifier — never copy one from another extension's props file" note) — must match the
    /// same literal value the Rider plugin (<c>RunTestRunner.kt</c>) and the phase-2 test fixture
    /// (<c>tests/Core/TestReporterFixtures/MsTestReqnrollMtp/MsTestReqnrollMtp.Fixture.csproj</c>)
    /// declare for this same hook.
    /// </summary>
    internal const string HookGuid = "a1d3c2f0-6b8e-4f2a-9c7d-3e5f8b1a4d6c";

    private static readonly HashSet<string> ExcludedDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules" };

    /// <summary>
    /// Scans <paramref name="solutionDirectory"/> for <c>.csproj</c> files (bounded — skips
    /// bin/obj/.git/.vs/node_modules) and, if any is MTP-capable, writes the ephemeral <c>.targets</c>
    /// file and sets <see cref="CustomAfterMicrosoftCommonTargetsVariable"/> for this process. No-op
    /// (env var left untouched) if <paramref name="reporterDllPath"/> isn't found, or no project is
    /// MTP-capable. Every failure is logged and swallowed — this is a best-effort enhancement, never a
    /// reason to fail package initialization.
    /// </summary>
    public static bool TryEnableForSolution(string solutionDirectory, string reporterDllPath, IIdeSupportLogger logger)
    {
        try
        {
            if (string.IsNullOrEmpty(solutionDirectory) || !Directory.Exists(solutionDirectory))
            {
                logger.LogVerbose($"{nameof(MtpEphemeralInjection)}: no solution directory to scan; not enabling.");
                return false;
            }

            if (!File.Exists(reporterDllPath))
            {
                logger.LogVerbose($"{nameof(MtpEphemeralInjection)}: bundled reporter '{reporterDllPath}' not found; not enabling.");
                return false;
            }

            var mtpCapable = EnumerateProjectFiles(solutionDirectory).Any(MtpProjectDetection.IsMtpCapable);
            if (!mtpCapable)
            {
                logger.LogVerbose($"{nameof(MtpEphemeralInjection)}: no MTP-capable project found under '{solutionDirectory}'; not enabling.");
                return false;
            }

            var preExisting = Environment.GetEnvironmentVariable(CustomAfterMicrosoftCommonTargetsVariable, EnvironmentVariableTarget.Process);
            var targetsFile = WriteTargetsFile(reporterDllPath, preExisting);
            Environment.SetEnvironmentVariable(CustomAfterMicrosoftCommonTargetsVariable, targetsFile, EnvironmentVariableTarget.Process);
            logger.LogInfo($"{nameof(MtpEphemeralInjection)}: enabled for this session — {CustomAfterMicrosoftCommonTargetsVariable}={targetsFile}");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogException(ex, $"{nameof(MtpEphemeralInjection)}: TryEnableForSolution failed");
            return false;
        }
    }

    /// <summary>Bounded recursive <c>*.csproj</c> walk, pruning heavy/irrelevant directories — mirrors the Rider plugin's own ad hoc-scan philosophy.</summary>
    internal static IEnumerable<string> EnumerateProjectFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.csproj"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;

            IEnumerable<string> subDirectories;
            try { subDirectories = Directory.EnumerateDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var subDirectory in subDirectories)
            {
                if (!ExcludedDirectoryNames.Contains(Path.GetFileName(subDirectory)))
                    stack.Push(subDirectory);
            }
        }
    }

    /// <summary>
    /// Writes a small, distinctly-named <c>.targets</c> file (plan §5.6) declaring a <c>HintPath</c>
    /// <c>&lt;Reference&gt;</c> to the bundled MTP reporter plus the
    /// <c>&lt;TestingPlatformBuilderHook&gt;</c> item that gets it auto-registered via MTP's own
    /// <c>SelfRegisteredExtensions</c> generation — never touching any project file itself.
    /// <paramref name="preExistingCustomAfterTargets"/>, when non-null, is chain-imported first (plan
    /// §7 risk #6): <c>CustomAfterMicrosoftCommonTargets</c> is a general-purpose MSBuild
    /// extensibility slot a repo could already be using for something unrelated — overwriting it
    /// outright would silently break that customization for the duration of this session.
    /// <c>Exists(...)</c> guards the import so a value that happened to be a stale/invalid path
    /// doesn't itself break every subsequent build.
    /// </summary>
    internal static string WriteTargetsFile(string reporterDllPath, string? preExistingCustomAfterTargets)
    {
        var dir = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-inject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Reqnroll.IdeSupport.TestReporter.MTP.g.targets");

        var chainImport = string.IsNullOrWhiteSpace(preExistingCustomAfterTargets)
            ? string.Empty
            : $"  <Import Project=\"{preExistingCustomAfterTargets}\" Condition=\"Exists('{preExistingCustomAfterTargets}')\" />{Environment.NewLine}";

        var xml =
            "<Project>" + Environment.NewLine +
            chainImport +
            "  <ItemGroup>" + Environment.NewLine +
            "    <Reference Include=\"Reqnroll.IdeSupport.TestReporter.MTP\">" + Environment.NewLine +
            $"      <HintPath>{reporterDllPath}</HintPath>" + Environment.NewLine +
            "    </Reference>" + Environment.NewLine +
            $"    <TestingPlatformBuilderHook Include=\"{HookGuid}\">" + Environment.NewLine +
            "      <DisplayName>Reqnroll.IdeSupport.TestReporter.MTP</DisplayName>" + Environment.NewLine +
            "      <TypeFullName>Reqnroll.IdeSupport.TestReporter.MTP.TestingPlatformBuilderHook</TypeFullName>" + Environment.NewLine +
            "    </TestingPlatformBuilderHook>" + Environment.NewLine +
            "  </ItemGroup>" + Environment.NewLine +
            "</Project>" + Environment.NewLine;

        File.WriteAllText(file, xml);
        return file;
    }
}
