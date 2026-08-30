#nullable disable
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// No-op <see cref="ITelemetryService"/> for the LSP server process.
/// Telemetry is not collected from the server side.
/// </summary>
public sealed class NullLspTelemetryService : ITelemetryService
{
    /// <summary>The shared singleton no-op telemetry service instance.</summary>
    public static readonly NullLspTelemetryService Instance = new();

    private NullLspTelemetryService() { }

    /// <summary>No-op: the LSP server does not track project-system open telemetry.</summary>
    public void MonitorOpenProjectSystem(IIdeScope ideScope) { }
    /// <summary>No-op: the LSP server does not track project-open telemetry.</summary>
    public void MonitorOpenProject(ProjectSettings settings, int? featureFileCount) { }
    /// <summary>No-op: the LSP server does not track feature-file-open telemetry.</summary>
    public void MonitorOpenFeatureFile(ProjectSettings projectSettings) { }
    /// <summary>No-op: the LSP server does not track extension-install telemetry.</summary>
    public void MonitorExtensionInstalled() { }
    /// <summary>No-op: the LSP server does not track extension-upgrade telemetry.</summary>
    public void MonitorExtensionUpgraded(string oldExtensionVersion) { }
    /// <summary>No-op: the LSP server does not track extension-usage-duration telemetry.</summary>
    public void MonitorExtensionDaysOfUsage(int usageDays) { }
    /// <summary>No-op: the LSP server does not track "add feature file" command telemetry.</summary>
    public void MonitorCommandAddFeatureFile(ProjectSettings projectSettings) { }
    /// <summary>No-op: the LSP server does not track "add reqnroll.json" command telemetry.</summary>
    public void MonitorCommandAddReqnrollConfigFile(ProjectSettings projectSettings) { }
    /// <summary>No-op: the LSP server does not report exceptions through this telemetry channel.</summary>
    public void MonitorError(System.Exception exception, bool? isFatal = null) { }
    /// <summary>No-op: the LSP server does not track project-template-wizard-started telemetry.</summary>
    public void MonitorProjectTemplateWizardStarted() { }
    /// <summary>No-op: the LSP server does not track project-template-wizard-completed telemetry.</summary>
    public void MonitorProjectTemplateWizardCompleted(string dotNetFramework, string unitTestFramework, bool addFluentAssertions) { }
    /// <summary>No-op: the LSP server does not track upgrade-dialog-dismissed telemetry.</summary>
    public void MonitorUpgradeDialogDismissed(Dictionary<string, object> additionalProps) { }
    /// <summary>No-op: the LSP server does not track welcome-dialog-dismissed telemetry.</summary>
    public void MonitorWelcomeDialogDismissed(Dictionary<string, object> additionalProps) { }
    /// <summary>No-op: the LSP server does not track link-click telemetry.</summary>
    public void MonitorLinkClicked(string source, string url, Dictionary<string, object> additionalProps = null) { }
    /// <summary>No-op: the LSP server does not transmit ad-hoc telemetry events through this channel.</summary>
    public void TransmitEvent(ITelemetryEvent runtimeEvent) { }
}
