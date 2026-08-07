using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// Unit tests for <see cref="LspParamsBuilder"/> — the shared JSON-object builder for outbound
/// custom <c>reqnroll/*</c>/standard LSP request params, replacing the near-identical
/// <c>BuildParams</c> method duplicated across every LSP-calling service in the Extension project
/// (issue #447).
/// </summary>
/// <remarks>
/// Assertions parse the built string back through <see cref="Newtonsoft.Json.Linq.JObject"/> rather
/// than comparing raw strings — member order isn't a contract, only the resulting JSON shape is.
/// </remarks>
public class LspParamsBuilderTests
{
    // ── Static convenience builders ──────────────────────────────────────────────

    [Fact]
    public void TextDocumentUri_builds_the_textDocument_only_shape()
    {
        var json = LspParamsBuilder.TextDocumentUri("file:///a.feature");

        var obj = JObject.Parse(json);
        obj["textDocument"]?["uri"]?.Value<string>().Should().Be("file:///a.feature");
        obj.Properties().Should().ContainSingle();
    }

    [Fact]
    public void TextDocumentPosition_builds_the_textDocument_plus_position_shape()
    {
        var json = LspParamsBuilder.TextDocumentPosition("file:///a.feature", 3, 7);

        var obj = JObject.Parse(json);
        obj["textDocument"]?["uri"]?.Value<string>().Should().Be("file:///a.feature");
        obj["position"]?["line"]?.Value<int>().Should().Be(3);
        obj["position"]?["character"]?.Value<int>().Should().Be(7);
        obj.Properties().Should().HaveCount(2);
    }

    // ── Fluent composition ────────────────────────────────────────────────────────

    [Fact]
    public void AddBool_adds_a_true_valued_member()
    {
        var json = new LspParamsBuilder().AddTextDocument("file:///a.feature").AddBool("ownLevelOnly", true).Build();

        JObject.Parse(json)["ownLevelOnly"]?.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public void AddBool_adds_a_false_valued_member()
    {
        var json = new LspParamsBuilder().AddTextDocument("file:///a.feature").AddBool("ownLevelOnly", false).Build();

        JObject.Parse(json)["ownLevelOnly"]?.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public void AddString_adds_an_escaped_string_valued_member()
    {
        var json = new LspParamsBuilder().AddString("command", "reqnroll.toggleComment").Build();

        JObject.Parse(json)["command"]?.Value<string>().Should().Be("reqnroll.toggleComment");
    }

    [Fact]
    public void AddRaw_adds_a_verbatim_JSON_value()
    {
        var json = new LspParamsBuilder().AddRaw("context", "{\"includeDeclaration\":false}").Build();

        JObject.Parse(json)["context"]?["includeDeclaration"]?.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public void Members_are_comma_separated_and_produce_valid_JSON_regardless_of_how_many_are_added()
    {
        var json = new LspParamsBuilder()
            .AddTextDocument("file:///a.feature")
            .AddPosition(1, 2)
            .AddBool("ownLevelOnly", true)
            .AddString("extra", "value")
            .Build();

        var obj = JObject.Parse(json); // throws if the JSON is malformed (e.g. a missing/extra comma)
        obj.Properties().Should().HaveCount(4);
    }

    [Fact]
    public void Build_with_no_members_produces_an_empty_object()
    {
        var json = new LspParamsBuilder().Build();

        json.Should().Be("{}");
    }

    // ── Escaping ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("file:///with a space.feature")]
    [InlineData("file:///with\"a\"quote.feature")]
    [InlineData("file:///with\\backslash.feature")]
    [InlineData("file:///with\nnewline.feature")]
    public void EscapeString_produces_a_value_that_round_trips_through_JSON_parsing(string rawUri)
    {
        var json = LspParamsBuilder.TextDocumentUri(rawUri);

        JObject.Parse(json)["textDocument"]?["uri"]?.Value<string>().Should().Be(rawUri);
    }

    [Fact]
    public void AddString_escapes_hostile_content_the_same_way_as_EscapeString()
    {
        var json = new LspParamsBuilder().AddString("command", "a\"b\\c").Build();

        JObject.Parse(json)["command"]?.Value<string>().Should().Be("a\"b\\c");
    }
}
