using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.VisualStudio.Shell.Interop;
using NSubstitute;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspNotifications;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspNotifications;

/// <summary>
/// <see cref="VsProjectEventMonitor"/> reacts to <see cref="IVsTrackProjectDocumentsEvents2"/>
/// callbacks (rename/add/remove of individual files) so the server's <c>reqnroll/projectFiles</c>
/// membership index does not go stale between full builds/solution reloads (issue #32). The
/// callback plumbing itself is COM/DTE-bound and not practical to unit test (see
/// ScaffoldTrackingInterceptorTests for the same tradeoff), but the index math and file
/// classification it depends on are pure and covered here.
/// </summary>
public class VsProjectEventMonitorTests
{
    // ── GroupByProject ────────────────────────────────────────────────────────

    [Fact]
    public void A_single_project_claims_the_whole_file_range()
    {
        var project = Substitute.For<IVsProject>();

        var groups = VsProjectEventMonitor.GroupByProject(
            projectCount: 1, fileCount: 3,
            projects: new[] { project },
            fileStartIndices: new[] { 0 }).ToList();

        groups.Should().ContainSingle();
        groups[0].Project.Should().BeSameAs(project);
        groups[0].Start.Should().Be(0);
        groups[0].Count.Should().Be(3);
    }

    [Fact]
    public void Multiple_projects_split_the_flat_file_arrays_by_fileStartIndices()
    {
        var projectA = Substitute.For<IVsProject>();
        var projectB = Substitute.For<IVsProject>();

        var groups = VsProjectEventMonitor.GroupByProject(
            projectCount: 2, fileCount: 5,
            projects: new[] { projectA, projectB },
            fileStartIndices: new[] { 0, 3 }).ToList();

        groups.Should().HaveCount(2);
        groups[0].Project.Should().BeSameAs(projectA);
        groups[0].Start.Should().Be(0);
        groups[0].Count.Should().Be(3);
        groups[1].Project.Should().BeSameAs(projectB);
        groups[1].Start.Should().Be(3);
        groups[1].Count.Should().Be(2);
    }

    [Fact]
    public void A_project_that_owns_zero_files_in_the_batch_is_skipped()
    {
        var projectA = Substitute.For<IVsProject>();
        var projectB = Substitute.For<IVsProject>(); // owns no files in this call
        var projectC = Substitute.For<IVsProject>();

        var groups = VsProjectEventMonitor.GroupByProject(
            projectCount: 3, fileCount: 5,
            projects: new[] { projectA, projectB, projectC },
            fileStartIndices: new[] { 0, 2, 2 }).ToList();

        groups.Should().HaveCount(2);
        groups[0].Project.Should().BeSameAs(projectA);
        groups[0].Start.Should().Be(0);
        groups[0].Count.Should().Be(2);
        groups[1].Project.Should().BeSameAs(projectC);
        groups[1].Start.Should().Be(2);
        groups[1].Count.Should().Be(3);
    }

    [Fact]
    public void No_files_in_the_batch_yields_no_groups()
    {
        var project = Substitute.For<IVsProject>();

        var groups = VsProjectEventMonitor.GroupByProject(
            projectCount: 1, fileCount: 0,
            projects: new[] { project },
            fileStartIndices: new[] { 0 }).ToList();

        groups.Should().BeEmpty();
    }

    // ── ClassifyRole ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Solution\Project\Features\Foo.feature", 0)]
    [InlineData(@"C:\Solution\Project\Features\Foo.FEATURE", 0)] // extension match is case-insensitive
    [InlineData(@"C:\Solution\Project\StepDefinitions\Foo.cs", 1)]
    [InlineData(@"C:\Solution\Project\StepDefinitions\Foo.CS", 1)]
    public void Feature_and_cs_files_are_classified_by_role(string path, int expectedRole)
    {
        VsProjectEventMonitor.ClassifyRole(path).Should().Be(expectedRole);
    }

    [Theory]
    [InlineData(@"C:\Solution\Project\Project.csproj")]
    [InlineData(@"C:\Solution\Project\notes.txt")]
    [InlineData(@"C:\Solution\Project\noextension")]
    public void Files_outside_the_membership_index_are_not_classified(string path)
    {
        VsProjectEventMonitor.ClassifyRole(path).Should().BeNull();
    }
}
