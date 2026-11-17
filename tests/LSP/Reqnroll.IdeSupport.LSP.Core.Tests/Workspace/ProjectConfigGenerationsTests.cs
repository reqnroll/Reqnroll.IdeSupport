using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Workspace;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Workspace;

public class ProjectConfigGenerationsTests
{
    private static readonly ProjectOwner ProjectA = new(@"c:\proj\A.csproj", "net10.0");
    private static readonly ProjectOwner ProjectB = new(@"c:\proj\B.csproj", "net10.0");

    [Fact]
    public void A_project_nothing_has_bumped_is_at_generation_zero()
    {
        new ProjectConfigGenerations().Current(ProjectA).Should().Be(0);
    }

    [Fact]
    public void Each_bump_advances_the_generation_by_one_and_returns_it()
    {
        var sut = new ProjectConfigGenerations();

        sut.Bump(ProjectA).Should().Be(1);
        sut.Bump(ProjectA).Should().Be(2);
        sut.Current(ProjectA).Should().Be(2);
    }

    [Fact]
    public void Projects_are_counted_independently()
    {
        var sut = new ProjectConfigGenerations();

        sut.Bump(ProjectA);

        sut.Current(ProjectA).Should().Be(1);
        sut.Current(ProjectB).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_bumps_never_lose_an_increment()
    {
        var sut = new ProjectConfigGenerations();
        const int bumps = 10_000;

        await Task.WhenAll(Enumerable.Range(0, bumps).Select(_ => Task.Run(() => sut.Bump(ProjectA))));

        sut.Current(ProjectA).Should().Be(bumps);
    }
}
