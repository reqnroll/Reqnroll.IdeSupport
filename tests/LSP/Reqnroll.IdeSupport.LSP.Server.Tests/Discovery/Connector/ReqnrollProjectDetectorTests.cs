using System.Collections.Concurrent;
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
    private readonly string _projectFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _outputFolder;
    private readonly string _assemblyPath;

    public ReqnrollProjectDetectorTests()
    {
        // A real output folder under the project folder, so the reqnroll.json the configuration
        // provider looks for in the project folder and the runtime-assembly probe in the output
        // folder are independent of each other.
        _outputFolder = Path.Combine(_projectFolder, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(_outputFolder);
        _assemblyPath = Path.Combine(_outputFolder, "MyApp.Tests.dll");
        File.WriteAllText(_assemblyPath, "not a real assembly");
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectFolder))
            Directory.Delete(_projectFolder, recursive: true);
    }

    private static ReqnrollProjectDetector CreateSut() => new(new FileSystemForIDE());

    private IProjectScope MakeScope(params string[] packageNames)
    {
        var scope = Substitute.For<IProjectScope>();
        scope.ProjectName.Returns("MyApp.Tests");
        scope.ProjectFolder.Returns(_projectFolder);
        scope.OutputAssemblyPath.Returns(_assemblyPath);
        // A real bag: the configuration provider is created lazily and cached in it.
        scope.Properties.Returns(new ConcurrentDictionary<Type, object>());
        scope.IdeScope.FileSystem.Returns(new FileSystemForIDE());
        scope.PackageReferences.Returns(packageNames
            .Select(name => new NuGetPackageReference(name, new NuGetVersion("1.0.0", "1.0.0"), null))
            .ToArray());
        return scope;
    }

    /// <summary>Writes a reqnroll.json carrying the <c>ide.reqnroll.isReqnrollProject</c> override.</summary>
    private void GivenReqnrollJsonWithIsReqnrollProject(bool value)
    {
        var json = $$"""
        {
          "ide": {
            "reqnroll": {
              "isReqnrollProject": {{(value ? "true" : "false")}}
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_projectFolder, "reqnroll.json"), json);
    }

    // ── Package-reference signal ──────────────────────────────────────────────

    [Theory]
    [InlineData("Reqnroll")]
    [InlineData("Reqnroll.MsTest")]
    [InlineData("Reqnroll.NUnit")]
    [InlineData("Reqnroll.xUnit")]
    [InlineData("Reqnroll.xunit.v3")]
    [InlineData("Reqnroll.TUnit")]
    [InlineData("Reqnroll.Tools.MsBuild.Generation")]
    [InlineData("Reqnroll.SpecFlowCompatibility.ReqnrollPlugin")]
    [InlineData("SpecSync.AzureDevOps.Reqnroll.2-1")]
    [InlineData("reqnroll.mstest")] // package ids are compared case-insensitively
    public void Recognises_a_reqnroll_package_reference(string packageName)
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

    [Fact]
    public void Rejects_a_legacy_specflow_project()
    {
        // SpecFlow is out of scope for this tooling: a SpecFlow-only project is skipped like any
        // other non-Reqnroll project, whichever signal it would otherwise have matched on.
        File.WriteAllText(Path.Combine(_outputFolder, "TechTalk.SpecFlow.dll"), "not a real assembly");

        CreateSut().IsReqnrollProject(MakeScope("SpecFlow", "SpecFlow.NUnit")).Should().BeFalse();
    }

    // ── Output-folder signal ──────────────────────────────────────────────────

    [Fact]
    public void Recognises_the_runtime_assembly_next_to_the_output_assembly()
    {
        // Clients that report no package references at all still have to work: Rider sends an
        // empty list for every project, and VS can send one transiently while NuGet loads (#690).
        File.WriteAllText(Path.Combine(_outputFolder, "Reqnroll.dll"), "not a real assembly");

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
        var scope = MakeScope();
        scope.OutputAssemblyPath.Returns(string.Empty);

        CreateSut().IsReqnrollProject(scope).Should().BeFalse();
    }

    [Fact]
    public void Tolerates_a_null_package_reference_collection()
    {
        var scope = MakeScope();
        scope.PackageReferences.Returns((IEnumerable<NuGetPackageReference>?)null);

        CreateSut().IsReqnrollProject(scope).Should().BeFalse();
    }

    // ── Configuration override (ide.reqnroll.isReqnrollProject) ───────────────

    [Fact]
    public void Configured_true_forces_discovery_on_for_a_project_neither_heuristic_recognises()
    {
        GivenReqnrollJsonWithIsReqnrollProject(true);

        CreateSut().IsReqnrollProject(MakeScope("Newtonsoft.Json")).Should().BeTrue();
    }

    [Fact]
    public void Configured_false_forces_discovery_off_despite_a_reqnroll_package_reference()
    {
        GivenReqnrollJsonWithIsReqnrollProject(false);

        CreateSut().IsReqnrollProject(MakeScope("Reqnroll.MsTest")).Should().BeFalse();
    }

    [Fact]
    public void Configured_false_forces_discovery_off_despite_the_runtime_assembly()
    {
        GivenReqnrollJsonWithIsReqnrollProject(false);
        File.WriteAllText(Path.Combine(_outputFolder, "Reqnroll.dll"), "not a real assembly");

        CreateSut().IsReqnrollProject(MakeScope()).Should().BeFalse();
    }

    [Fact]
    public void Falls_back_to_the_heuristics_when_a_reqnroll_json_omits_the_setting()
    {
        // A reqnroll.json is present but says nothing about isReqnrollProject: the tri-state
        // bool? is null, which means "not configured", not "false".
        File.WriteAllText(Path.Combine(_projectFolder, "reqnroll.json"),
            """{ "language": { "feature": "en-US" } }""");

        CreateSut().IsReqnrollProject(MakeScope("Reqnroll.MsTest")).Should().BeTrue();
        CreateSut().IsReqnrollProject(MakeScope("Newtonsoft.Json")).Should().BeFalse();
    }
}
