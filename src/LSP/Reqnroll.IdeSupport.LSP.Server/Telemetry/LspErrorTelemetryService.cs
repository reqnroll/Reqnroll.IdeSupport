#nullable disable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// LSP-server-side <see cref="ITelemetryService"/>. Every VS/host-lifecycle member (project
/// wizards, dialogs, project-system open) is a no-op here, same as <see cref="NullLspTelemetryService"/>
/// — those only make sense from a host UI, which the server doesn't have.
/// <see cref="IErrorTelemetryService.MonitorError"/> is the one exception: it forwards to
/// <see cref="ILspTelemetryService"/> as an "Error" <c>telemetry/event</c>, so exceptions raised
/// inside LSP.Core (e.g. <c>IdeSupportGherkinParser</c>/<c>IdeSupportTagParser</c> via
/// <c>IdeSupportLoggerExtensions.LogException</c>) actually reach telemetry instead of being
/// silently dropped. Previously the server was wired with <see cref="NullLspTelemetryService"/> for
/// every <see cref="ITelemetryService"/> consumer, including these (issue #255).
/// <para>
/// This class still implements the full <see cref="ITelemetryService"/> (not just
/// <see cref="IErrorTelemetryService"/>) purely because <see cref="Workspace.LspIdeScope.TelemetryService"/>
/// is typed as <see cref="ITelemetryService"/> — that property's type is shared with VS's
/// <c>IIdeScope.TelemetryService</c>, which genuinely needs the full interface for wizard/dialog
/// telemetry, so it can't be narrowed without touching VS-side code that has nothing to do with the
/// LSP server. <c>LSP.Core</c>'s own classes (<c>IdeSupportGherkinParser</c>, <c>IdeSupportTagParser</c>,
/// <c>CompletionContextResolver</c>) depend on the narrow <see cref="IErrorTelemetryService"/>
/// directly instead — DI resolves both interfaces to this same singleton (issue #255/#259).
/// </para>
/// </summary>
public sealed class LspErrorTelemetryService : ITelemetryService
{
    // Windows absolute/UNC paths (C:\..., \\server\share\...) and POSIX absolute paths (/home/...).
    // Deliberately broad (over-redacting is safe; under-redacting leaks a path) — see
    // docs/LSP-IDE-Support-Architecture.md's Privacy Considerations: "The Error event must scrub
    // exception messages for file paths and user-identifiable strings before transmission."
    private static readonly Regex PathPattern = new(
        @"(?:[A-Za-z]:\\|\\\\|/)[^\s""'<>:*?|]+",
        RegexOptions.Compiled);

    private readonly ILspTelemetryService _lspTelemetryService;

    /// <summary>Initializes a new instance of the <see cref="LspErrorTelemetryService"/> class.</summary>
    public LspErrorTelemetryService(ILspTelemetryService lspTelemetryService)
    {
        _lspTelemetryService = lspTelemetryService;
    }

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

    /// <summary>
    /// Sends the exception to the client as an "Error" <c>telemetry/event</c>, with the exception
    /// message redacted via <see cref="RedactPaths"/> first.
    /// </summary>
    public void MonitorError(Exception exception, bool? isFatal = null)
    {
        var properties = new Dictionary<string, object>
        {
            ["ExceptionType"] = exception.GetType().FullName,
            ["Message"] = RedactPaths(exception.Message),
        };
        if (isFatal.HasValue)
            properties["IsFatal"] = isFatal.Value;

        _lspTelemetryService.SendEvent(TelemetryEvents.UnhandledException, properties);
    }

    /// <summary>No-op: the LSP server does not track project-template-wizard-started telemetry.</summary>
    public void MonitorProjectTemplateWizardStarted() { }
    /// <summary>No-op: the LSP server does not track project-template-wizard-completed telemetry.</summary>
    public void MonitorProjectTemplateWizardCompleted(string dotNetFramework, string unitTestFramework, bool addFluentAssertions) { }
    /// <summary>No-op: the LSP server does not track link-click telemetry.</summary>
    public void MonitorLinkClicked(string source, string url, Dictionary<string, object> additionalProps = null) { }
    /// <summary>No-op: the LSP server does not track upgrade-dialog-dismissed telemetry.</summary>
    public void MonitorUpgradeDialogDismissed(Dictionary<string, object> additionalProps) { }
    /// <summary>No-op: the LSP server does not track welcome-dialog-dismissed telemetry.</summary>
    public void MonitorWelcomeDialogDismissed(Dictionary<string, object> additionalProps) { }
    /// <summary>No-op: the LSP server does not transmit ad-hoc telemetry events through this channel.</summary>
    public void TransmitEvent(ITelemetryEvent runtimeEvent) { }

    /// <summary>Replaces filesystem-path-shaped substrings with <c>&lt;path&gt;</c>.</summary>
    internal static string RedactPaths(string message) =>
        string.IsNullOrEmpty(message) ? message : PathPattern.Replace(message, "<path>");
}
