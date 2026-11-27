using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.FormatDocument;
using Reqnroll.IdeSupport.VisualStudio.FormatDocument;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.FormatDocument;

/// <summary>
/// Pins the <c>textDocument/formatting</c>/<c>rangeFormatting</c> payload
/// <see cref="FormatDocumentService"/> sends: the <c>options</c> carry the editor's tab settings
/// (the server builds the indent from them alone), and a selection adds a whole-line range.
/// </summary>
public class FormatDocumentServiceBuildParamsTests
{
    private const string Uri = "file:///c:/repo/My%20Feature.feature";

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(8, true)]
    public void Sends_the_editor_tab_settings_as_the_formatting_options(int tabSize, bool insertSpaces)
    {
        var json = JObject.Parse(FormatDocumentService.BuildParams(
            Uri, isSelection: false, 0, 0, new GherkinFormattingOptions(tabSize, insertSpaces)));

        json["textDocument"]!["uri"]!.Value<string>().Should().Be(Uri);
        json["options"]!["tabSize"]!.Value<int>().Should().Be(tabSize);
        json["options"]!["insertSpaces"]!.Value<bool>().Should().Be(insertSpaces);
        json["range"].Should().BeNull();
    }

    [Fact]
    public void Adds_a_whole_line_range_for_a_selection()
    {
        var json = JObject.Parse(FormatDocumentService.BuildParams(
            Uri, isSelection: true, 3, 7, new GherkinFormattingOptions(2, false)));

        json["range"]!["start"]!["line"]!.Value<int>().Should().Be(3);
        json["range"]!["start"]!["character"]!.Value<int>().Should().Be(0);
        json["range"]!["end"]!["line"]!.Value<int>().Should().Be(7);
        json["options"]!["tabSize"]!.Value<int>().Should().Be(2);
        json["options"]!["insertSpaces"]!.Value<bool>().Should().BeFalse();
    }
}
