using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.DocumentLinks;
using Xunit;
using Reqnroll.IdeSupport.VisualStudio.DocumentLinks;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.DocumentLinks;

/// <summary>
/// Client-side pieces of clickable tags in Visual Studio (issue #755): mapping the server's
/// <c>DocumentLink[]</c>, locating the link under the pointer, and the URL-scheme allow-list.
/// </summary>
public class TagLinkTests
{
    private static JObject Link(int line, int startChar, int endChar, string? target) => new()
    {
        ["range"] = new JObject
        {
            ["start"] = new JObject { ["line"] = line, ["character"] = startChar },
            ["end"]   = new JObject { ["line"] = line, ["character"] = endChar },
        },
        ["target"] = target,
    };

    [Fact]
    public void MapResult_maps_range_and_target()
    {
        var links = DocumentLinkService.MapResult(new JArray(Link(1, 0, 11, "https://example.com/issues/1234")));

        links.Should().ContainSingle().Which.Should()
            .Be(new TagLinkEntry(1, 0, 1, 11, "https://example.com/issues/1234"));
    }

    [Fact]
    public void MapResult_skips_entries_without_a_target()
    {
        DocumentLinkService.MapResult(new JArray(Link(1, 0, 11, null))).Should().BeEmpty();
    }

    [Fact]
    public void MapResult_yields_nothing_for_null_or_non_array_results()
    {
        DocumentLinkService.MapResult(null).Should().BeEmpty();
        DocumentLinkService.MapResult(JValue.CreateNull()).Should().BeEmpty();
        DocumentLinkService.MapResult(new JObject()).Should().BeEmpty();
    }

    [Fact]
    public void Telemetry_event_is_named_after_the_catalog_constant_and_carries_no_properties()
    {
        var evt = TagLinkTelemetry.CreateEvent();

        evt.EventName.Should().Be("TagLink command executed");
        evt.Properties.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, 0, true)]    // the '@'
    [InlineData(1, 10, true)]   // last character of the tag
    [InlineData(1, 11, false)]  // end is exclusive
    [InlineData(0, 5, false)]   // other line
    [InlineData(2, 5, false)]
    public void Contains_is_start_inclusive_end_exclusive(int line, int character, bool expected)
    {
        new TagLinkEntry(1, 0, 1, 11, "https://example.com").Contains(line, character).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.com/issues/1", true)]
    [InlineData("http://example.com/issues/1", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-msdt:/id", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void IsOpenableUrl_allows_only_http_and_https(string? target, bool expected)
    {
        TagLinkRedirect.IsOpenableUrl(target).Should().Be(expected);
    }
}
