using System;
using System.IO;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.TestLogger;

/// <summary>
/// Pure decision logic for whether/where to activate the bundled Reqnroll VSTest logger, extracted
/// from <see cref="ReqnrollTestLoggerRunSettingsService"/> so it can be unit tested without loading
/// any <c>Microsoft.VisualStudio.TestWindow.*</c> assembly at all. That class implements
/// <c>IRunSettingsService</c> and has members (<c>AddRunSettings</c>) whose parameter types come
/// from <c>Microsoft.VisualStudio.TestWindow.Interfaces</c> -- the .NET type loader must resolve
/// every member signature on a type before constructing an instance of it, so merely instantiating
/// that class (even just to call one of its unrelated pure helpers) forces that assembly to load.
/// It's supplied by devenv.exe at runtime and isn't present in a standalone test run, so no member
/// of that class can be unit tested at all without either accepting a VS-install-version-coupled
/// test dependency or, as here, moving the VS-independent logic somewhere the loader never touches
/// those types. See the tracking issue for auditing other classes with the same shape.
/// </summary>
internal static class TestLoggerActivationRules
{
    internal const string DisableEnvironmentVariable = "REQNROLL_IDE_DISABLE_TEST_LOGGER";
    private const string ReqnrollRuntimeAssemblyFileName = "Reqnroll.dll";

    /// <summary>Kill switch: true when <see cref="DisableEnvironmentVariable"/> is set to anything other than absent, empty, "0", or "false" (case-insensitive) -- fail safe, not fail open.</summary>
    internal static bool IsDisabled()
    {
        var value = Environment.GetEnvironmentVariable(DisableEnvironmentVariable);
        return !string.IsNullOrEmpty(value) && value != "0" && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A test container is "Reqnroll's" if the Reqnroll runtime sits beside it — every Reqnroll test
    /// project copies <c>Reqnroll.dll</c> to its output. Cheap, no project-system round-trip, and good
    /// enough to keep the logger out of unrelated solutions.
    /// </summary>
    internal static bool IsReqnrollTestContainer(string source, IIdeSupportLogger logger)
    {
        try
        {
            var directory = Path.GetDirectoryName(source);
            return directory is not null && File.Exists(Path.Combine(directory, ReqnrollRuntimeAssemblyFileName));
        }
        catch (Exception ex)
        {
            logger.LogVerbose($"{nameof(TestLoggerActivationRules)}: could not inspect container '{source}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The VSIX places the logger under <c>TestLogger\</c> next to the extension assembly. Returns
    /// null when it isn't there rather than pointing vstest at a non-existent directory.
    /// </summary>
    /// <param name="extensionAssemblyLocation">
    /// <c>typeof(ReqnrollTestLoggerRunSettingsService).Assembly.Location</c> at the real call site;
    /// taken as a parameter (rather than reflecting on this class's own assembly) so a test can pass
    /// an arbitrary directory instead of mutating this test run's actual output folder.
    /// </param>
    internal static string? ResolveLoggerDirectory(string extensionAssemblyLocation)
    {
        var extensionDirectory = Path.GetDirectoryName(extensionAssemblyLocation);
        if (extensionDirectory is null) return null;

        var loggerDirectory = Path.Combine(extensionDirectory, TestLoggerRunSettings.LoggerSubdirectory);
        return File.Exists(Path.Combine(loggerDirectory, TestLoggerRunSettings.LoggerAssemblyFileName))
            ? loggerDirectory
            : null;
    }
}
