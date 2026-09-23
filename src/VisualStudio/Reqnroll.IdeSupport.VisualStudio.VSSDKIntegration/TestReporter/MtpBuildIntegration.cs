using System;
using System.IO;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.TestReporter;

/// <summary>
/// Registers the bundled Microsoft.Testing.Platform (MTP) reporter with every MTP-capable .NET
/// project build for this Windows user — issue #715 plan §5.6/§5.7, redesigned after the original
/// <c>CustomAfterMicrosoftCommonTargets</c> environment-variable injection (see git history:
/// <c>MtpEphemeralInjection</c>/<c>MtpProjectDetection</c>) was live-verified to never actually reach
/// the build: VS's Test Explorer build runs through a reused MSBuild worker node/evaluation context
/// whose environment snapshot predates this VSIX's package load, so
/// <c>Environment.SetEnvironmentVariable(...)</c> on devenv.exe's own process was invisible to it —
/// confirmed by inspecting the generated <c>SelfRegisteredExtensions.cs</c>, which never contained a
/// call into our <c>TestingPlatformBuilderHook</c> despite the env var being set and the project being
/// correctly MTP-capable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mechanism</b>: drops a small, fixed-path <c>.targets</c> file into
/// <c>%LOCALAPPDATA%\Microsoft\MSBuild\Current\Microsoft.Common.targets\ImportAfter\</c> — a
/// well-known, per-user MSBuild extensibility point that <c>Microsoft.Common.CurrentVersion.targets</c>
/// itself unconditionally wildcard-imports (<c>Condition="exists(...)"</c>) during every project
/// evaluation, for every MSBuild host (devenv's in-proc evaluator, an out-of-proc MSBuild.exe worker
/// node regardless of when it started, or a bare <c>dotnet build</c>/<c>dotnet msbuild</c>) — there is
/// no environment variable or process-timing dependency left at all. Unlike the single-slot
/// <c>CustomAfterMicrosoftCommonTargets</c> property the old design used, <c>ImportAfter</c> is a
/// directory multiple tools can each drop their own file into without conflicting, so there is no
/// pre-existing-value chain-import concern to carry forward either.
/// </para>
/// <para>
/// <b>Self-gating, not solution-scoped.</b> The old design only injected into projects under the
/// currently-loaded solution, decided by an ad hoc C# text-scan (<c>MtpProjectDetection</c>, itself
/// later patched for issue #722 — a project made MTP-capable only through an imported props file) run
/// once per solution load. That whole scan is now unnecessary and has been removed: the dropped
/// <c>.targets</c> file carries its own MSBuild <c>Condition</c>, keyed on the real, evaluated
/// <c>IsTestingPlatformApplication</c>/<c>EnableMSTestRunner</c>/<c>EnableNUnitRunner</c>/
/// <c>UseMicrosoftTestingPlatformRunner</c> properties of whichever project MSBuild happens to be
/// evaluating — real MSBuild evaluation sees a property set via an imported props file for free
/// (issue #722's whole problem), and this applies uniformly regardless of which solution or VS session
/// is building, so there is no "which solution is this for" question left to answer at all.
/// </para>
/// <para>
/// <b>Blast-radius trade-off</b>: because the dropped file is user-global rather than solution-scoped,
/// it now affects every MTP-capable .NET project build for this Windows user, not only ones opened
/// through this extension. The added <c>&lt;Reference&gt;</c>/<c>&lt;TestingPlatformBuilderHook&gt;</c>
/// only take effect for a project that is already MTP-capable, and the reporter itself is inert with no
/// matching session breadcrumb (see <c>ReqnrollMtpReporter.TryConnect</c>), so the practical effect
/// elsewhere is one extra assembly loaded into that project's test host, not a behavior change — an
/// accepted trade-off for a mechanism that actually reaches the build.
/// </para>
/// <para>
/// <b>Self-healing across extension updates/uninstalls.</b> The dropped file is rewritten (not merely
/// written-if-absent) every time this runs, so the newest installed VS session's reporter path wins if
/// more than one VS install/hive shares this Windows user profile; a version-mismatched or
/// since-uninstalled path is harmless because MSBuild resolves the file's own <c>Exists(...)</c> clause
/// on every evaluation. Kept independent of any <c>Microsoft.VisualStudio.*</c> type so it stays
/// unit-testable without a VS install.
/// </para>
/// </remarks>
public static class MtpBuildIntegration
{
    internal const string HookGuid = "a1d3c2f0-6b8e-4f2a-9c7d-3e5f8b1a4d6c";

    private const string FileName = "Reqnroll.IdeSupport.TestReporter.MTP.g.targets";

    /// <summary>
    /// Writes/overwrites the ImportAfter file for <paramref name="reporterDllPath"/>. Returns the path
    /// written, or <c>null</c> on failure — every failure is logged and swallowed, this is a
    /// best-effort enhancement and must never prevent package initialization from completing.
    /// </summary>
    public static string? TryEnable(string reporterDllPath, IIdeSupportLogger logger) =>
        TryEnable(reporterDllPath, ResolveImportAfterDirectory(), logger);

    /// <summary><paramref name="importAfterDirectory"/> is injected for testability — production callers use the two-argument overload, which defaults to <see cref="ResolveImportAfterDirectory"/>.</summary>
    internal static string? TryEnable(string reporterDllPath, string importAfterDirectory, IIdeSupportLogger logger)
    {
        try
        {
            if (!File.Exists(reporterDllPath))
            {
                logger.LogVerbose($"{nameof(MtpBuildIntegration)}: bundled reporter '{reporterDllPath}' not found; not enabling.");
                return null;
            }

            Directory.CreateDirectory(importAfterDirectory);
            var file = Path.Combine(importAfterDirectory, FileName);
            File.WriteAllText(file, BuildTargetsXml(reporterDllPath));

            logger.LogInfo($"{nameof(MtpBuildIntegration)}: registered the MTP reporter for this user at {file}");
            return file;
        }
        catch (Exception ex)
        {
            logger.LogException(ex, $"{nameof(MtpBuildIntegration)}: TryEnable failed");
            return null;
        }
    }

    /// <summary><c>%LOCALAPPDATA%\Microsoft\MSBuild\Current\Microsoft.Common.targets\ImportAfter</c> — matches <c>$(MSBuildUserExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.targets\ImportAfter</c> as <c>Microsoft.Common.CurrentVersion.targets</c> itself resolves it for any SDK-style project (confirmed via <c>dotnet msbuild -getProperty:MSBuildUserExtensionsPath,MSBuildToolsVersion</c>); non-SDK-style projects can never be MTP-capable, so the "Current" toolset segment is the only one that matters here.</summary>
    internal static string ResolveImportAfterDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "MSBuild", "Current", "Microsoft.Common.targets", "ImportAfter");

    internal static string BuildTargetsXml(string reporterDllPath)
    {
        // '$(IsTestProject)' == 'true' is required, not just the MTP opt-in properties: merely
        // referencing the Microsoft.Testing.Platform package — even with ExcludeAssets="runtime", as
        // this reporter's own project does — defaults IsTestingPlatformApplication to true (confirmed
        // live via `dotnet msbuild -getProperty`), so without this guard, any library that references
        // Microsoft.Testing.Platform for its own reasons (this reporter project included — it would
        // self-match and pull in a stale copy of itself, the exact conflict that surfaced this) would
        // match too. IsTestProject is the standard SDK/dotnet-test signal for "this is an actual test
        // host project", not merely something that references test-platform types.
        var condition =
            $"Exists('{reporterDllPath}') and '$(IsTestProject)' == 'true' and (" +
            "'$(IsTestingPlatformApplication)' == 'true' or " +
            "'$(EnableMSTestRunner)' == 'true' or " +
            "'$(EnableNUnitRunner)' == 'true' or " +
            "'$(UseMicrosoftTestingPlatformRunner)' == 'true')";

        return
            "<Project>" + Environment.NewLine +
            $"  <ItemGroup Condition=\"{condition}\">" + Environment.NewLine +
            "    <Reference Include=\"Reqnroll.IdeSupport.TestReporter.MTP\">" + Environment.NewLine +
            $"      <HintPath>{reporterDllPath}</HintPath>" + Environment.NewLine +
            "    </Reference>" + Environment.NewLine +
            $"    <TestingPlatformBuilderHook Include=\"{HookGuid}\">" + Environment.NewLine +
            "      <DisplayName>Reqnroll.IdeSupport.TestReporter.MTP</DisplayName>" + Environment.NewLine +
            "      <TypeFullName>Reqnroll.IdeSupport.TestReporter.MTP.TestingPlatformBuilderHook</TypeFullName>" + Environment.NewLine +
            "    </TestingPlatformBuilderHook>" + Environment.NewLine +
            "  </ItemGroup>" + Environment.NewLine +
            "</Project>" + Environment.NewLine;
    }
}
