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

        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Extension loaded"));
    }

    /// <summary>Transmits the "Project loaded" event with project settings and feature-file count.</summary>
    public void MonitorOpenProject(ProjectSettings settings, int? featureFileCount)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Project loaded",
            GetProjectSettingsProps(settings,
                new Dictionary<string, object>
                {
                    {"FeatureFileCount", featureFileCount}
                }
            )));
    }

    /// <summary>Transmits the "Feature file opened" event with project settings.</summary>
    public void MonitorOpenFeatureFile(ProjectSettings projectSettings)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Feature file opened",
            GetProjectSettingsProps(projectSettings)));
    }

    /// <summary>Transmits the "Feature file parsed" event with project settings and additional properties.</summary>
    public void MonitorParserParse(ProjectSettings settings, Dictionary<string, object> additionalProps)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Feature file parsed",
            GetProjectSettingsProps(settings, additionalProps)));
    }


    // EXTENSION

    /// <summary>Transmits the "Extension installed" event.</summary>
    public void MonitorExtensionInstalled()
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Extension installed"));
    }

    /// <summary>Transmits the "Extension upgraded" event with the previous version.</summary>
    public void MonitorExtensionUpgraded(string oldExtensionVersion)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Extension upgraded",
            new Dictionary<string, object>
            {
                {"OldExtensionVersion", oldExtensionVersion}
            }));
    }

    /// <summary>Transmits a "{usageDays} day usage" event.</summary>
    public void MonitorExtensionDaysOfUsage(int usageDays)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent($"{usageDays} day usage"));
    }


    //COMMAND

    /// <summary>Transmits the "Feature file added" event with project settings.</summary>
    public void MonitorCommandAddFeatureFile(ProjectSettings settings)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Feature file added",
            GetProjectSettingsProps(settings)));
    }

    /// <summary>Transmits the "Reqnroll config added" event with project settings.</summary>
    public void MonitorCommandAddReqnrollConfigFile(ProjectSettings settings)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Reqnroll config added",
            GetProjectSettingsProps(settings)));
    }

    //REQNROLL

    /// <summary>Transmits the "Reqnroll Generation executed" event with the failure flag and project settings.</summary>
    public void MonitorReqnrollGeneration(bool isFailed, ProjectSettings projectSettings)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Reqnroll Generation executed",
            GetProjectSettingsProps(projectSettings,
                new Dictionary<string, object>
                {
                    {"IsFailed", isFailed}
                })));
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
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Project Template Wizard Started"));
    }

    /// <summary>Transmits the "Project Template Wizard Completed" event with the selected wizard options.</summary>
    public void MonitorProjectTemplateWizardCompleted(string dotNetFramework, string unitTestFramework,
        bool addFluentAssertions)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Project Template Wizard Completed",
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
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Link clicked",
            additionalProps));
    }

    /// <summary>Transmits the "Upgrade dialog dismissed" event.</summary>
    public void MonitorUpgradeDialogDismissed(Dictionary<string, object> additionalProps)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Upgrade dialog dismissed",
            additionalProps));
    }

    /// <summary>Transmits the "Welcome dialog dismissed" event.</summary>
    public void MonitorWelcomeDialogDismissed(Dictionary<string, object> additionalProps)
    {
        _telemetryTransmitter.TransmitEvent(new VsGenericEvent("Welcome dialog dismissed",
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

    private ImmutableDictionary<string, object> GetProjectSettingsProps(ProjectSettings settings,
        IEnumerable<KeyValuePair<string, object>> additionalSettings)
    {
        var props = GetProps(settings);
        props.AddRange(additionalSettings);
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
