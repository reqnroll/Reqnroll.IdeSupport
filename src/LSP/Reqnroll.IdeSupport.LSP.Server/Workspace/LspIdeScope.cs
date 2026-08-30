using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// Server-level singleton implementation of <see cref="IIdeScope"/> for the LSP server process.
/// Provides infrastructure services (logger, file system, monitoring) to project scopes and
/// configuration providers without any VSSDK dependency.
/// </summary>
public sealed class LspIdeScope : IIdeScope
{
    /// <summary>Initializes a new instance of the <see cref="LspIdeScope"/> class.</summary>
    public LspIdeScope(IIdeSupportLogger logger, ITelemetryService? telemetryService = null)
    {
        Logger = logger;
        FileSystem = new FileSystemForIDE();
        TelemetryService = telemetryService ?? NullLspTelemetryService.Instance;
        Actions = new LspIdeActions(logger);
    }

    /// <summary>Always <see langword="true"/>: the LSP server has no concept of an unloaded solution.</summary>
    public bool IsSolutionLoaded => true;
    /// <summary>Logger shared by all project scopes and configuration providers.</summary>
    public IIdeSupportLogger Logger { get; }
    /// <summary>
    /// Telemetry sink — the same DI-registered <see cref="LspErrorTelemetryService"/> singleton
    /// used everywhere else on the LSP server. Previously hardcoded to
    /// <see cref="NullLspTelemetryService"/>, which silently dropped errors reported through this
    /// property (e.g. <c>ProjectScopeDeveroomConfigurationProvider</c>'s config-load exceptions via
    /// <c>WatchedFilesHandler</c>) even after issue #255's fix — that fix only reached consumers
    /// that resolve <see cref="Common.Telemetry.IErrorTelemetryService"/>/<see cref="ITelemetryService"/>
    /// directly via DI, not this separate <see cref="IIdeScope.TelemetryService"/> access path
    /// (issue #255/#259).
    /// </summary>
    public ITelemetryService TelemetryService { get; }
    /// <summary>IDE action callbacks (e.g. error/warning display), implemented via logging for the LSP server.</summary>
    public IIdeActions Actions { get; }
    /// <summary>File system abstraction used by project scopes and configuration providers.</summary>
    public IFileSystemForIDE FileSystem { get; }

    // ── Inner type ────────────────────────────────────────────────────────────

    private sealed class LspIdeActions : IIdeActions
    {
        private readonly IIdeSupportLogger _logger;

        public LspIdeActions(IIdeSupportLogger logger) => _logger = logger;

        public void ShowError(string description, Exception exception)
            => _logger.LogError($"{description}: {exception}");

        public void ShowProblem(string message)
            => _logger.LogWarning(message);
    }
}
