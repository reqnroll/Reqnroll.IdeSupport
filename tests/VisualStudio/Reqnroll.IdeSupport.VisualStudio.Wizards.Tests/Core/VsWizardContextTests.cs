using System.Collections.Concurrent;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.Core;

public class VsWizardContextTests
{
    [Fact]
    public void Constructor_preserves_replacements_dictionary_instance()
    {
        var ideScope = CreateIdeScope(out _);
        var projectScope = CreateProjectScopeWithSettings(CreateProjectSettings(ReqnrollProjectTraits.None));
        var dict = new Dictionary<string, string> { ["$projectname$"] = "My.Project" };

        var sut = new VsWizardContext(
            isAddNewItem: false,
            projectScope,
            ideScope,
            templateFolder: "template",
            targetFolder: "target",
            targetFileName: "file.feature",
            replacementsDictionary: dict,
            dialogService: Substitute.For<IWizardDialogService>(),
            telemetry: Substitute.For<IWizardTelemetry>());

        sut.ReplacementsDictionary.Should().BeSameAs(dict);
    }

    [Fact]
    public void Constructor_maps_ProjectSettings_to_WizardProjectSettings()
    {
        var traits = ReqnrollProjectTraits.LegacySpecFlow |
                     ReqnrollProjectTraits.DesignTimeFeatureFileGeneration |
                     ReqnrollProjectTraits.XUnitAdapter;
        var sourceSettings = CreateProjectSettings(traits);

        var ideScope = CreateIdeScope(out _);
        var projectScope = CreateProjectScopeWithSettings(sourceSettings);

        var sut = new VsWizardContext(
            isAddNewItem: true,
            projectScope,
            ideScope,
            templateFolder: "template",
            targetFolder: "target",
            targetFileName: "out.feature",
            replacementsDictionary: new Dictionary<string, string>(),
            dialogService: Substitute.For<IWizardDialogService>(),
            telemetry: Substitute.For<IWizardTelemetry>());

        sut.ProjectSettings.IsReqnrollProject.Should().Be(sourceSettings.IsReqnrollProject);
        sut.ProjectSettings.IsSpecFlowProject.Should().Be(sourceSettings.IsSpecFlowProject);
        sut.ProjectSettings.DesignTimeFeatureFileGenerationEnabled.Should().Be(sourceSettings.DesignTimeFeatureFileGenerationEnabled);
        sut.ProjectSettings.HasDesignTimeGenerationReplacement.Should().Be(sourceSettings.HasDesignTimeGenerationReplacement);
        sut.ProjectSettings.HasXUnitAdapter.Should().BeTrue();
        sut.ProjectSettings.ReqnrollVersionLabel.Should().Be(sourceSettings.GetReqnrollVersionLabel());
        sut.ProjectSettings.ShortLabel.Should().Be(sourceSettings.GetShortLabel());
    }

    [Fact]
    public void Constructor_uses_Uninitialized_settings_when_projectScope_is_null()
    {
        var ideScope = CreateIdeScope(out _);

        var sut = new VsWizardContext(
            isAddNewItem: false,
            projectScope: null,
            ideScope,
            templateFolder: "template",
            targetFolder: "target",
            targetFileName: "out.feature",
            replacementsDictionary: new Dictionary<string, string>(),
            dialogService: Substitute.For<IWizardDialogService>(),
            telemetry: Substitute.For<IWizardTelemetry>());

        sut.ProjectSettings.Should().BeSameAs(WizardProjectSettings.Uninitialized);
    }

    [Fact]
    public void ShowProblem_delegates_to_IdeScope_Actions()
    {
        var ideScope = CreateIdeScope(out var actions);
        var projectScope = CreateProjectScopeWithSettings(CreateProjectSettings(ReqnrollProjectTraits.None));

        var sut = new VsWizardContext(
            isAddNewItem: false,
            projectScope,
            ideScope,
            templateFolder: "template",
            targetFolder: "target",
            targetFileName: "out.feature",
            replacementsDictionary: new Dictionary<string, string>(),
            dialogService: Substitute.For<IWizardDialogService>(),
            telemetry: Substitute.For<IWizardTelemetry>());

        sut.ShowProblem("boom");

        actions.Received(1).ShowProblem("boom");
    }

    private static ProjectSettings CreateProjectSettings(ReqnrollProjectTraits traits)
        => new(
            Kind: IdeSupportProjectKind.ReqnrollTestProject,
            TargetFrameworkMoniker: TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0")!,
            TargetFrameworkMonikers: "net8.0",
            PlatformTarget: ProjectPlatformTarget.x64,
            OutputAssemblyPath: @"c:\temp\out.dll",
            DefaultNamespace: "Test.Namespace",
            ReqnrollVersion: new NuGetVersion("2.3.2", "2.3.2"),
            ReqnrollGeneratorFolder: @"c:\temp\gen",
            ReqnrollConfigFilePath: @"c:\temp\reqnroll.json",
            ReqnrollProjectTraits: traits,
            ProgrammingLanguage: ProjectProgrammingLanguage.CSharp);

    private static IProjectScope CreateProjectScopeWithSettings(ProjectSettings settings)
    {
        var scope = Substitute.For<IProjectScope>();
        scope.Properties.Returns(new ConcurrentDictionary<Type, object>());

        var provider = Substitute.For<IProjectSettingsProvider>();
        provider.GetProjectSettings().Returns(settings);
        scope.Properties[typeof(IProjectSettingsProvider)] = provider;

        return scope;
    }

    private static IIdeScope CreateIdeScope(out IIdeActions actions)
    {
        var ideScope = Substitute.For<IIdeScope>();
        actions = Substitute.For<IIdeActions>();
        ideScope.Actions.Returns(actions);
        return ideScope;
    }
}
