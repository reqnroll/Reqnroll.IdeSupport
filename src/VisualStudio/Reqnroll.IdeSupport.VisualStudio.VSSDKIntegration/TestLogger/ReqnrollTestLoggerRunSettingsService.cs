using System.ComponentModel.Composition;
using System.Threading;
using System.Xml.XPath;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TestWindow.Extensibility;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
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
/// Kill switch: set <c>REQNROLL_IDE_DISABLE_TEST_LOGGER=1</c> in devenv's environment and nothing is
/// injected (the glyph falls back to the reflection bridge). For a misbehaving logger this is cheaper
/// than uninstalling the extension.
/// </para>
/// <para>
/// <b>LSP-server outcome pipeline.</b> Run registration used to be a same-process, in-memory call
/// (<c>TestOutcomeListener.RegisterRun()</c>); the receiver, store, and persistence now live in the
/// LSP server so every IDE shares one implementation, which makes this call a cross-process JSON-RPC
/// round trip over <c>LspInterceptingPipe</c>. <see cref="AddRunSettings"/> is a synchronous VS Test
/// Platform callback with no async overload, so that round trip is awaited synchronously via
/// <c>ThreadHelper.JoinableTaskFactory.Run</c> (the same sync-over-async bridge this codebase already
/// uses elsewhere, e.g. <c>VsUtils.GetInstalledNuGetPackages</c>), bounded by
/// <see cref="RegistrationTimeout"/> so a slow or unresponsive server degrades to "inject nothing for
/// this run" — same as today's "listener couldn't start" path — rather than hanging test-run
/// configuration. The pipe's own read/write pumps run on background tasks with no UI-thread affinity
/// (<c>ServerToVsPump</c>/<c>VsToServerPump</c>), so this does not depend on <c>AddRunSettings</c>
/// happening to run off the UI thread — JoinableTaskFactory.Run's reentrancy protection covers the
/// case where it doesn't.
/// </para>
/// </remarks>
[Export(typeof(IRunSettingsService))]
public sealed class ReqnrollTestLoggerRunSettingsService : IRunSettingsService
{
    private readonly IIdeSupportLogger _logger;

    private const string ReqnrollRuntimeAssemblyFileName = "Reqnroll.dll";
    internal const string DisableEnvironmentVariable = "REQNROLL_IDE_DISABLE_TEST_LOGGER";

    /// <summary>Bounds the blocking wait for the server's registration response — see the class remarks.</summary>
    internal static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// MEF composes this class in-proc in devenv.exe, the same composition root that exports the
    /// shared <see cref="IIdeSupportLogger"/> (see <c>IdeSupportLoggerExportProvider</c>) -- imported
    /// here rather than constructing a standalone <c>SynchronousFileLogger</c>, which would otherwise
    /// write to the same physical log file through its own unrelated write lock (see
    /// <c>RunTestCodeLensCallbackListener</c>'s remarks for the full reasoning).
    /// </summary>
    [ImportingConstructor]
    public ReqnrollTestLoggerRunSettingsService(IIdeSupportLogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Reqnroll IDE test logger";

    /// <inheritdoc />
    public IXPathNavigable AddRunSettings(IXPathNavigable inputRunSettingDocument, IRunSettingsConfigurationInfo configurationInfo, ILogger log)
    {
        try
        {
            if (IsDisabled())
            {
                // Visible in the Tests output pane, not just our own verbose log: a user who set this
                // kill switch themselves should see it confirmed without having to go find a log file.
                log.Log(MessageLevel.Informational, $"Reqnroll: test logger disabled via {DisableEnvironmentVariable}; not injecting.");
                _logger.LogVerbose($"{nameof(ReqnrollTestLoggerRunSettingsService)}: disabled via {DisableEnvironmentVariable}; not injecting.");
                return inputRunSettingDocument;
            }

            if (configurationInfo.RequestState != RunSettingConfigurationInfoState.Execution)
            {
                _logger.LogVerbose($"{nameof(ReqnrollTestLoggerRunSettingsService)}: {configurationInfo.RequestState} request — not injecting.");
                return inputRunSettingDocument;
            }

            var containers = (configurationInfo.TestContainers ?? Enumerable.Empty<ITestContainer>())
                .Select(c => c.Source)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            _logger.LogInfo($"{nameof(ReqnrollTestLoggerRunSettingsService)}: execution request for {containers.Count} container(s): {string.Join(", ", containers)}");

            if (!containers.Any(IsReqnrollTestContainer))
            {
                log.Log(MessageLevel.Informational, "Reqnroll: no Reqnroll test containers in this run; test logger not registered.");
                _logger.LogVerbose($"{nameof(ReqnrollTestLoggerRunSettingsService)}: no Reqnroll test containers in this run; not injecting.");
                return inputRunSettingDocument;
            }

            var loggerDirectory = ResolveLoggerDirectory();
            if (loggerDirectory is null)
            {
                log.Log(MessageLevel.Warning, $"Reqnroll: bundled test logger '{TestLoggerRunSettings.LoggerAssemblyFileName}' not found next to the extension; live test outcomes will not be recorded.");
                _logger.LogWarning($"{nameof(ReqnrollTestLoggerRunSettingsService)}: bundled test logger '{TestLoggerRunSettings.LoggerAssemblyFileName}' not found next to the extension; not injecting.");
                return inputRunSettingDocument;
            }

            var registration = RegisterRunBlocking();
            if (registration is null)
            {
                log.Log(MessageLevel.Warning, "Reqnroll: could not register this run with the LSP server for test outcomes; live test outcomes will not be recorded for this run.");
                return inputRunSettingDocument;
            }

            int ideProcessId;
            using (var devenv = Process.GetCurrentProcess())
                ideProcessId = devenv.Id;

            var parameters = new List<KeyValuePair<string, string>>
            {
                new(TestLoggerRunSettings.EndpointParameter, registration.Endpoint),
                new(TestLoggerRunSettings.RunIdParameter, registration.RunId),
                new(TestLoggerRunSettings.IdeProcessIdParameter, ideProcessId.ToString()),
            };

            // Optional troubleshooting mirror: the same NDJSON the IDE receives, appended to a file.
            var mirror = Environment.GetEnvironmentVariable(TestLoggerRunSettings.MirrorFileEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(mirror))
            {
                var mirrorPath = string.Equals(mirror, "1", StringComparison.Ordinal)
                    ? Path.Combine(ReqnrollLogPaths.ResolveLogDirectory(), $"reqnroll-vs-testlogger-{DateTime.Now:yyyyMMdd}-{ideProcessId}.ndjson")
                    : mirror!;
                parameters.Add(new KeyValuePair<string, string>(TestLoggerRunSettings.LogFilePathParameter, mirrorPath));
            }

            var merged = TestLoggerRunSettings.Inject(inputRunSettingDocument, loggerDirectory, parameters);

            log.Log(MessageLevel.Informational, $"Reqnroll: registered test logger from '{loggerDirectory}' (run {registration.RunId}, endpoint {registration.Endpoint}).");
            _logger.LogVerbose($"{nameof(ReqnrollTestLoggerRunSettingsService)}: merged runsettings:{Environment.NewLine}{merged.OuterXml}");
            return merged;
        }
        catch (Exception ex)
        {
            // VS would also catch and revert, but reporting it ourselves keeps the message specific.
            log.Log(MessageLevel.Warning, $"Reqnroll: failed to register the test logger — {ex.Message}");
            _logger.LogException(ex, $"{nameof(ReqnrollTestLoggerRunSettingsService)}.{nameof(AddRunSettings)} failed");
            return inputRunSettingDocument;
        }
    }

    /// <summary>
    /// Blocks (bounded by <see cref="RegistrationTimeout"/>) on the LSP-server round trip that mints
    /// this run's endpoint. Returns null on a missing connection, a timeout, or any exception —
    /// every one of those means "inject nothing", never "hang the run".
    /// </summary>
    private TestRunRegistration? RegisterRunBlocking()
    {
        var registerAsync = RunTestCodeLensRedirect.RegisterTestRunAsync;
        if (registerAsync is null)
        {
            _logger.LogWarning($"{nameof(ReqnrollTestLoggerRunSettingsService)}: LSP connection not established yet; not registering.");
            return null;
        }

        using var timeout = new CancellationTokenSource(RegistrationTimeout);
        try
        {
            // JoinableTaskFactory.Run, not a raw .GetAwaiter().GetResult(): same sync-over-async
            // bridge this codebase already uses elsewhere (VsUtils.GetInstalledNuGetPackages,
            // DocumentInitializationMonitor) to stay deadlock-safe under VS's threading model,
            // rather than a bare blocking wait the VSTHRD002 analyzer (correctly) flags as unsafe.
            return ThreadHelper.JoinableTaskFactory.Run(() => registerAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning($"{nameof(ReqnrollTestLoggerRunSettingsService)}: timed out after {RegistrationTimeout} waiting for the LSP server to register this run.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, $"{nameof(ReqnrollTestLoggerRunSettingsService)}: RegisterRunBlocking failed");
            return null;
        }
    }

    private bool IsDisabled()
    {
        var value = Environment.GetEnvironmentVariable(DisableEnvironmentVariable);
        return !string.IsNullOrEmpty(value) && value != "0" && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A test container is "Reqnroll's" if the Reqnroll runtime sits beside it — every Reqnroll test
    /// project copies <c>Reqnroll.dll</c> to its output. Cheap, no project-system round-trip, and good
    /// enough to keep the logger out of unrelated solutions.
    /// </summary>
    private bool IsReqnrollTestContainer(string source)
    {
        try
        {
            var directory = Path.GetDirectoryName(source);
            return directory is not null && File.Exists(Path.Combine(directory, ReqnrollRuntimeAssemblyFileName));
        }
        catch (Exception ex)
        {
            _logger.LogVerbose($"{nameof(ReqnrollTestLoggerRunSettingsService)}: could not inspect container '{source}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The VSIX places the logger under <c>TestLogger\</c> next to this assembly (extension root).
    /// Returns null when it isn't there rather than pointing vstest at a non-existent directory.
    /// </summary>
    private string? ResolveLoggerDirectory()
    {
        var extensionDirectory = Path.GetDirectoryName(typeof(ReqnrollTestLoggerRunSettingsService).Assembly.Location);
        if (extensionDirectory is null) return null;

        var loggerDirectory = Path.Combine(extensionDirectory, TestLoggerRunSettings.LoggerSubdirectory);
        return File.Exists(Path.Combine(loggerDirectory, TestLoggerRunSettings.LoggerAssemblyFileName))
            ? loggerDirectory
            : null;
    }
}
