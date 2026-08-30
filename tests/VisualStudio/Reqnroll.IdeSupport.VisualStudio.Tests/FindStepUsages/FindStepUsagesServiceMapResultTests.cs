using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.FindStepUsages;

/// <summary>
/// Client-side mapping of a <c>reqnroll/findStepUsages</c> result into the three-state
/// <see cref="StepUsagesResult"/> (<see cref="FindStepUsagesService.MapResult"/>). The three
/// states drive whether Surface 3 falls through to the built-in command, shows "0 usages",
/// or shows a results window.
/// </summary>
public class FindStepUsagesServiceMapResultTests
{
    private static JObject Location(string uri, int startLine) => new()
    {
        ["uri"]          = uri,
        ["startLine"]    = startLine,
        ["startChar"]    = 4,
        ["endLine"]      = startLine,
        ["endChar"]      = 20,
        ["stepText"]     = "I press add",
        ["keyword"]      = "When ",
        ["scenarioName"] = "Add",
        ["projectName"]  = "Sample",
    };

    [Fact]
    public void A_null_or_non_object_result_is_not_a_binding()
    {
        FindStepUsagesService.MapResult(null).IsBinding.Should().BeFalse();
        FindStepUsagesService.MapResult(JValue.CreateNull()).IsBinding.Should().BeFalse();
        FindStepUsagesService.MapResult(new JArray()).IsBinding.Should().BeFalse();
    }

    [Fact]
    public void IsBinding_false_maps_to_NotABinding()
    {
        var result = FindStepUsagesService.MapResult(new JObject { ["isBinding"] = false });
        result.IsBinding.Should().BeFalse();
    }

    [Fact]
    public void IsBinding_true_with_no_locations_is_a_binding_with_zero_usages()
    {
        var result = FindStepUsagesService.MapResult(new JObject
        {
            ["isBinding"] = true,
            ["locations"] = new JArray(),
        });

        result.IsBinding.Should().BeTrue();
        result.Locations.Should().BeEmpty();
    }

    [Fact]
    public void IsBinding_true_with_locations_parses_each_usage()
    {
        var result = FindStepUsagesService.MapResult(new JObject
        {
            ["isBinding"] = true,
            ["locations"] = new JArray(
                Location("file:///c:/w/A.feature", 2),
                Location("file:///c:/w/B.feature", 5)),
        });

        result.IsBinding.Should().BeTrue();
        result.Locations.Should().HaveCount(2);
        result.Locations[0].FileUri.Should().Be("file:///c:/w/A.feature");
        result.Locations[0].StartLine.Should().Be(2);
        result.Locations[0].StepText.Should().Be("I press add");
        result.Locations[0].Keyword.Should().Be("When ");
        result.Locations[1].StartLine.Should().Be(5);
    }

    [Fact]
    public void A_location_without_a_uri_is_skipped()
    {
        var result = FindStepUsagesService.MapResult(new JObject
        {
            ["isBinding"] = true,
            ["locations"] = new JArray(
                new JObject { ["startLine"] = 1 }, // no uri
                Location("file:///c:/w/A.feature", 2)),
        });

        result.Locations.Should().ContainSingle();
        result.Locations[0].FileUri.Should().Be("file:///c:/w/A.feature");
    }
}
