using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Documents;

public class DocumentGenerationsTests
{
    private const string Uri = "file:///c:/proj/feature1.feature";

    [Fact]
    public void A_document_nothing_has_bumped_is_at_generation_zero()
    {
        new DocumentGenerations().Current(Uri).Should().Be(0);
    }

    [Fact]
    public void Each_bump_advances_the_generation_by_one_and_returns_it()
    {
        var sut = new DocumentGenerations();

        sut.Bump(Uri).Should().Be(1);
        sut.Bump(Uri).Should().Be(2);
        sut.Current(Uri).Should().Be(2);
    }

    [Fact]
    public void Documents_are_counted_independently()
    {
        var sut = new DocumentGenerations();

        sut.Bump(Uri);
        sut.Bump(Uri);
        sut.Bump("file:///c:/proj/feature2.feature");

        sut.Current(Uri).Should().Be(2);
        sut.Current("file:///c:/proj/feature2.feature").Should().Be(1);
    }

    [Fact]
    public void Document_ids_that_differ_only_in_case_share_one_generation()
    {
        var sut = new DocumentGenerations();

        sut.Bump("file:///c:/Proj/Feature1.feature");

        sut.Current("file:///C:/proj/feature1.FEATURE").Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_bumps_never_lose_an_increment()
    {
        var sut = new DocumentGenerations();
        const int bumps = 10_000;

        await Task.WhenAll(Enumerable.Range(0, bumps).Select(_ => Task.Run(() => sut.Bump(Uri))));

        sut.Current(Uri).Should().Be(bumps);
    }
}
