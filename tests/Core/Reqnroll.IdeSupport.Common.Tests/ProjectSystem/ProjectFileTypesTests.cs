using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.Common.Tests.ProjectSystem;

/// <summary>Covers shared-project classification (issue #735).</summary>
public class ProjectFileTypesTests
{
    [Theory]
    [InlineData(@"C:\repos\MyApp\Shared\MyApp.Shared.shproj")]
    [InlineData(@"C:\repos\MyApp\Shared\MyApp.Shared.SHPROJ")]
    [InlineData(@"C:\repos\MyApp\Shared\MyApp.Shared.projitems")]
    [InlineData("/home/me/repos/MyApp/Shared/MyApp.Shared.shproj")]
    public void Recognises_a_shared_project(string projectFilePath)
    {
        ProjectFileTypes.IsSharedProject(projectFilePath).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\repos\MyApp\tests\MyApp.Tests\MyApp.Tests.csproj")]
    [InlineData(@"C:\repos\MyApp\MyApp.vbproj")]
    [InlineData(@"C:\repos\MyApp\MyApp.sln")]
    // A project whose *name* ends in the word, rather than its extension.
    [InlineData(@"C:\repos\MyApp\Shared\MyApp.shproj.csproj")]
    public void Does_not_recognise_an_ordinary_project(string projectFilePath)
    {
        ProjectFileTypes.IsSharedProject(projectFilePath).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Treats_a_missing_path_as_not_shared(string? projectFilePath)
    {
        ProjectFileTypes.IsSharedProject(projectFilePath!).Should().BeFalse();
    }
}
