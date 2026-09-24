using System.IO;

namespace Reqnroll.IdeSupport.VisualStudio.TestReporter;

/// <summary>
/// Resolves the bundled Microsoft.Testing.Platform (MTP) reporter <em>source bundle</em> (issue #741)
/// — the MTP counterpart to <see cref="TestLogger.TestLoggerActivationRules"/>, packaged under
/// <c>MtpReporter\</c>: <c>Reqnroll.IdeSupport.TestReporter.MTP.targets</c> plus the
/// <c>ReporterSource\*.cs</c> it compiles into a user's test project.
/// </summary>
/// <remarks>
/// Callers need the <c>.targets</c> file's own path: <see cref="MtpProjectStubs"/> writes it into each
/// project's <c>obj\&lt;Project&gt;.csproj.reqnroll-ide.targets</c> stub as an <c>Import</c>.
/// </remarks>
public static class MtpReporterPathResolver
{
    internal const string ReporterSubdirectory = "MtpReporter";
    internal const string BundleTargetsFileName = "Reqnroll.IdeSupport.TestReporter.MTP.targets";
    internal const string SourceSubdirectory = "ReporterSource";

    /// <summary>
    /// The VSIX places the bundle under <c>MtpReporter\</c> next to the extension assembly. Returns
    /// null unless both the <c>.targets</c> file and its <c>ReporterSource\</c> directory are there,
    /// rather than pointing a stub at an incomplete bundle.
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

        var bundleDirectory = Path.Combine(extensionDirectory, ReporterSubdirectory);
        var targets = Path.Combine(bundleDirectory, BundleTargetsFileName);
        return File.Exists(targets) && Directory.Exists(Path.Combine(bundleDirectory, SourceSubdirectory)) ? targets : null;
    }
}
