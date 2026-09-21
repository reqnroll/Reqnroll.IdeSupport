using System.IO;

namespace Reqnroll.IdeSupport.VisualStudio.TestReporter;

/// <summary>
/// Resolves the bundled <c>Reqnroll.IdeSupport.TestReporter.MTP.dll</c> (issue #715 phase 4) — the
/// Microsoft.Testing.Platform (MTP) counterpart to <see cref="TestLogger.TestLoggerActivationRules"/>,
/// packaged under <c>MtpReporter\</c> instead of <c>TestLogger\</c>.
/// </summary>
/// <remarks>
/// Unlike the VSTest logger (injected per-run via runsettings/<c>TestAdaptersPaths</c>, which needs a
/// <em>directory</em>), this assembly is referenced via a <c>HintPath</c> inside an
/// ephemerally-injected <c>.targets</c> file (<see cref="MtpEphemeralInjection"/>'s
/// <c>CustomAfterMicrosoftCommonTargets</c> mechanism, plan §5.6), set once for the whole
/// <c>devenv.exe</c> session rather than per Test Explorer run — so callers need the DLL's own file
/// path, not a containing directory.
/// </remarks>
public static class MtpReporterPathResolver
{
    internal const string ReporterSubdirectory = "MtpReporter";
    internal const string ReporterAssemblyFileName = "Reqnroll.IdeSupport.TestReporter.MTP.dll";

    /// <summary>
    /// The VSIX places the reporter under <c>MtpReporter\</c> next to the extension assembly. Returns
    /// null when it isn't there rather than pointing an injected reference at a non-existent file.
    /// </summary>
    /// <param name="extensionAssemblyLocation">
    /// <c>typeof(ReqnrollPluginPackage).Assembly.Location</c> at the real call site; taken as a
    /// parameter (rather than reflecting on this class's own assembly, which lives in a different
    /// project than the extension package) so a test can pass an arbitrary directory.
    /// </param>
    public static string? Resolve(string extensionAssemblyLocation)
    {
        var extensionDirectory = Path.GetDirectoryName(extensionAssemblyLocation);
        if (extensionDirectory is null) return null;

        var path = Path.Combine(extensionDirectory, ReporterSubdirectory, ReporterAssemblyFileName);
        return File.Exists(path) ? path : null;
    }
}
