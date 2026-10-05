using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Workspace;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Workspace;

public class LspReqnrollProjectTests
{
    private readonly TestIdeScope _ideScope = new(Substitute.For<IIdeSupportLogger>());

    private static ReqnrollProjectLoadedParams Params(
        string outputAssemblyPath = @"C:\repo\proj\bin\Debug\net8.0\Proj.dll",
        string tfm = ".NETCoreApp,Version=v8.0")
        => new()
        {
            WorkspaceFolder        = @"C:\repo",
            ProjectFile            = @"C:\repo\proj\Proj.csproj",
            ProjectFolder          = @"C:\repo\proj",
            OutputAssemblyPath     = outputAssemblyPath,
            TargetFrameworkMoniker = tfm
        };

    [Fact]
    public void Constructor_populates_identity_and_build_properties()
    {
        var project = new LspReqnrollProject(Params(), _ideScope);

        project.ProjectName.Should().Be("Proj");
        project.ProjectFullName.Should().Be(@"C:\repo\proj\Proj.csproj");
        project.ProjectFolder.Should().Be(@"C:\repo\proj");
        project.OutputAssemblyPath.Should().Be(@"C:\repo\proj\bin\Debug\net8.0\Proj.dll");
        project.TargetFrameworkMoniker.Should().Be(".NETCoreApp,Version=v8.0");
    }

    [Fact]
    public void Update_returns_true_when_output_assembly_path_changes()
    {
        var project = new LspReqnrollProject(Params(), _ideScope);

        var changed = project.Update(Params(outputAssemblyPath: @"C:\repo\proj\bin\Release\net8.0\Proj.dll"));

        changed.Should().BeTrue();
        project.OutputAssemblyPath.Should().Be(@"C:\repo\proj\bin\Release\net8.0\Proj.dll");
    }

    [Fact]
    public void Update_returns_true_when_target_framework_changes()
    {
        var project = new LspReqnrollProject(Params(), _ideScope);

        var changed = project.Update(Params(tfm: ".NETCoreApp,Version=v9.0"));

        changed.Should().BeTrue();
        project.TargetFrameworkMoniker.Should().Be(".NETCoreApp,Version=v9.0");
    }

    [Fact]
    public void Update_returns_false_when_discovery_inputs_are_unchanged()
    {
        var project = new LspReqnrollProject(Params(), _ideScope);

        var changed = project.Update(Params()); // identical output path + TFM

        changed.Should().BeFalse();
    }

    [Fact]
    public void Update_treats_output_path_comparison_as_case_insensitive()
    {
        var project = new LspReqnrollProject(Params(), _ideScope);

        var changed = project.Update(Params(
            outputAssemblyPath: @"C:\REPO\PROJ\BIN\DEBUG\NET8.0\PROJ.DLL"));

        changed.Should().BeFalse("Windows file paths are case-insensitive");
    }
}
