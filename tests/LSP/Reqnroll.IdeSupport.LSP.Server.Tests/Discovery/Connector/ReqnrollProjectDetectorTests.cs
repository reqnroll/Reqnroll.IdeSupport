using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

/// <summary>
/// Covers the gate that keeps the out-of-process connector away from projects that cannot
/// contain bindings (issue #731).
/// </summary>
public class ReqnrollProjectDetectorTests : IDisposable
{
    private readonly string _outputFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _assemblyPath;

    public ReqnrollProjectDetectorTests()
    {
        Directory.CreateDirectory(_outputFolder);
        _assemblyPath = Path.Combine(_outputFolder, "MyApp.Tests.dll");
        File.WriteAllText(_assemblyPath, "not a real assembly");
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputFolder))
            Directory.Delete(_outputFolder, recursive: true);
    }

    private static ReqnrollProjectDetector CreateSut() => new(new FileSystemForIDE());

    private IProjectScope MakeScope(params string[] packageNames)
    {
        var scope = Substitute.For<IProjectScope>();
        scope.ProjectName.Returns("MyApp.Tests");
        scope.OutputAssemblyPath.Returns(_assemblyPath);
        scope.PackageReferences.Returns(packageNames
            .Select(name => new NuGetPackageReference(name, new NuGetVersion("1.0.0", "1.0.0"), null))
            .ToArray());
        return scope;
    }

    // ── Package-reference signal ──────────────────────────────────────────────

    [Theory]
    [InlineData("Reqnroll")]
    [InlineData("Reqnroll.MsTest")]
    [InlineData("Reqnroll.xunit.v3")]
    [InlineData("Reqnroll.Tools.MsBuild.Generation")]
    [InlineData("Reqnroll.SpecFlowCompatibility.ReqnrollPlugin")]
    [InlineData("SpecSync.AzureDevOps.Reqnroll.2-1")]
    [InlineData("SpecFlow")]
    [InlineData("TechTalk.SpecFlow")]
    [InlineData("SpecFlow.NUnit")]
    [InlineData("CucumberExpressions.SpecFlow.3-9")]
    [InlineData("reqnroll.mstest")] // package ids are compared case-insensitively
    public void Recognises_a_reqnroll_or_specflow_package_reference(string packageName)
    {
        CreateSut().IsReqnrollProject(MakeScope("Newtonsoft.Json", packageName)).Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_project_with_only_unrelated_package_references()
    {
        CreateSut().IsReqnrollProject(MakeScope("Newtonsoft.Json", "Serilog")).Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_project_with_no_package_references_and_no_runtime_assembly()
    {
        CreateSut().IsReqnrollProject(MakeScope()).Should().BeFalse();
    }

    // ── Output-folder signal ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Reqnroll.dll")]
    [InlineData("TechTalk.SpecFlow.dll")]
    public void Recognises_a_runtime_assembly_next_to_the_output_assembly(string runtimeAssemblyName)
    {
        // Clients that report no package references at all still have to work: Rider sends an
        // empty list for every project, and VS can send one transiently while NuGet loads (#690).
        File.WriteAllText(Path.Combine(_outputFolder, runtimeAssemblyName), "not a real assembly");

        CreateSut().IsReqnrollProject(MakeScope()).Should().BeTrue();
    }

    [Fact]
    public void Recognises_a_transitive_reqnroll_reference_via_the_output_folder()
    {
        // The project references only an internal meta-package, so no package name mentions
        // Reqnroll, but the runtime still lands in the output folder.
        File.WriteAllText(Path.Combine(_outputFolder, "Reqnroll.dll"), "not a real assembly");

        CreateSut().IsReqnrollProject(MakeScope("Contoso.Testing.Common")).Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_project_whose_output_assembly_path_is_empty()
    {
        var scope = Substitute.For<IProjectScope>();
        scope.ProjectName.Returns("MyApp.Utilities");
        scope.OutputAssemblyPath.Returns(string.Empty);
        scope.PackageReferences.Returns([]);

        CreateSut().IsReqnrollProject(scope).Should().BeFalse();
    }

    [Fact]
    public void Tolerates_a_null_package_reference_collection()
    {
        var scope = Substitute.For<IProjectScope>();
        scope.ProjectName.Returns("MyApp.Utilities");
        scope.OutputAssemblyPath.Returns(_assemblyPath);
        scope.PackageReferences.Returns((IEnumerable<NuGetPackageReference>?)null);

        CreateSut().IsReqnrollProject(scope).Should().BeFalse();
    }
}
