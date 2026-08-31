using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.Common.Tests.ProjectSystem;

public class PathUtilsTests
{
    // ── Regression: a sibling folder whose name extends the prefix must not match ──────────
    // Confirmed live: "C:\Repo\Minimalnet481\Foo.cs" is a plain string.StartsWith match for
    // "C:\Repo\Minimal", even though Minimalnet481 is a completely different project folder —
    // this let one project's step-definition bindings bleed into another's registry.

    [Fact]
    public void Sibling_folder_whose_name_extends_the_prefix_does_not_match()
    {
        PathUtils.IsUnderFolder(
                @"C:\Repo\Minimalnet481\StepDefinitions\Steps.cs",
                @"C:\Repo\Minimal")
            .Should().BeFalse();
    }

    [Fact]
    public void File_directly_under_the_folder_matches()
    {
        PathUtils.IsUnderFolder(
                @"C:\Repo\Minimal\StepDefinitions\Steps.cs",
                @"C:\Repo\Minimal")
            .Should().BeTrue();
    }

    [Fact]
    public void File_nested_several_levels_under_the_folder_matches()
    {
        PathUtils.IsUnderFolder(
                @"C:\Repo\Minimal\A\B\C\Steps.cs",
                @"C:\Repo\Minimal")
            .Should().BeTrue();
    }

    [Fact]
    public void Path_equal_to_the_folder_itself_matches()
    {
        PathUtils.IsUnderFolder(@"C:\Repo\Minimal", @"C:\Repo\Minimal")
            .Should().BeTrue();
    }

    [Fact]
    public void Trailing_separator_on_the_folder_is_tolerated()
    {
        PathUtils.IsUnderFolder(
                @"C:\Repo\Minimal\Steps.cs",
                @"C:\Repo\Minimal\")
            .Should().BeTrue();
    }

    [Fact]
    public void Comparison_is_case_insensitive()
    {
        PathUtils.IsUnderFolder(
                @"c:\repo\minimal\steps.cs",
                @"C:\Repo\Minimal")
            .Should().BeTrue();
    }

    [Fact]
    public void Unrelated_folder_does_not_match()
    {
        PathUtils.IsUnderFolder(
                @"C:\Repo\Other\Steps.cs",
                @"C:\Repo\Minimal")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(null, @"C:\Repo\Minimal")]
    [InlineData(@"C:\Repo\Minimal\Steps.cs", null)]
    [InlineData("", @"C:\Repo\Minimal")]
    [InlineData(@"C:\Repo\Minimal\Steps.cs", "")]
    public void Null_or_empty_inputs_do_not_match(string? filePath, string? folder)
    {
        PathUtils.IsUnderFolder(filePath, folder).Should().BeFalse();
    }

    // ── NormalizeForComparison / IsSamePath (issue #540 F4, F5) ────────────────────────────
    // These run on windows-latest in CI (every .NET test job does), so Windows path shapes are
    // the shapes under test.

    [Fact]
    public void Same_file_written_with_different_separators_and_casing_matches()
    {
        PathUtils.IsSamePath(@"C:\Repo\Minimal\Steps.cs", "c:/repo/minimal/Steps.cs")
            .Should().BeTrue();
    }

    [Fact]
    public void Relative_segments_are_collapsed_before_comparing()
    {
        PathUtils.IsSamePath(@"C:\Repo\Minimal\Sub\..\Steps.cs", @"C:\Repo\Minimal\Steps.cs")
            .Should().BeTrue();
    }

    [Fact]
    public void Trailing_separator_does_not_affect_identity()
    {
        PathUtils.IsSamePath(@"C:\Repo\Minimal\", @"C:\Repo\Minimal").Should().BeTrue();
    }

    [Theory]
    [InlineData("/workspaces/host-solution/Specs/Steps.cs")]  // devcontainer build
    [InlineData("/_/Specs/Steps.cs")]                         // deterministic / CI build
    public void Foreign_absolute_path_is_not_rebased_onto_the_current_drive(string foreignPath)
    {
        // Path.GetFullPath would silently turn "/workspaces/host-solution/Specs/Steps.cs" into
        // "C:\workspaces\host-solution\Specs\Steps.cs" — rooted on whichever drive the server
        // process happens to be running from. That makes the normalized form process-dependent and
        // can manufacture a false match against a workspace that genuinely lives there. A path that
        // is rooted but not fully qualified must keep its shape (issue #540 F5).
        var normalized = PathUtils.NormalizeForComparison(foreignPath);

        normalized.Should().NotContain(":");
        normalized.Should().Be(foreignPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Foreign_path_does_not_match_the_local_path_it_would_have_been_rebased_onto()
    {
        PathUtils.IsSamePath("/workspaces/host-solution/Steps.cs", @"C:\workspaces\host-solution\Steps.cs")
            .Should().BeFalse();
    }

    [Fact]
    public void Foreign_path_matches_itself()
    {
        // Two connector-discovered bindings from the same devcontainer-built file still have to
        // recognise each other, even though neither resolves locally.
        PathUtils.IsSamePath("/workspaces/host-solution/Steps.cs", "/workspaces/host-solution/Steps.cs")
            .Should().BeTrue();
    }

    [Fact]
    public void Unc_paths_are_compared_as_fully_qualified()
    {
        PathUtils.IsSamePath(@"\\server\share\Repo\Steps.cs", @"\\SERVER\share\Repo\Steps.cs")
            .Should().BeTrue();
    }

    [Fact]
    public void Drive_relative_path_is_not_resolved_against_the_current_directory()
    {
        // "C:Steps.cs" is rooted but drive-relative; resolving it would pull in the process's
        // current directory on that drive.
        PathUtils.NormalizeForComparison("C:Steps.cs").Should().Be("C:Steps.cs");
    }

    [Theory]
    [InlineData(null, @"C:\Repo\Steps.cs")]
    [InlineData(@"C:\Repo\Steps.cs", null)]
    [InlineData("", @"C:\Repo\Steps.cs")]
    [InlineData("   ", @"C:\Repo\Steps.cs")]
    [InlineData(null, null)]
    public void An_absent_path_is_not_the_same_file_as_anything(string? a, string? b)
    {
        // Including another absent path: "no file" is not an identity. SourceLocation.Equals needs
        // the opposite convention and handles that case itself rather than changing this one.
        PathUtils.IsSamePath(a, b).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalizing_an_absent_path_yields_empty_rather_than_throwing(string? path)
    {
        PathUtils.NormalizeForComparison(path).Should().BeEmpty();
    }
}
