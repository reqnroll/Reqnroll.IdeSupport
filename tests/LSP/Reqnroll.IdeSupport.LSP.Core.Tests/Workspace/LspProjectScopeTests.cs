using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Workspace;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Workspace;

public class LspProjectScopeTests
{
    private readonly TestIdeScope _ideScope = new(Substitute.For<IIdeSupportLogger>());
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    private LspProjectScope CreateSut() => new(_root, _ideScope);

    private ReqnrollProjectLoadedParams Params(
        string outputAssemblyPath = "bin/Debug/Proj.dll",
        string tfm = ".NETCoreApp,Version=v8.0")
        => new()
        {
            WorkspaceFolder        = _root,
            ProjectFile            = Path.Combine(_root, "Proj.csproj"),
            ProjectFolder          = _root,
            OutputAssemblyPath     = Path.Combine(_root, outputAssemblyPath),
            TargetFrameworkMoniker = tfm
        };

    [Fact]
    public void AddOrUpdateProject_reports_new_project_as_new_and_changed()
    {
        var (project, isNew, changed) = CreateSut().AddOrUpdateProject(Params());

        project.Should().NotBeNull();
        isNew.Should().BeTrue();
        changed.Should().BeTrue();
    }

    [Fact]
    public void AddOrUpdateProject_returns_same_instance_and_not_new_on_second_call()
    {
        var sut = CreateSut();

        var (first, _, _) = sut.AddOrUpdateProject(Params());
        var (second, isNew, _) = sut.AddOrUpdateProject(Params());

        second.Should().BeSameAs(first);
        isNew.Should().BeFalse();
    }

    [Fact]
    public void AddOrUpdateProject_reports_changed_false_when_inputs_unchanged()
    {
        var sut = CreateSut();
        sut.AddOrUpdateProject(Params());

        var (_, isNew, changed) = sut.AddOrUpdateProject(Params());

        isNew.Should().BeFalse();
        changed.Should().BeFalse();
    }

    [Fact]
    public void AddOrUpdateProject_reports_changed_true_when_output_path_changes()
    {
        var sut = CreateSut();
        sut.AddOrUpdateProject(Params());

        var (_, isNew, changed) = sut.AddOrUpdateProject(Params(outputAssemblyPath: "bin/Release/Proj.dll"));

        isNew.Should().BeFalse();
        changed.Should().BeTrue();
    }

    [Fact]
    public void RemoveProject_returns_the_removed_project()
    {
        var sut = CreateSut();
        var (project, _, _) = sut.AddOrUpdateProject(Params());

        var removed = sut.RemoveProject(project.ProjectFullName);

        removed.Should().BeSameAs(project);
        sut.Projects.Should().BeEmpty();
    }

    [Fact]
    public void RemoveProject_returns_null_for_unknown_project()
    {
        CreateSut().RemoveProject(@"C:\nope\Unknown.csproj").Should().BeNull();
    }
}
