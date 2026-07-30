using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.HookFeatureCodeLens;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.HookCodeLens;

/// <summary>
/// Client-side mapping of the server's <c>textDocument/codeLens</c> response for a <c>.feature</c>
/// file into <c>HookFeatureLensEntry</c> records
/// (<see cref="HookFeatureCodeLensService.ParseItems"/>) — the same seam-and-test shape as
/// <c>GoToHooksService.MapResult</c>.
/// </summary>
/// <remarks>
/// The 5th command argument (<c>alwaysShowPicker</c>) is the only thing distinguishing the two lens
/// kinds a <c>Scenario:</c> line carries, and it is what each data point provider filters on. It was
/// parsed here but silently dropped further downstream, which is what made only one of the two
/// lenses render (issue #400) — so it is asserted explicitly below.
/// </remarks>
public class HookFeatureCodeLensServiceParseItemsTests
{
    private static JObject Lens(int line, string title, params object[] arguments) => new()
    {
        ["range"]   = new JObject
        {
            ["start"] = new JObject { ["line"] = line, ["character"] = 0 },
            ["end"]   = new JObject { ["line"] = line, ["character"] = 0 },
        },
        ["command"] = new JObject
        {
            ["title"]     = title,
            ["command"]   = "reqnroll.goToHooks",
            ["arguments"] = new JArray(arguments),
        },
    };

    private const string Uri = "file:///c:/w/A.feature";

    [Fact]
    public void An_empty_response_maps_to_no_entries()
    {
        HookFeatureCodeLensService.ParseItems(new JArray()).Should().BeEmpty();
    }

    [Fact]
    public void An_own_level_lens_is_mapped_with_its_nav_target_and_flags()
    {
        var result = HookFeatureCodeLensService.ParseItems(
            new JArray(Lens(1, "2 hooks", Uri, 1, 0, true)));

        var entry = result.Should().ContainSingle().Subject;
        entry.Line.Should().Be(1);
        entry.Title.Should().Be("2 hooks");
        entry.NavLine.Should().Be(1);
        entry.NavChar.Should().Be(0);
        entry.OwnLevelOnly.Should().BeTrue();
        entry.AlwaysShowPicker.Should().BeFalse("a 4-argument lens is the own-level kind");
    }

    [Fact]
    public void A_step_hooks_lens_is_distinguished_by_its_fifth_argument()
    {
        var result = HookFeatureCodeLensService.ParseItems(
            new JArray(Lens(1, "2 step hooks", Uri, 2, 4, true, true)));

        var entry = result.Should().ContainSingle().Subject;
        entry.AlwaysShowPicker.Should().BeTrue();
        entry.NavLine.Should().Be(2, "the step-hooks lens navigates to the scenario's first step");
        entry.NavChar.Should().Be(4);
    }

    [Fact]
    public void Both_lens_kinds_on_one_line_are_kept_as_separate_entries()
    {
        // The regression behind issue #400: these two must survive as distinct entries so the two
        // data point providers can each claim their own.
        var result = HookFeatureCodeLensService.ParseItems(new JArray(
            Lens(1, "1 hook",       Uri, 1, 0, true),
            Lens(1, "2 step hooks", Uri, 2, 4, true, true)));

        result.Should().HaveCount(2);
        result.Should().ContainSingle(e => !e.AlwaysShowPicker).Which.Title.Should().Be("1 hook");
        result.Should().ContainSingle(e =>  e.AlwaysShowPicker).Which.Title.Should().Be("2 step hooks");
    }

    [Fact]
    public void Lenses_on_different_lines_are_all_mapped()
    {
        var result = HookFeatureCodeLensService.ParseItems(new JArray(
            Lens(0, "1 hook",       Uri, 0, 0, true),
            Lens(3, "2 hooks",      Uri, 3, 0, true),
            Lens(3, "1 step hook",  Uri, 4, 4, true, true)));

        result.Select(e => e.Line).Should().BeEquivalentTo(new[] { 0, 3, 3 });
    }

    [Fact]
    public void An_entry_without_a_usable_range_is_skipped()
    {
        var noRange = new JObject
        {
            ["command"] = new JObject { ["title"] = "1 hook", ["arguments"] = new JArray(Uri, 1, 0, true) },
        };

        HookFeatureCodeLensService.ParseItems(new JArray(noRange)).Should().BeEmpty();
    }

    [Fact]
    public void A_non_object_array_element_is_skipped_rather_than_throwing()
    {
        var result = HookFeatureCodeLensService.ParseItems(
            new JArray("nonsense", Lens(1, "1 hook", Uri, 1, 0, true)));

        result.Should().ContainSingle().Which.Title.Should().Be("1 hook");
    }

    [Fact]
    public void A_lens_without_a_command_still_maps_with_safe_defaults()
    {
        // Defensive: a lens the server emitted without a command must not take the whole response
        // down, and must not be mistaken for the step-hooks kind.
        var commandless = new JObject
        {
            ["range"] = new JObject
            {
                ["start"] = new JObject { ["line"] = 5, ["character"] = 0 },
                ["end"]   = new JObject { ["line"] = 5, ["character"] = 0 },
            },
        };

        var entry = HookFeatureCodeLensService.ParseItems(new JArray(commandless)).Should().ContainSingle().Subject;
        entry.Line.Should().Be(5);
        entry.Title.Should().BeEmpty();
        entry.NavLine.Should().Be(5, "the nav target falls back to the display line");
        entry.OwnLevelOnly.Should().BeFalse();
        entry.AlwaysShowPicker.Should().BeFalse();
    }

    [Fact]
    public void Missing_trailing_arguments_fall_back_rather_than_throwing()
    {
        // An older/mismatched server payload with only the uri argument.
        var entry = HookFeatureCodeLensService.ParseItems(
            new JArray(Lens(2, "1 hook", Uri))).Should().ContainSingle().Subject;

        entry.NavLine.Should().Be(2);
        entry.NavChar.Should().Be(0);
        entry.OwnLevelOnly.Should().BeFalse();
        entry.AlwaysShowPicker.Should().BeFalse();
    }
}
