#nullable enable

using ApprovalTests;
using ApprovalTests.Namers;
using ApprovalTests.Reporters;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Xunit;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Scaffolding;

[UseReporter /*(typeof(VisualStudioReporter))*/]
[UseApprovalSubdirectory("../ApprovalTestData")]
public class StepDefinitionFileBuilderTests
{
    // The snippet is pre-indented at one level (matching what StepSkeletonRenderer.Render produces)
    private const string Snippet = """
            [When(@"I press add")]
            public void WhenIPressAdd()
            {
                throw new PendingStepException();
            }

        """;

    [Theory]
    [InlineData("block_scoped")]
    [InlineData("file_scoped")]
    public void GenerateStepDefinitionClass(string namespaceStyle)
    {
        NamerFactory.AdditionalInformation = namespaceStyle;

        var csharpConfig = new CSharpCodeGenerationConfiguration
        {
            NamespaceDeclarationStyle = namespaceStyle
        };

        var result = StepDefinitionFileBuilder.BuildNewFile(
            snippets:     new[] { Snippet },
            className:    "Feature1StepDefinitions",
            @namespace:   "MyNamespace.MyProject",
            csharpConfig: csharpConfig,
            indent:       "    ",
            newLine:      Environment.NewLine);

        Approvals.Verify(result);
    }

    // ── Async snippets need `using System.Threading.Tasks;` (issue #961) ─────
    // The async snippet mirrors what StepSkeletonRenderer.Render emits for an async style
    // (`public async Task ...`); StepDefinitionFileBuilder is what decides the file's using block.

    private const string AsyncSnippet = """
            [When(@"I press add")]
            public async Task WhenIPressAddAsync()
            {
                throw new PendingStepException();
            }

        """;

    [Theory]
    [InlineData("block_scoped")]
    [InlineData("file_scoped")]
    public void Async_snippets_get_the_System_Threading_Tasks_using(string namespaceStyle)
    {
        var csharpConfig = new CSharpCodeGenerationConfiguration
        {
            NamespaceDeclarationStyle = namespaceStyle
        };

        var result = StepDefinitionFileBuilder.BuildNewFile(
            snippets:     new[] { AsyncSnippet },
            className:    "Feature1StepDefinitions",
            @namespace:   "MyNamespace.MyProject",
            csharpConfig: csharpConfig,
            indent:       "    ",
            newLine:      "\r\n");

        result.Should().Contain("using System.Threading.Tasks;");
        // It must be a top-level directive above the namespace, not something that merely
        // appears later in the file.
        result.IndexOf("using System.Threading.Tasks;").Should().BeLessThan(result.IndexOf("namespace"));
        result.Should().Contain("public async Task WhenIPressAddAsync()");
    }

    [Theory]
    [InlineData("block_scoped")]
    [InlineData("file_scoped")]
    public void Sync_snippets_omit_the_System_Threading_Tasks_using(string namespaceStyle)
    {
        var csharpConfig = new CSharpCodeGenerationConfiguration
        {
            NamespaceDeclarationStyle = namespaceStyle
        };

        var result = StepDefinitionFileBuilder.BuildNewFile(
            snippets:     new[] { Snippet },
            className:    "Feature1StepDefinitions",
            @namespace:   "MyNamespace.MyProject",
            csharpConfig: csharpConfig,
            indent:       "    ",
            newLine:      "\r\n");

        result.Should().NotContain("using System.Threading.Tasks;");
    }

    [Fact]
    public void Mixed_snippets_get_the_using_when_any_is_async()
    {
        var csharpConfig = new CSharpCodeGenerationConfiguration
        {
            NamespaceDeclarationStyle = "file_scoped"
        };

        var result = StepDefinitionFileBuilder.BuildNewFile(
            snippets:     new[] { Snippet, AsyncSnippet },
            className:    "Feature1StepDefinitions",
            @namespace:   "MyNamespace.MyProject",
            csharpConfig: csharpConfig,
            indent:       "    ",
            newLine:      "\r\n");

        result.Should().Contain("using System.Threading.Tasks;");
    }

    // ── Naming helpers ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("addition.feature",        "AdditionStepDefinitions")]
    [InlineData("MyCalculator.feature",    "MyCalculatorStepDefinitions")]
    [InlineData("my-calculator.feature",   "MyCalculatorStepDefinitions")]
    [InlineData("feature with spaces.feature", "FeatureWithSpacesStepDefinitions")]
    public void ClassNameFromFeaturePath_derives_PascalCase_class(string fileName, string expected)
    {
        var className = StepDefinitionFileBuilder.ClassNameFromFeaturePath(
            Path.Combine("C:\\project\\features", fileName));
        className.Should().Be(expected);
    }

    [Fact]
    public void DeriveNamespace_appends_relative_folder_segments()
    {
        var ns = StepDefinitionFileBuilder.DeriveNamespace(
            projectFolder:    "C:\\project",
            defaultNamespace: "MyApp.Tests",
            targetFilePath:   "C:\\project\\Features\\AdditionStepDefinitions.cs");

        ns.Should().Be("MyApp.Tests.Features");
    }

    [Fact]
    public void DeriveNamespace_returns_default_when_file_is_at_project_root()
    {
        var ns = StepDefinitionFileBuilder.DeriveNamespace(
            projectFolder:    "C:\\project",
            defaultNamespace: "MyApp.Tests",
            targetFilePath:   "C:\\project\\AdditionStepDefinitions.cs");

        ns.Should().Be("MyApp.Tests");
    }
}
