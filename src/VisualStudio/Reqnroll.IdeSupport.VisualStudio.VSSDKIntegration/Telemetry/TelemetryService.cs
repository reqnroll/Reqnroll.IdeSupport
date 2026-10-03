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

    /// <summary>Transmits the "Extension loaded" event.</summary>
    public void MonitorOpenProjectSystem(IIdeScope ideScope)
    {
        //_welcomeService.OnIdeScopeActivityStarted(ideScope, this);

        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.ExtensionLoaded));
    }

    /// <summary>Transmits the "Feature file opened" event with project settings.</summary>
    public void MonitorOpenFeatureFile(ProjectSettings projectSettings)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.FeatureFileOpened,
            GetProjectSettingsProps(projectSettings)));
    }

    // EXTENSION

    /// <summary>Transmits the "Extension installed" event.</summary>
    public void MonitorExtensionInstalled()
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.ExtensionInstalled));
    }

    /// <summary>Transmits the "Extension upgraded" event with the previous version.</summary>
    public void MonitorExtensionUpgraded(string oldExtensionVersion)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.ExtensionUpgraded,
            new Dictionary<string, object>
            {
                {"OldExtensionVersion", oldExtensionVersion}
            }));
    }

    /// <summary>Transmits a "{usageDays} day usage" event.</summary>
    public void MonitorExtensionDaysOfUsage(int usageDays)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(string.Format(TelemetryEvents.DaysOfUsageEventNameFormat, usageDays)));
    }


    //COMMAND

    /// <summary>Transmits the "Feature file added" event with project settings.</summary>
    public void MonitorCommandAddFeatureFile(ProjectSettings settings)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.FeatureFileAdded,
            GetProjectSettingsProps(settings)));
    }

    /// <summary>Transmits the "Reqnroll config added" event with project settings.</summary>
    public void MonitorCommandAddReqnrollConfigFile(ProjectSettings settings)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.ReqnrollConfigAdded,
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

    /// <summary>Transmits the "Project Template Wizard Started" event.</summary>
    public void MonitorProjectTemplateWizardStarted()
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.ProjectTemplateWizardStarted));
    }

    /// <summary>Transmits the "Project Template Wizard Completed" event with the selected wizard options.</summary>
    public void MonitorProjectTemplateWizardCompleted(string dotNetFramework, string unitTestFramework,
        bool addFluentAssertions)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.ProjectTemplateWizardCompleted,
            new Dictionary<string, object>
            {
                {"SelectedDotNetFramework", dotNetFramework},
                {"SelectedUnitTestFramework", unitTestFramework},
                {"AddFluentAssertions", addFluentAssertions}
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

    /// <summary>Transmits the "Link clicked" event with the link source and URL.</summary>
    public void MonitorLinkClicked(string source, string url, Dictionary<string, object> additionalProps = null)
    {
        additionalProps ??= new Dictionary<string, object>();
        additionalProps.Add("Source", source);
        additionalProps.Add("URL", url);
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.LinkClicked,
            additionalProps));
    }

    /// <summary>Transmits the "Upgrade dialog dismissed" event.</summary>
    public void MonitorUpgradeDialogDismissed(Dictionary<string, object> additionalProps)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.UpgradeDialogDismissed,
            additionalProps));
    }

    /// <summary>Transmits the "Welcome dialog dismissed" event.</summary>
    public void MonitorWelcomeDialogDismissed(Dictionary<string, object> additionalProps)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent(TelemetryEvents.WelcomeDialogDismissed,
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
