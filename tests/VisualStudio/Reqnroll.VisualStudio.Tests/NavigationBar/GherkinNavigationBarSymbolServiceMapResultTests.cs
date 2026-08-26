using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.NavigationBar;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.NavigationBar;

/// <summary>
/// Client-side mapping of a <c>textDocument/documentSymbol</c> result into the
/// <see cref="Reqnroll.IdeSupport.VisualStudio.NavigationBar.GherkinSymbolNode"/> tree
/// (<see cref="GherkinNavigationBarSymbolService.MapResult"/>).
/// </summary>
public class GherkinNavigationBarSymbolServiceMapResultTests
{
    private static JObject Position(int line, int character) => new()
    {
        ["line"]      = line,
        ["character"] = character,
    };

    private static JObject Range(int startLine, int startChar, int endLine, int endChar) => new()
    {
        ["start"] = Position(startLine, startChar),
        ["end"]   = Position(endLine, endChar),
    };

    private static JObject Symbol(string name, int kind, JObject range, JObject? selectionRange = null, JArray? children = null) => new()
    {
        ["name"]           = name,
        ["kind"]           = kind,
        ["range"]          = range,
        ["selectionRange"] = selectionRange ?? range,
        ["children"]       = children ?? new JArray(),
    };

    [Fact]
    public void A_null_or_empty_result_maps_to_no_symbols()
    {
        GherkinNavigationBarSymbolService.MapResult(null).Should().BeEmpty();
        GherkinNavigationBarSymbolService.MapResult(new JArray()).Should().BeEmpty();
    }

    [Fact]
    public void A_feature_symbol_maps_name_kind_and_ranges()
    {
        var result = GherkinNavigationBarSymbolService.MapResult(new JArray(
            Symbol("Calculator", 2, Range(0, 0, 9, 0), Range(0, 0, 0, 19))));

        result.Should().ContainSingle();
        var feature = result[0];
        feature.Name.Should().Be("Calculator");
        feature.Kind.Should().Be(2);
        feature.Range.Start.Line.Should().Be(0);
        feature.Range.End.Line.Should().Be(9);
        feature.SelectionRange.End.Character.Should().Be(19);
    }

    [Fact]
    public void Nested_children_are_mapped_recursively()
    {
        var stepSymbol = Symbol("Given a step", 8, Range(6, 0, 6, 29));
        var scenarioSymbol = Symbol("Add two numbers", 6, Range(5, 0, 7, 33), children: new JArray(stepSymbol));
        var featureSymbol = Symbol("Calculator", 2, Range(0, 0, 9, 0), children: new JArray(scenarioSymbol));

        var result = GherkinNavigationBarSymbolService.MapResult(new JArray(featureSymbol));

        var feature = result[0];
        feature.Children.Should().ContainSingle();
        var scenario = feature.Children[0];
        scenario.Name.Should().Be("Add two numbers");
        scenario.Children.Should().ContainSingle();
        scenario.Children[0].Name.Should().Be("Given a step");
        scenario.Children[0].Kind.Should().Be(8);
    }

    [Fact]
    public void Missing_range_maps_to_default_zero_range()
    {
        var symbol = new JObject { ["name"] = "Feature", ["kind"] = 2 };

        var result = GherkinNavigationBarSymbolService.MapResult(new JArray(symbol));

        result[0].Range.Start.Line.Should().Be(0);
        result[0].Range.End.Character.Should().Be(0);
    }

    [Fact]
    public void Non_object_array_entries_are_skipped()
    {
        var result = GherkinNavigationBarSymbolService.MapResult(new JArray(
            new JValue("not an object"),
            Symbol("Calculator", 2, Range(0, 0, 9, 0))));

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Calculator");
    }

    [Fact]
    public void Detail_is_mapped_when_present()
    {
        // Kind alone can't distinguish Scenario from Scenario Outline on the wire (both collapse
        // to LSP SymbolKind.Method) — Detail carries that distinction (issue #262 follow-up, Run
        // CodeLens "Run Scenario" vs "Run Scenarios" wording).
        var symbol = Symbol("SO", 6, Range(1, 0, 3, 0));
        symbol["detail"] = "Scenario Outline";

        var result = GherkinNavigationBarSymbolService.MapResult(new JArray(symbol));

        result[0].Detail.Should().Be("Scenario Outline");
    }

    [Fact]
    public void Missing_detail_maps_to_null()
    {
        var result = GherkinNavigationBarSymbolService.MapResult(new JArray(
            Symbol("Calculator", 2, Range(0, 0, 9, 0))));

        result[0].Detail.Should().BeNull();
    }
}
