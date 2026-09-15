using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.FormatDocument;

namespace Reqnroll.VisualStudio.Tests.FormatDocument;

/// <summary>
/// Client-side mapping of a <c>textDocument/formatting</c>/<c>rangeFormatting</c> result (an LSP
/// <c>TextEdit[]</c>) into <see cref="GherkinLineRangeEdit"/>s (<see cref="FormatDocumentService.MapResult"/>).
/// Mirrors <see cref="GoToHooks.GoToHooksServiceMapResultTests"/>, which covers the analogous mapping
/// for the sibling Go to Hooks service.
/// </summary>
public class FormatDocumentServiceMapResultTests
{
    private static JObject TextEdit(int startLine, int endLine, string newText) => new()
    {
        ["range"] = new JObject
        {
            ["start"] = new JObject { ["line"] = startLine, ["character"] = 0 },
            ["end"]   = new JObject { ["line"] = endLine,   ["character"] = 12 },
        },
        ["newText"] = newText,
    };

    [Fact]
    public void A_null_or_non_array_result_yields_no_edits()
    {
        FormatDocumentService.MapResult(null).Should().BeEmpty();
        FormatDocumentService.MapResult(JValue.CreateNull()).Should().BeEmpty();
        FormatDocumentService.MapResult(new JObject()).Should().BeEmpty();
    }

    [Fact]
    public void An_empty_array_yields_no_edits()
    {
        FormatDocumentService.MapResult(new JArray()).Should().BeEmpty();
    }

    [Fact]
    public void Edits_are_parsed_with_line_range_and_new_text()
    {
        var edits = FormatDocumentService.MapResult(new JArray(
            TextEdit(0, 34, "Feature: Addition\n...")));

        edits.Should().ContainSingle();
        edits[0].StartLine.Should().Be(0);
        edits[0].EndLine.Should().Be(34);
        edits[0].NewText.Should().Be("Feature: Addition\n...");
    }

    [Fact]
    public void Multiple_edits_are_all_parsed()
    {
        var edits = FormatDocumentService.MapResult(new JArray(
            TextEdit(0, 5, "first"),
            TextEdit(10, 12, "second")));

        edits.Should().HaveCount(2);
        edits[0].NewText.Should().Be("first");
        edits[1].NewText.Should().Be("second");
    }

    [Fact]
    public void An_edit_missing_range_or_newText_is_skipped()
    {
        var edits = FormatDocumentService.MapResult(new JArray(
            new JObject { ["newText"] = "no range" }, // missing range
            new JObject { ["range"] = TextEdit(0, 1, "x")["range"] }, // missing newText
            TextEdit(2, 3, "kept")));

        edits.Should().ContainSingle();
        edits[0].NewText.Should().Be("kept");
    }
}
