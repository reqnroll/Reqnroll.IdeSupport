#nullable disable
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
using System.Collections.Immutable;
using System.ComponentModel.Composition;

namespace Reqnroll.IdeSupport.VisualStudio.Telemetry;

/// <summary>
/// Visual Studio's MEF-exported <see cref="ITelemetryService"/> implementation: raises the
/// well-known Reqnroll telemetry events by building a <see cref="VsGenericEvent"/> with the
/// relevant project-settings properties and handing it to the <see cref="ITelemetryTransmitter"/>.
/// </summary>
[Export(typeof(ITelemetryService))]
public class TelemetryService : ITelemetryService
{
    private readonly ITelemetryTransmitter _telemetryTransmitter;
    //private readonly IWelcomeService _welcomeService;

    /// <summary>MEF importing constructor.</summary>
    [ImportingConstructor]
    public TelemetryService(ITelemetryTransmitter telemetryTransmitter)
    {
        _telemetryTransmitter = telemetryTransmitter;
    }

    // OPEN

    /// <summary>Transmits the "Lifecycle.Extension.Loaded" event.</summary>
    public void MonitorOpenProjectSystem(IIdeScope ideScope)
    {
        //_welcomeService.OnIdeScopeActivityStarted(ideScope, this);

        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.LifecycleExtensionLoaded));
    }

    /// <summary>Transmits the "Project.FeatureFile.Opened" event with project settings.</summary>
    public void MonitorOpenFeatureFile(ProjectSettings projectSettings)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.ProjectFeatureFileOpened,
            GetProjectSettingsProps(projectSettings)));
    }

    // EXTENSION

    /// <summary>Transmits the "Lifecycle.Extension.Installed" event.</summary>
    public void MonitorExtensionInstalled()
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.LifecycleExtensionInstalled));
    }

    /// <summary>Transmits the "Lifecycle.Extension.Upgraded" event with the previous version.</summary>
    public void MonitorExtensionUpgraded(string oldExtensionVersion)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.LifecycleExtensionUpgraded,
            new Dictionary<string, object>
            {
                {"OldExtensionVersion", oldExtensionVersion}
            }));
    }

    /// <summary>Transmits the "Lifecycle.Usage.Recorded" event with the running <c>UsageDays</c> count.</summary>
    public void MonitorExtensionDaysOfUsage(int usageDays)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.LifecycleUsageRecorded,
            new Dictionary<string, object>
            {
                {"UsageDays", usageDays}
            }));
    }


    //COMMAND

    /// <summary>Transmits the "Project.FeatureFile.Added" event with project settings.</summary>
    public void MonitorCommandAddFeatureFile(ProjectSettings settings)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.ProjectFeatureFileAdded,
            GetProjectSettingsProps(settings)));
    }

    /// <summary>Transmits the "Project.ReqnrollConfig.Added" event with project settings.</summary>
    public void MonitorCommandAddReqnrollConfigFile(ProjectSettings settings)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.ProjectReqnrollConfigAdded,
            GetProjectSettingsProps(settings)));
    }

    //ERROR

    /// <summary>Transmits an exception event, treating it as fatal iff <paramref name="isFatal"/> is <see langword="true"/>; falls back to normal-error classification when <paramref name="isFatal"/> is <see langword="null"/>.</summary>
    public void MonitorError(Exception exception, bool? isFatal = null)
    {
        if (isFatal.HasValue)
            _telemetryTransmitter.TransmitFatalExceptionEvent(exception, isFatal.Value);
        else
            _telemetryTransmitter.TransmitExceptionEvent(exception, ImmutableDictionary<string, object>.Empty);
    }


    // PROJECT TEMPLATE WIZARD

    /// <summary>Transmits the "Project.Creation.Started" event.</summary>
    public void MonitorProjectTemplateWizardStarted()
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.ProjectCreationStarted));
    }

    /// <summary>Transmits the "Project.Creation.Completed" event with the selected wizard options.</summary>
    public void MonitorProjectTemplateWizardCompleted(string dotNetFramework, string unitTestFramework)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.ProjectCreationCompleted,
            new Dictionary<string, object>
            {
                {"SelectedDotNetFramework", dotNetFramework},
                {"SelectedUnitTestFramework", unitTestFramework}
            }));
    }


    //public void MonitorNotificationShown(NotificationData notification)
    //{
    //    _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Notification shown",
    //        GetNotificationProps(notification)));
    //}

    //public void MonitorNotificationDismissed(NotificationData notification)
    //{
    //    _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Notification dismissed",
    //        GetNotificationProps(notification)));
    //}

    /// <summary>Transmits the "Lifecycle.Link.Clicked" event with the link source and URL.</summary>
    public void MonitorLinkClicked(string source, string url, Dictionary<string, object> additionalProps = null)
    {
        additionalProps ??= new Dictionary<string, object>();
        additionalProps.Add("Source", source);
        additionalProps.Add("URL", url);
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.LifecycleLinkClicked,
            additionalProps));
    }

    /// <summary>Transmits the "Lifecycle.UpgradeDialog.Dismissed" event.</summary>
    public void MonitorUpgradeDialogDismissed(Dictionary<string, object> additionalProps)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.LifecycleUpgradeDialogDismissed,
            additionalProps));
    }

    /// <summary>Transmits the "Lifecycle.WelcomeDialog.Dismissed" event.</summary>
    public void MonitorWelcomeDialogDismissed(Dictionary<string, object> additionalProps)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.LifecycleWelcomeDialogDismissed,
            additionalProps));
    }

    /// <summary>Passes a pre-built telemetry event straight through to the transmitter.</summary>
    public void TransmitEvent(ITelemetryEvent runtimeEvent)
        => _telemetryTransmitter.TransmitEvent(runtimeEvent);


    private ImmutableDictionary<string, object> GetProjectSettingsProps(ProjectSettings settings)
    {
        var props = GetProps(settings);
        return props.ToImmutable();
    }

    private static ImmutableDictionary<string, object>.Builder GetProps(ProjectSettings settings)
    {
        var props = ImmutableDictionary.CreateBuilder<string, object>();

        if (settings == null) return props;

        props.Add("ReqnrollVersion", settings.GetReqnrollVersionLabel());
        props.Add("ProjectTargetFramework", settings.TargetFrameworkMonikers);
        props.Add("SingleFileGeneratorUsed", settings.DesignTimeFeatureFileGenerationEnabled);
        props.Add("ProgrammingLanguage", settings.ProgrammingLanguage);
        if (settings.IsSpecFlowProject)
            props.Add("LegacySpecFlow", true);
        return props;
    }
}
