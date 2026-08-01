namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.Core;

public class ReqnrollProjectTemplateWizardTests
{
    private readonly IWizardDialogService _dialogService = Substitute.For<IWizardDialogService>();
    private readonly IWizardTelemetry _telemetry = Substitute.For<IWizardTelemetry>();
    private readonly IWizardContext _context = Substitute.For<IWizardContext>();

    private ReqnrollProjectTemplateWizard CreateSut() =>
        new(_dialogService, _telemetry);

    private void SetupContext(string projectName = "MyProject")
    {
        var replacements = new Dictionary<string, string> { ["$projectname$"] = projectName };
        _context.ReplacementsDictionary.Returns(replacements);
    }

    [Fact]
    public void RunStarted_fires_OnProjectTemplateWizardStarted_telemetry()
    {
        SetupContext();
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult("net8.0", "MSTest"));

        CreateSut().RunStarted(_context);

        _telemetry.Received(1).OnProjectTemplateWizardStarted();
    }

    [Fact]
    public void RunStarted_returns_false_when_dialog_is_cancelled()
    {
        SetupContext();
        _dialogService.ShowAddNewProjectDialog().Returns((AddNewProjectWizardResult?)null);

        var result = CreateSut().RunStarted(_context);

        result.Should().BeFalse();
    }

    [Fact]
    public void RunStarted_returns_true_when_dialog_is_completed()
    {
        SetupContext();
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult("net8.0", "xUnit"));

        var result = CreateSut().RunStarted(_context);

        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("net8.0", "MSTest")]
    [InlineData("net481", "NUnit")]
    [InlineData("net10.0", "xUnit")]
    public void RunStarted_populates_required_replacement_keys(string framework, string testFramework)
    {
        SetupContext();
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult(framework, testFramework));
        var replacements = _context.ReplacementsDictionary;

        CreateSut().RunStarted(_context);

        replacements.Should().ContainKey("$dotnetframework$");
        replacements.Should().ContainKey("$unittestframework$");
        replacements.Should().ContainKey("$rootnamespace$");
        replacements["$dotnetframework$"].Should().Be(framework);
        replacements["$unittestframework$"].Should().Be(testFramework);
    }

    [Fact]
    public void RunStarted_sets_IsNetFramework_key_for_net48_target()
    {
        SetupContext();
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult("net481", "MSTest"));

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary[WizardContextKeys.IsNetFrameworkKey]
            .Should().Be("True");
    }

    [Fact]
    public void RunStarted_sets_IsNetFramework_to_false_for_net8_target()
    {
        SetupContext();
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult("net8.0", "MSTest"));

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary[WizardContextKeys.IsNetFrameworkKey]
            .Should().Be("False");
    }

    [Fact]
    public void RunStarted_adds_globalUsings_for_xUnit_net8_target()
    {
        SetupContext();
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult("net8.0", "xUnit"));

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary.Should().ContainKey("$globalUsings$");
        _context.ReplacementsDictionary["$globalUsings$"].Should().Contain("Xunit");
    }

    [Fact]
    public void RunStarted_cleans_project_name_dots_to_valid_identifier_for_rootnamespace()
    {
        // "My.Project" → each segment is already a valid identifier, rootNamespace = ""
        SetupContext(projectName: "My.Project");
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult("net8.0", "MSTest"));

        CreateSut().RunStarted(_context);

        // When proposed == cleaned (all segments valid), rootNamespace should be empty
        _context.ReplacementsDictionary["$rootnamespace$"].Should().Be(string.Empty);
    }

    [Fact]
    public void RunStarted_does_not_add_globalUsings_for_net481_target()
    {
        SetupContext();
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult("net481", "MSTest"));

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary.Should().NotContainKey("$globalUsings$");
    }

    [Fact]
    public void RunStarted_cleans_a_leading_digit_project_name_to_a_valid_identifier_for_rootnamespace()
    {
        // "1Project" is not a valid identifier (leading digit) → cleaned to "_1Project"
        SetupContext(projectName: "1Project");
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult("net8.0", "MSTest"));

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary["$rootnamespace$"].Should().Be("_1Project");
    }

    [Fact]
    public void RunStarted_adds_globalUsings_for_NUnit_net8_target()
    {
        SetupContext();
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult("net8.0", "NUnit"));

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary.Should().ContainKey("$globalUsings$");
        _context.ReplacementsDictionary["$globalUsings$"].Should().Contain("NUnit.Framework");
    }

    [Fact]
    public void RunStarted_adds_globalUsings_for_xUnit_v3_net8_target()
    {
        SetupContext();
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult("net8.0", "xUnit.v3"));

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary.Should().ContainKey("$globalUsings$");
        _context.ReplacementsDictionary["$globalUsings$"].Should().Contain("Xunit");
    }

    [Fact]
    public void RunStarted_adds_an_empty_globalUsings_for_TUnit_net8_target()
    {
        SetupContext();
        _dialogService.ShowAddNewProjectDialog()
            .Returns(new AddNewProjectWizardResult("net8.0", "TUnit"));

        CreateSut().RunStarted(_context);

        _context.ReplacementsDictionary.Should().ContainKey("$globalUsings$");
        _context.ReplacementsDictionary["$globalUsings$"].Should().BeEmpty();
    }
}
