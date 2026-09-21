using System;
using System.IO;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.TestReporter;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestReporter;

/// <summary>Covers <see cref="MtpProjectDetection"/>, the C# port of the Rider plugin's own ad hoc MTP-capability scan (issue #715 plan §5.7).</summary>
public sealed class MtpProjectDetectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-project-detection-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ProjectFile(string xml, string relativePath = "Proj/Proj.csproj")
    {
        var file = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, xml);
        return file;
    }

    [Theory]
    [InlineData("EnableMSTestRunner")]
    [InlineData("EnableNUnitRunner")]
    [InlineData("UseMicrosoftTestingPlatformRunner")]
    [InlineData("IsTestingPlatformApplication")]
    public void IsMtpCapable_is_true_when_the_project_itself_sets_any_recognized_property(string property)
    {
        var project = ProjectFile($"<Project><PropertyGroup><{property}>true</{property}></PropertyGroup></Project>");

        MtpProjectDetection.IsMtpCapable(project).Should().BeTrue();
    }

    [Fact]
    public void IsMtpCapable_is_false_for_a_plain_project()
    {
        var project = ProjectFile("<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        MtpProjectDetection.IsMtpCapable(project).Should().BeFalse();
    }

    [Fact]
    public void IsMtpCapable_is_false_when_the_property_is_explicitly_false()
    {
        var project = ProjectFile("<Project><PropertyGroup><EnableMSTestRunner>false</EnableMSTestRunner></PropertyGroup></Project>");

        MtpProjectDetection.IsMtpCapable(project).Should().BeFalse();
    }

    [Fact]
    public void IsMtpCapable_finds_a_repo_root_Directory_Build_props_the_project_itself_does_not_set()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        File.WriteAllText(Path.Combine(_dir, "Directory.Build.props"),
            "<Project><PropertyGroup><EnableNUnitRunner>true</EnableNUnitRunner></PropertyGroup></Project>");
        var project = ProjectFile("<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>", "src/Tests/Tests.csproj");

        MtpProjectDetection.IsMtpCapable(project).Should().BeTrue();
    }

    [Fact]
    public void IsMtpCapable_does_not_climb_above_the_git_root()
    {
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Directory.Build.props"),
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup></Project>");
        var repo = Path.Combine(outside, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var project = ProjectFile("<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>", "outside/repo/src/Tests/Tests.csproj");

        MtpProjectDetection.IsMtpCapable(project).Should().BeFalse();
    }

    [Fact]
    public void IsMtpCapable_is_false_for_a_project_file_that_does_not_exist_and_no_props_anywhere()
    {
        Directory.CreateDirectory(_dir);

        MtpProjectDetection.IsMtpCapable(Path.Combine(_dir, "Missing", "Missing.csproj")).Should().BeFalse();
    }
}
