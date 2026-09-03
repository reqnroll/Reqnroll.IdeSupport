using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.VsIntegration;

public class VsWizardTelemetryTests
{
    private readonly ITelemetryService _telemetryService = Substitute.For<ITelemetryService>();

    private VsWizardTelemetry CreateSut() => new(_telemetryService);

    [Fact]
    public void OnFeatureFileAdded_maps_a_reqnroll_project_to_ReqnrollTestProject_kind()
    {
        var settings = new WizardProjectSettings
        {
            IsReqnrollProject = true,
            ReqnrollVersionLabel = "2.14.0",
            HasXUnitAdapter = true
        };

        CreateSut().OnFeatureFileAdded(settings);

        _telemetryService.Received(1).MonitorCommandAddFeatureFile(Arg.Is<ProjectSettings>(ps =>
            ps.Kind == IdeSupportProjectKind.ReqnrollTestProject &&
            ps.ReqnrollVersion == new NuGetVersion("2.14.0", null) &&
            ps.ReqnrollProjectTraits == ReqnrollProjectTraits.XUnitAdapter));
    }

    [Fact]
    public void OnFeatureFileAdded_maps_a_specflow_project_to_ReqnrollTestProject_kind()
    {
        // Kind is driven by IsReqnrollProject || IsSpecFlowProject — a legacy SpecFlow
        // project (which is not itself "a Reqnroll project") must still count.
        var settings = new WizardProjectSettings
        {
            IsReqnrollProject = false,
            IsSpecFlowProject = true,
            ReqnrollVersionLabel = "3.9.74"
        };

        CreateSut().OnFeatureFileAdded(settings);

        _telemetryService.Received(1).MonitorCommandAddFeatureFile(
            Arg.Is<ProjectSettings>(ps => ps.Kind == IdeSupportProjectKind.ReqnrollTestProject));
    }

    [Fact]
    public void OnFeatureFileAdded_maps_a_non_reqnroll_project_to_Unknown_kind_with_default_version_and_no_traits()
    {
        var settings = new WizardProjectSettings
        {
            IsReqnrollProject = false,
            IsSpecFlowProject = false,
            ReqnrollVersionLabel = null,
            HasXUnitAdapter = false
        };

        CreateSut().OnFeatureFileAdded(settings);

        _telemetryService.Received(1).MonitorCommandAddFeatureFile(Arg.Is<ProjectSettings>(ps =>
            ps.Kind == IdeSupportProjectKind.Unknown &&
            ps.ReqnrollVersion == new NuGetVersion("0.0.0", null) &&
            ps.ReqnrollProjectTraits == ReqnrollProjectTraits.None));
    }

    [Fact]
    public void OnConfigFileAdded_maps_settings_the_same_way_as_OnFeatureFileAdded()
    {
        var settings = new WizardProjectSettings
        {
            IsReqnrollProject = true,
            ReqnrollVersionLabel = "2.14.0",
            HasXUnitAdapter = true
        };

        CreateSut().OnConfigFileAdded(settings);

        _telemetryService.Received(1).MonitorCommandAddReqnrollConfigFile(Arg.Is<ProjectSettings>(ps =>
            ps.Kind == IdeSupportProjectKind.ReqnrollTestProject &&
            ps.ReqnrollVersion == new NuGetVersion("2.14.0", null) &&
            ps.ReqnrollProjectTraits == ReqnrollProjectTraits.XUnitAdapter));
    }

    [Fact]
    public void OnProjectTemplateWizardStarted_delegates_to_the_telemetry_service()
    {
        CreateSut().OnProjectTemplateWizardStarted();

        _telemetryService.Received(1).MonitorProjectTemplateWizardStarted();
    }

    [Fact]
    public void OnProjectTemplateWizardCompleted_delegates_with_addFluentAssertions_hardcoded_to_false()
    {
        CreateSut().OnProjectTemplateWizardCompleted("net8.0", "xUnit");

        _telemetryService.Received(1).MonitorProjectTemplateWizardCompleted("net8.0", "xUnit", false);
    }

    [Fact]
    public void MonitorError_delegates_to_the_telemetry_service()
    {
        var exception = new InvalidOperationException("boom");

        CreateSut().MonitorError(exception, isFatal: true);

        _telemetryService.Received(1).MonitorError(exception, true);
    }
}
