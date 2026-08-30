using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="DriveLetterUriNormalizer"/> upper-cases the drive letter of <c>file:///</c> URIs
/// wherever they appear in a parsed JSON-RPC body, so server-emitted locations (OmniSharp's
/// <c>DocumentUri</c> always lowercases the drive letter) match VS's own document identity
/// (see issue #65).
/// </summary>
public class DriveLetterUriNormalizerTests
{
    [Fact]
    public void Uppercases_a_lowercase_drive_letter_in_a_string_value()
    {
        var body = new JObject { ["uri"] = "file:///c:/Users/dev/Steps.cs" };

        var changed = DriveLetterUriNormalizer.NormalizeInPlace(body);

        changed.Should().BeTrue();
        body["uri"]!.Value<string>().Should().Be("file:///C:/Users/dev/Steps.cs");
    }

    [Fact]
    public void Leaves_an_already_uppercase_drive_letter_untouched()
    {
        var body = new JObject { ["uri"] = "file:///C:/Users/dev/Steps.cs" };

        var changed = DriveLetterUriNormalizer.NormalizeInPlace(body);

        changed.Should().BeFalse();
        body["uri"]!.Value<string>().Should().Be("file:///C:/Users/dev/Steps.cs");
    }

    [Fact]
    public void Normalizes_uris_nested_inside_arrays_and_objects()
    {
        var body = new JObject
        {
            ["result"] = new JArray(
                new JObject
                {
                    ["uri"] = "file:///d:/Repo/Foo.feature",
                    ["range"] = new JObject { ["start"] = new JObject { ["line"] = 1 } },
                })
        };

        var changed = DriveLetterUriNormalizer.NormalizeInPlace(body);

        changed.Should().BeTrue();
        body["result"]![0]!["uri"]!.Value<string>().Should().Be("file:///D:/Repo/Foo.feature");
    }

    [Fact]
    public void Normalizes_a_uri_used_as_an_object_property_name()
    {
        // WorkspaceEdit.changes is a map keyed by URI, not just a value.
        var body = new JObject
        {
            ["result"] = new JObject
            {
                ["changes"] = new JObject
                {
                    ["file:///e:/Repo/Steps.cs"] = new JArray(),
                },
            },
        };

        var changed = DriveLetterUriNormalizer.NormalizeInPlace(body);

        changed.Should().BeTrue();
        var changesObj = (JObject)body["result"]!["changes"]!;
        changesObj.Properties().Should().ContainSingle(p => p.Name == "file:///E:/Repo/Steps.cs");
    }

    [Fact]
    public void Ignores_non_file_uris_and_strings_without_a_drive_letter()
    {
        var body = new JObject
        {
            ["a"] = "https://example.com/c:/not-a-path",
            ["b"] = "file:///Users/dev/no-drive-letter",
            ["c"] = "just some text",
        };

        var changed = DriveLetterUriNormalizer.NormalizeInPlace(body);

        changed.Should().BeFalse();
    }

    [Fact]
    public void Returns_false_for_a_body_with_no_uris()
    {
        var body = new JObject { ["jsonrpc"] = "2.0", ["method"] = "window/logMessage" };

        DriveLetterUriNormalizer.NormalizeInPlace(body).Should().BeFalse();
    }
}
