using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeActions;

/// <summary>
/// Direct unit tests for <see cref="StepDefinitionTargetResolver"/> (issue #588), extracted from
/// <see cref="CodeActionHandler.Handle"/> so "where should generated code go" is testable
/// without going through a full code-action request.
/// </summary>
public class StepDefinitionTargetResolverTests : IDisposable
{
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();
    private readonly IFileSystemForIDE _fileSystem = new FileSystemForIDE();
    private readonly IIdeSupportLogger _ideLogger = Substitute.For<IIdeSupportLogger>();
    private readonly LspIdeScope _ideScope;
    private readonly string _projectFolder;

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/CalculatorSteps.feature");

    public StepDefinitionTargetResolverTests()
    {
        _ideScope = new LspIdeScope(_ideLogger);
        _projectFolder = Path.Combine(Path.GetTempPath(), "SDTRTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectFolder);

        _scopeManager.GetConfigurationProviderForUri(Arg.Any<DocumentUri>()).Returns(_configProvider);
        _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_projectFolder)) Directory.Delete(_projectFolder, recursive: true); } catch { /* best-effort */ }
    }

    private StepDefinitionTargetResolver CreateSut() => new(_scopeManager, _fileSystem);

    private string FeaturePath(string name = "CalculatorSteps.feature") => Path.Combine(_projectFolder, name);

    [Fact]
    public void Resolve_with_no_candidates_falls_back_to_a_sibling_StepDefinitions_folder_if_it_exists()
    {
        var stepDefsFolder = Path.Combine(_projectFolder, "StepDefinitions");
        Directory.CreateDirectory(stepDefsFolder);

        var target = CreateSut().Resolve(FeatureUri, FeaturePath(), primaryOwner: null, matchSet: null);

        Path.GetDirectoryName(target.TargetPath).Should().Be(stepDefsFolder);
    }

    [Fact]
    public void Resolve_with_no_candidates_and_no_sibling_folder_falls_back_to_the_feature_directory()
    {
        // No StepDefinitions/ folder created alongside the feature file.
        var target = CreateSut().Resolve(FeatureUri, FeaturePath(), primaryOwner: null, matchSet: null);

        Path.GetDirectoryName(target.TargetPath).Should().Be(_projectFolder);
    }

    [Fact]
    public void Resolve_derives_the_class_name_from_the_feature_file_name()
    {
        var target = CreateSut().Resolve(FeatureUri, FeaturePath(), primaryOwner: null, matchSet: null);

        target.ClassName.Should().Be("CalculatorStepsStepDefinitions");
        Path.GetFileName(target.TargetPath).Should().Be("CalculatorStepsStepDefinitions.cs");
    }

    [Fact]
    public void Resolve_appends_a_numeric_suffix_when_the_target_file_already_exists()
    {
        File.WriteAllText(Path.Combine(_projectFolder, "CalculatorStepsStepDefinitions.cs"), "// existing");

        var target = CreateSut().Resolve(FeatureUri, FeaturePath(), primaryOwner: null, matchSet: null);

        Path.GetFileName(target.TargetPath).Should().Be("CalculatorStepsStepDefinitions2.cs");
        target.ClassName.Should().Be("CalculatorStepsStepDefinitions2");
    }

    [Fact]
    public void Resolve_keeps_incrementing_the_suffix_past_the_first_collision()
    {
        File.WriteAllText(Path.Combine(_projectFolder, "CalculatorStepsStepDefinitions.cs"), "// existing");
        File.WriteAllText(Path.Combine(_projectFolder, "CalculatorStepsStepDefinitions2.cs"), "// also existing");

        var target = CreateSut().Resolve(FeatureUri, FeaturePath(), primaryOwner: null, matchSet: null);

        Path.GetFileName(target.TargetPath).Should().Be("CalculatorStepsStepDefinitions3.cs");
    }

    [Fact]
    public void Resolve_uses_the_default_snippet_style_when_the_project_has_no_configuration_override()
    {
        var target = CreateSut().Resolve(FeatureUri, FeaturePath(), primaryOwner: null, matchSet: null);

        target.Style.Should().Be(SnippetExpressionStyle.CucumberExpression);
    }

    [Fact]
    public void Resolve_honours_a_project_level_snippet_style_override()
    {
        _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration
        {
            SnippetExpressionStyle = SnippetExpressionStyle.RegularExpression
        });

        var target = CreateSut().Resolve(FeatureUri, FeaturePath(), primaryOwner: null, matchSet: null);

        target.Style.Should().Be(SnippetExpressionStyle.RegularExpression);
    }

    [Fact]
    public void Resolve_has_no_append_candidates_when_there_is_no_match_set()
    {
        var target = CreateSut().Resolve(FeatureUri, FeaturePath(), primaryOwner: null, matchSet: null);

        target.AppendCandidates.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_prefers_the_folder_with_the_most_existing_binding_files_for_a_new_file()
    {
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _projectFolder);
        var busyFolder = Path.Combine(_projectFolder, "Busy");
        var quietFolder = Path.Combine(_projectFolder, "Quiet");
        Directory.CreateDirectory(busyFolder);
        Directory.CreateDirectory(quietFolder);

        _scopeManager.GetBindingFilePathsForProject(project).Returns(new[]
        {
            Path.Combine(busyFolder, "A.cs"),
            Path.Combine(busyFolder, "B.cs"),
            Path.Combine(quietFolder, "C.cs"),
        });

        var target = CreateSut().Resolve(FeatureUri, FeaturePath(), primaryOwner: project, matchSet: null);

        Path.GetDirectoryName(target.TargetPath).Should().Be(busyFolder);
        project.Dispose();
    }
}
