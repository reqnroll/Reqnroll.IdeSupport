using System.ComponentModel.Composition;
using System.Xml.XPath;
using Microsoft.VisualStudio.TestWindow.Extensibility;
using Reqnroll.IdeSupport.Common.Logging;
using ILogger = Microsoft.VisualStudio.TestWindow.Extensibility.ILogger;

namespace Reqnroll.IdeSupport.VisualStudio.TestLogger;

/// <summary>
/// Registers the VSIX-bundled Reqnroll VSTest logger for every Test Explorer <em>execution</em> request
/// by merging <c>TestAdaptersPaths</c> + a <c>LoggerRunSettings</c> entry into the run's effective
/// runsettings (see <see cref="TestLoggerRunSettings"/> for the merge rules).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IRunSettingsService"/> is a MEF <c>[ImportMany]</c> of Test Explorer's
/// <c>RequestConfigurationFactory</c> (Microsoft.VisualStudio.TestWindow.Core, in-proc in devenv.exe).
/// For each discovery/execution request VS calls every export's <see cref="AddRunSettings"/> in turn,
/// feeding each one the previous one's output; a thrown exception is logged to the Tests output pane
/// and VS falls back to the document as it was before that service ran. VS's own Hot Reload support
/// and Fine Code Coverage's MS-collector injection use exactly this hook.
/// </para>
/// <para>
/// Decisions worth knowing when reading the Tests output pane:
/// </para>
/// <list type="bullet">
///   <item>Discovery requests are left untouched — loggers only matter for execution, and vstest would
///   load ours for nothing on every background discovery pass.</item>
///   <item>Runs whose test containers have no <c>Reqnroll.dll</c> beside them are left untouched, so
///   non-Reqnroll solutions never see our logger.</item>
///   <item>If the bundled logger assembly can't be found (broken deployment), nothing is injected and a
///   warning is written — a missing <c>TestAdaptersPaths</c> entry would otherwise make vstest warn on
///   every run.</item>
/// </list>
/// <para>
/// <b>Spike status:</b> the logger currently writes results to
/// <c>%LOCALAPPDATA%\Reqnroll\reqnroll-vs-testlogger-&lt;date&gt;-&lt;devenv pid&gt;.log</c>; nothing in the
/// extension reads it back yet. That file appearing (with the injected parameters echoed on its first
/// line) is the evidence this hook works end to end from Test Explorer.
/// </para>
/// </remarks>
[Export(typeof(IRunSettingsService))]
public sealed class ReqnrollTestLoggerRunSettingsService : IRunSettingsService
{
    // Same in-proc devenv.exe file log as the rest of the extension (see RunTestCodeLensCallbackListener
    // for why a standalone instance rather than a MEF import).
    private static readonly IIdeSupportLogger Logger = new SynchronousFileLogger("vs", "ext", TraceLevel.Verbose);

    private const string ReqnrollRuntimeAssemblyFileName = "Reqnroll.dll";

    /// <inheritdoc />
    public string Name => "Reqnroll IDE test logger";

    /// <inheritdoc />
    public IXPathNavigable AddRunSettings(IXPathNavigable inputRunSettingDocument, IRunSettingsConfigurationInfo configurationInfo, ILogger log)
    {
        try
        {
            if (configurationInfo.RequestState != RunSettingConfigurationInfoState.Execution)
            {
                Logger.LogVerbose($"{nameof(ReqnrollTestLoggerRunSettingsService)}: {configurationInfo.RequestState} request — not injecting.");
                return inputRunSettingDocument;
            }

            var containers = (configurationInfo.TestContainers ?? Enumerable.Empty<ITestContainer>())
                .Select(c => c.Source)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            Logger.LogInfo($"{nameof(ReqnrollTestLoggerRunSettingsService)}: execution request for {containers.Count} container(s): {string.Join(", ", containers)}");

            if (!containers.Any(IsReqnrollTestContainer))
            {
                log.Log(MessageLevel.Informational, "Reqnroll: no Reqnroll test containers in this run; test logger not registered.");
                return inputRunSettingDocument;
            }

            var loggerDirectory = ResolveLoggerDirectory();
            if (loggerDirectory is null)
            {
                log.Log(MessageLevel.Warning, $"Reqnroll: bundled test logger '{TestLoggerRunSettings.LoggerAssemblyFileName}' not found next to the extension; live test outcomes will not be recorded.");
                return inputRunSettingDocument;
            }

            int ideProcessId;
            using (var devenv = Process.GetCurrentProcess())
                ideProcessId = devenv.Id;
            var logFilePath = Path.Combine(
                ReqnrollLogPaths.ResolveLogDirectory(),
                $"reqnroll-vs-testlogger-{DateTime.Now:yyyyMMdd}-{ideProcessId}.log");

            var merged = TestLoggerRunSettings.Inject(inputRunSettingDocument, loggerDirectory, new[]
            {
                new KeyValuePair<string, string>(TestLoggerRunSettings.LogFilePathParameter, logFilePath),
                new KeyValuePair<string, string>(TestLoggerRunSettings.IdeProcessIdParameter, ideProcessId.ToString()),
            });

            log.Log(MessageLevel.Informational, $"Reqnroll: registered test logger from '{loggerDirectory}'; results will be appended to '{logFilePath}'.");
            Logger.LogVerbose($"{nameof(ReqnrollTestLoggerRunSettingsService)}: merged runsettings:{Environment.NewLine}{merged.OuterXml}");
            return merged;
        }
        catch (Exception ex)
        {
            // VS would also catch and revert, but reporting it ourselves keeps the message specific.
            log.Log(MessageLevel.Warning, $"Reqnroll: failed to register the test logger — {ex.Message}");
            Logger.LogException(ex, $"{nameof(ReqnrollTestLoggerRunSettingsService)}.{nameof(AddRunSettings)} failed");
            return inputRunSettingDocument;
        }
    }

    /// <summary>
    /// A test container is "Reqnroll's" if the Reqnroll runtime sits beside it — every Reqnroll test
    /// project copies <c>Reqnroll.dll</c> to its output. Cheap, no project-system round-trip, and good
    /// enough to keep the logger out of unrelated solutions.
    /// </summary>
    private static bool IsReqnrollTestContainer(string source)
    {
        try
        {
            var directory = Path.GetDirectoryName(source);
            return directory is not null && File.Exists(Path.Combine(directory, ReqnrollRuntimeAssemblyFileName));
        }
        catch (Exception ex)
        {
            Logger.LogVerbose($"{nameof(ReqnrollTestLoggerRunSettingsService)}: could not inspect container '{source}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The VSIX places the logger under <c>TestLogger\</c> next to this assembly (extension root).
    /// Returns null when it isn't there rather than pointing vstest at a non-existent directory.
    /// </summary>
    private static string? ResolveLoggerDirectory()
    {
        var extensionDirectory = Path.GetDirectoryName(typeof(ReqnrollTestLoggerRunSettingsService).Assembly.Location);
        if (extensionDirectory is null) return null;

        var loggerDirectory = Path.Combine(extensionDirectory, TestLoggerRunSettings.LoggerSubdirectory);
        return File.Exists(Path.Combine(loggerDirectory, TestLoggerRunSettings.LoggerAssemblyFileName))
            ? loggerDirectory
            : null;
    }
}
