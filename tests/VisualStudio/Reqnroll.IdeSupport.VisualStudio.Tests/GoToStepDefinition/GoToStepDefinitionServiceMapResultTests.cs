using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.GoToStepDefinition;

/// <summary>
/// Client-side mapping of a <c>textDocument/definition</c> result into step-definition locations
/// (<see cref="GoToStepDefinitionService.MapResult"/>, issue #757).
/// </summary>
public class GoToStepDefinitionServiceMapResultTests
{
    private static JObject Range(int line, int character) => new()
    {
        ["start"] = new JObject { ["line"] = line, ["character"] = character },
        ["end"]   = new JObject { ["line"] = line, ["character"] = character + 5 },
    };

    private static JObject Location(string uri, int line, int character) => new()
    {
        ["uri"]   = uri,
        ["range"] = Range(line, character),
    };

    [Fact]
    public void A_null_or_empty_result_has_no_locations()
    {
        GoToStepDefinitionService.MapResult(null).Should().BeEmpty();
        GoToStepDefinitionService.MapResult(JValue.CreateNull()).Should().BeEmpty();
        GoToStepDefinitionService.MapResult(new JArray()).Should().BeEmpty();
    }

    [Fact]
    public void A_location_array_maps_every_entry_in_order()
    {
        var result = new JArray(
            Location("file:///c:/w/Steps.cs", 16, 20),
            Location("file:///c:/w/Steps.cs", 24, 20));

        GoToStepDefinitionService.MapResult(result).Should().Equal(
            new StepDefinitionLocation("file:///c:/w/Steps.cs", 16, 20),
            new StepDefinitionLocation("file:///c:/w/Steps.cs", 24, 20));
    }

    [Fact]
    public void A_single_location_object_maps_to_one_entry()
    {
        GoToStepDefinitionService.MapResult(Location("file:///c:/w/Steps.cs", 3, 4))
            .Should().Equal(new StepDefinitionLocation("file:///c:/w/Steps.cs", 3, 4));
    }

    [Fact]
    public void A_location_link_uses_its_target_selection_range()
    {
        var link = new JObject
        {
            ["targetUri"]            = "file:///c:/w/Steps.cs",
            ["targetRange"]          = Range(10, 4),
            ["targetSelectionRange"] = Range(11, 20),
        };

        GoToStepDefinitionService.MapResult(new JArray(link))
            .Should().Equal(new StepDefinitionLocation("file:///c:/w/Steps.cs", 11, 20));
    }

    [Fact]
    public void Repeats_of_the_same_position_are_collapsed()
    {
        // One method with two matching [Given] attributes: two bindings, one navigation target.
        var result = new JArray(
            Location("file:///c:/w/Steps.cs", 16, 20),
            Location("file:///c:/w/Steps.cs", 16, 20),
            Location("file:///c:/w/Steps.cs", 24, 20));

        GoToStepDefinitionService.MapResult(result).Should().Equal(
            new StepDefinitionLocation("file:///c:/w/Steps.cs", 16, 20),
            new StepDefinitionLocation("file:///c:/w/Steps.cs", 24, 20));
    }

    [Fact]
    public void Entries_without_a_uri_or_start_position_are_skipped()
    {
        var result = new JArray(
            new JObject { ["range"] = Range(1, 1) },
            new JObject { ["uri"] = "file:///c:/w/Steps.cs" },
            Location("file:///c:/w/Steps.cs", 7, 8));

        GoToStepDefinitionService.MapResult(result)
            .Should().Equal(new StepDefinitionLocation("file:///c:/w/Steps.cs", 7, 8));
    }
}
