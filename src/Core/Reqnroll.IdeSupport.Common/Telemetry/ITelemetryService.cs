using System.Collections.Generic;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// VS-host telemetry lifecycle: project wizards, welcome/upgrade dialogs, and other concerns the
/// LSP server has no equivalent of. Extends <see cref="IErrorTelemetryService"/> (the one member
/// genuinely shared with <c>LSP.Core</c>) rather than declaring <c>MonitorError</c> itself — see
/// that interface's doc comment for why the split exists.
/// </summary>
public interface ITelemetryService : IErrorTelemetryService
{
    /// <summary>Records that a project system was opened.</summary>
    void MonitorOpenProjectSystem(IIdeScope ideScope);
    /// <summary>Records that a project was opened, including its feature file count.</summary>
    void MonitorOpenProject(ProjectSettings settings, int? featureFileCount);
    /// <summary>Records that a feature file was opened.</summary>
    void MonitorOpenFeatureFile(ProjectSettings projectSettings);
    // MonitorParserParse(ProjectSettings, Dictionary<string, object>) — retired, not just unwired:
    // VS no longer parses .feature files locally (all parsing moved server-side, uniformly across
    // every IDE, into LSP.Core's IdeSupportGherkinParser). The modern equivalent of "parse
    // duration/file size" is the LSP server's perf-sampling telemetry (PerfSample events for
    // textDocument/didOpen and textDocument/didChange via IOperationDurationRecorder — see
    // TextDocumentSyncHandler), not a business event on this interface. Restoring this member would
    // just duplicate that, and re-introduce the same "continuous per-edit event" problem the
    // CommandAutoFormatTable exclusion (issue #255/#260) deliberately avoids elsewhere.

    /// <summary>Records that the extension was freshly installed.</summary>
    void MonitorExtensionInstalled();
    /// <summary>Records that the extension was upgraded from <paramref name="oldExtensionVersion"/>.</summary>
    void MonitorExtensionUpgraded(string oldExtensionVersion);
    /// <summary>Records the number of days the extension has been in use.</summary>
    void MonitorExtensionDaysOfUsage(int usageDays);

    //void MonitorCommandCommentUncomment();
    //void MonitorCommandDefineSteps(CreateStepDefinitionsDialogResult action, int snippetCount);
    //void MonitorCommandFindStepDefinitionUsages(int usagesCount, bool isCancelled);
    //void MonitorCommandFindUnusedStepDefinitions(int unusedStepDefinitions, int scannedFeatureFiles, bool isCancellationRequested);
    //void MonitorCommandGoToStepDefinition(bool generateSnippet);
    //void MonitorCommandGoToHook();
    //void MonitorCommandAutoFormatTable();
    //void MonitorCommandAutoFormatDocument(bool isSelectionFormatting);
    /// <summary>Records that a feature file was added via the command.</summary>
    void MonitorCommandAddFeatureFile(ProjectSettings projectSettings);
    /// <summary>Records that a Reqnroll config file was added via the command.</summary>
    void MonitorCommandAddReqnrollConfigFile(ProjectSettings projectSettings);
    //void MonitorCommandRenameStepExecuted(RenameStepCommandContext ctx);

    // MonitorReqnrollGeneration(bool isFailed, ProjectSettings) — not just unwired, the feature it
    // monitors (single-file code-behind generation for .feature files) has no implementation
    // anywhere in Reqnroll.IdeSupport to hook into; see Reqnroll.IdeSupport.LSP.Connector.Models.GenerationResult,
    // a data contract with zero call sites. Restoring this member wouldn't close a gap — it would
    // need the underlying generation feature built first. Left commented out until that exists.

    /// <summary>Records that the "add new Reqnroll project" wizard was started.</summary>
    void MonitorProjectTemplateWizardStarted();

    /// <summary>Records that the "add new Reqnroll project" wizard completed with the given choices.</summary>
    void MonitorProjectTemplateWizardCompleted(string dotNetFramework, string unitTestFramework,
        bool addFluentAssertions);

    //void MonitorNotificationShown(NotificationData notification);
    //void MonitorNotificationDismissed(NotificationData notification);
    /// <summary>Records that a link was clicked from the given source.</summary>
    void MonitorLinkClicked(string source, string url, Dictionary<string, object> additionalProps = null);

    /// <summary>Records that the upgrade dialog was dismissed.</summary>
    void MonitorUpgradeDialogDismissed(Dictionary<string, object> additionalProps);
    /// <summary>Records that the welcome dialog was dismissed.</summary>
    void MonitorWelcomeDialogDismissed(Dictionary<string, object> additionalProps);
    /// <summary>Transmits a fully-formed telemetry event.</summary>
    void TransmitEvent(ITelemetryEvent runtimeEvent);
}
