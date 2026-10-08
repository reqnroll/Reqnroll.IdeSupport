using Reqnroll.IdeSupport.Common.Configuration;
using Xunit;

namespace Reqnroll.IdeSupport.Common.Tests.Configuration;

public class TraceabilityConfigurationTests
{
    private static TraceabilityConfiguration Configure(params (string Pattern, string Template)[] links)
    {
        var config = new TraceabilityConfiguration
        {
            TagLinks = links.Select(l => new TagLinkConfiguration { TagPattern = l.Pattern, UrlTemplate = l.Template })
                .ToArray()
        };
        config.CheckConfiguration();
        return config;
    }

    [Fact]
    public void ResolveTagLink_MatchingTag_ExpandsNamedGroupsIntoTemplate()
    {
        var config = Configure((@"issue\:(?<id>\d+)", "https://github.com/specsolutions/my-project/issues/{id}"));

        var url = config.ResolveTagLink("@issue:1234");

        Assert.Equal(new Uri("https://github.com/specsolutions/my-project/issues/1234"), url);
    }

    [Fact]
    public void ResolveTagLink_TagWithoutAtSign_Resolves()
    {
        var config = Configure((@"issue\:(?<id>\d+)", "https://example.com/{id}"));

        Assert.NotNull(config.ResolveTagLink("issue:7"));
    }

    [Fact]
    public void ResolveTagLink_NoPatternMatches_ReturnsNull()
    {
        var config = Configure((@"issue\:(?<id>\d+)", "https://example.com/{id}"));

        Assert.Null(config.ResolveTagLink("@smoke"));
    }

    [Fact]
    public void ResolveTagLink_PatternMustMatchWholeTag()
    {
        var config = Configure((@"issue\:(?<id>\d+)", "https://example.com/{id}"));

        Assert.Null(config.ResolveTagLink("@issue:12abc"));
    }

    [Fact]
    public void ResolveTagLink_TopLevelAlternation_MatchesWholeTagOnly()
    {
        var config = Configure((@"ab|cd", "https://example.com/"));

        Assert.NotNull(config.ResolveTagLink("@ab"));
        Assert.NotNull(config.ResolveTagLink("@cd"));
        Assert.Null(config.ResolveTagLink("@abX"));
        Assert.Null(config.ResolveTagLink("@Xcd"));
        Assert.Null(config.ResolveTagLink("@abcd"));
    }

    [Fact]
    public void ResolveTagLink_TrailingEscapedDollar_KeepsLiteralDollarAndAnchors()
    {
        var config = Configure((@"v\d+\$", "https://example.com/"));

        Assert.NotNull(config.ResolveTagLink("@v1$"));
        Assert.Null(config.ResolveTagLink("@v1$extra"));
        Assert.Null(config.ResolveTagLink("@v1"));
    }

    [Fact]
    public void ResolveTagLink_MultipleLinks_FirstMatchWins()
    {
        var config = Configure(
            (@"tc\:(?<id>\d+)", "https://first.example.com/{id}"),
            (@"tc\:(?<id>\d+)", "https://second.example.com/{id}"));

        Assert.Equal(new Uri("https://first.example.com/5"), config.ResolveTagLink("@tc:5"));
    }

    [Fact]
    public void ResolveTagLink_TemplateIsNotAbsoluteUri_ReturnsNull()
    {
        var config = Configure((@"issue\:(?<id>\d+)", "not a url {id}"));

        Assert.Null(config.ResolveTagLink("@issue:1"));
    }

    [Fact]
    public void ResolveTagLink_NoLinksConfigured_ReturnsNull()
    {
        Assert.Null(new TraceabilityConfiguration().ResolveTagLink("@issue:1"));
    }

    [Fact]
    public void ResolveTagLink_PathologicalPattern_TimesOutInsteadOfHanging()
    {
        var config = Configure((@"(a+)+", "https://example.com/{x}"));
        var tag = "@" + new string('a', 40) + "!";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var url = config.ResolveTagLink(tag);
        stopwatch.Stop();

        Assert.Null(url);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }
}
