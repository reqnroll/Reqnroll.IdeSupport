using System.IO;
using Reqnroll.IdeSupport.Rider.Backend.UnitTesting;
using Xunit;

namespace Reqnroll.IdeSupport.Rider.Backend.Tests;

public class CodeBehindPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "cbp");
    private static readonly string Project = Path.Combine(Root, "Specs");

    private static string P(params string[] parts) => Path.GetFullPath(Path.Combine(parts));

    [Fact]
    public void By_default_the_code_behind_is_next_to_the_feature()
    {
        var feature = P(Project, "Features", "Price.feature");

        Assert.Equal(P(Project, "Features", "Price.feature.cs"),
            CodeBehindPaths.Locate(Project, feature, useIntermediateOutputPath: false, intermediateOutputPath: @"obj\Debug\net8.0\"));
    }

    [Fact]
    public void With_the_intermediate_output_path_it_keeps_the_folder_under_obj()
    {
        var feature = P(Project, "A folder with spaces", "Examples.feature");

        Assert.Equal(P(Project, "obj", "Debug", "net8.0", "A folder with spaces", "Examples.feature.cs"),
            CodeBehindPaths.Locate(Project, feature, true, "obj/Debug/net8.0/"));
    }

    [Fact]
    public void A_feature_in_the_project_folder_itself_goes_straight_into_obj()
    {
        Assert.Equal(P(Project, "obj", "Release", "net10.0", "Root.feature.cs"),
            CodeBehindPaths.Locate(Project, P(Project, "Root.feature"), true, @"obj\Release\net10.0\"));
    }

    [Fact]
    public void A_linked_feature_outside_the_project_has_its_parent_steps_replaced_by_underscores()
    {
        var feature = P(Root, "Shared", "Login.feature");

        Assert.Equal(P(Project, "obj", "Debug", "net8.0", "_", "Shared", "Login.feature.cs"),
            CodeBehindPaths.Locate(Project, feature, true, @"obj\Debug\net8.0\"));
    }

    [Fact]
    public void An_absolute_intermediate_output_path_is_used_as_it_is()
    {
        var artifacts = P(Root, "artifacts", "obj", "Specs", "debug");

        Assert.Equal(Path.Combine(artifacts, "Features", "Price.feature.cs"),
            CodeBehindPaths.Locate(Project, P(Project, "Features", "Price.feature"), true, artifacts + Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_an_intermediate_output_path_there_is_no_location(string intermediateOutputPath)
    {
        Assert.Null(CodeBehindPaths.Locate(Project, P(Project, "Features", "Price.feature"), true, intermediateOutputPath));
    }

    [Fact]
    public void Relative_directory_spells_folders_as_msbuild_does()
    {
        var sep = Path.DirectorySeparatorChar.ToString();

        Assert.Equal(string.Empty, CodeBehindPaths.RelativeDirectory(Project, Project));
        Assert.Equal("Features" + sep, CodeBehindPaths.RelativeDirectory(Project, P(Project, "Features")));
        Assert.Equal(".." + sep + "Shared" + sep, CodeBehindPaths.RelativeDirectory(Project, P(Root, "Shared")));
    }

    [Fact]
    public void Same_path_ignores_separators_case_and_a_trailing_separator()
    {
        var path = P(Project, "obj", "Debug", "Price.feature.cs");

        Assert.True(CodeBehindPaths.SamePath(path, path.Replace(Path.DirectorySeparatorChar, '/').ToUpperInvariant()));
        Assert.False(CodeBehindPaths.SamePath(path, P(Project, "Features", "Price.feature.cs")));
        Assert.False(CodeBehindPaths.SamePath(path, null));
    }
}
