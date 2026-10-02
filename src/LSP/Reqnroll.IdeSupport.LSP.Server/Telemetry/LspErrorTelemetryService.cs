#nullable disable
using System;
using System.Diagnostics;
using System.Collections.Generic;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// LSP-server-side <see cref="ITelemetryService"/>. Every VS/host-lifecycle member (project
/// wizards, dialogs, project-system open) is a no-op here, same as <see cref="NullLspTelemetryService"/>
/// — those only make sense from a host UI, which the server doesn't have.
/// <see cref="IErrorTelemetryService.MonitorError"/> is the one exception: it forwards to
/// <see cref="ILspTelemetryService"/> as an <c>UnhandledException</c> <c>telemetry/event</c>, so exceptions raised
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
    /// <summary>Most distinct stacks per server session that get <c>StackFrames</c> attached (volume bound, issue #620).</summary>
    internal const int DefaultMaxDistinctStacksPerSession = 25;

    private readonly ILspTelemetryService _lspTelemetryService;
    private readonly int _maxDistinctStacks;
    private readonly object _stacksGate = new();
    private readonly HashSet<string> _stacksSent = new();

    /// <summary>Initializes a new instance of the <see cref="LspErrorTelemetryService"/> class.</summary>
    public LspErrorTelemetryService(ILspTelemetryService lspTelemetryService)
        : this(lspTelemetryService, DefaultMaxDistinctStacksPerSession)
    {
    }

    internal LspErrorTelemetryService(ILspTelemetryService lspTelemetryService, int maxDistinctStacks)
    {
        _lspTelemetryService = lspTelemetryService;
        _maxDistinctStacks = maxDistinctStacks;
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
    /// Sends the exception to the client as an <c>UnhandledException</c> <c>telemetry/event</c>. The message is passed
    /// through raw: <see cref="LspTelemetryService"/> (the last hop before the client) redacts paths via
    /// <see cref="TelemetryScrubber.ScrubProperties"/>, so local logs and the debug-log mirror keep it.
    /// </summary>
    public void MonitorError(Exception exception, bool? isFatal = null)
    {
        var properties = new Dictionary<string, object>
        {
            ["ExceptionType"] = exception.GetType().FullName,
            ["Message"] = exception.Message,
        };
        if (isFatal.HasValue)
            properties["IsFatal"] = isFatal.Value;
        // One stack walk feeds both Source and StackFrames.
        var captured = ExceptionStackSanitizer.Capture(exception);
        if (captured?.Source is { } source)
            properties[TelemetryProperties.Source] = source;
        if (ResolveStackFramesOnce(exception, captured) is { } stackFrames)
            properties[TelemetryProperties.StackFrames] = stackFrames;

        _lspTelemetryService.SendEvent(TelemetryEvents.UnhandledException, properties);
    }

    /// <summary>
    /// The sanitized stack (<see cref="ExceptionStackSanitizer"/>) the first time this exception type/stack is
    /// seen in this session, up to a per-session cap on distinct stacks; <see langword="null"/> otherwise, so a
    /// hot failure loop cannot multiply payload size (the event itself is still sent and counted).
    /// </summary>
    private string ResolveStackFramesOnce(Exception exception, ExceptionStackSanitizer.CapturedStack captured)
    {
        if (captured is null)
            return null;

        // Cheap pre-check so a saturated session skips the formatting work entirely.
        lock (_stacksGate)
        {
            if (_stacksSent.Count >= _maxDistinctStacks)
                return null;
        }

        var frames = captured.Sanitize();
        if (frames is null)
            return null;

        // Re-check under the lock: the cap is exact even when callers race.
        lock (_stacksGate)
        {
            if (_stacksSent.Count >= _maxDistinctStacks)
                return null;
            return _stacksSent.Add(exception.GetType().FullName + "|" + frames) ? frames : null;
        }
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

    /// <summary>
    /// The simple name of the class (no namespace, no stack trace) that contains the topmost frame of
    /// <paramref name="exception"/>'s stack in a <c>Reqnroll.IdeSupport</c> assembly, so an error can be
    /// attributed to a component without transmitting the stack (issue #849, #620). Compiler-generated
    /// async/lambda types are folded into the class that declares them. <see langword="null"/> when the
    /// exception was never thrown or no frame belongs to this product.
    /// </summary>
    internal static string ResolveSource(Exception exception)
    {
        // Shares ExceptionStackSanitizer's product test (namespace AND assembly, exact-or-dotted); never throws.
        return ExceptionStackSanitizer.Capture(exception)?.Source;
    }
}
