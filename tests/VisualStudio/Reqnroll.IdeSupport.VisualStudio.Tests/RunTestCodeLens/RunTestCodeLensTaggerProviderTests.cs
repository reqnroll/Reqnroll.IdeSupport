using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.RunTestCodeLens;

/// <summary>
/// Unit tests for <see cref="RunTestCodeLensTaggerProvider.EncodeElementDescription"/> — the
/// Run-CodeLens-specific revision-key formatting built on the shared
/// <see cref="LineElementDescription"/> envelope (issue #262 follow-up refactor; re-scoped by issue
/// #495 to key off <see cref="RunTestLensLocation"/> instead of the resolved
/// <see cref="RunTestTargetEntry"/>, since the tagger no longer resolves targets at all — see
/// <see cref="RunTestCodeLensTaggerProvider.FetchAsync"/>). Mirrors <c>HookElementDescriptionTests</c>'s
/// shape for <c>HookCodeLensTaggerProvider</c>'s counterpart.
/// </summary>
/// <remarks>
/// The revision component is load-bearing rather than cosmetic: <c>LineKeyedCodeLensTagger{TEntry}</c>
/// reuses a tag instance for as long as its <c>ElementDescription</c> is unchanged, so if the
/// encoding failed to vary with a line's scenario identity, a renamed/re-kinded scenario would never
/// get its data point recreated.
/// </remarks>
public class RunTestCodeLensTaggerProviderTests
{
    private static RunTestLensLocation Location(int line = 1, string key = "Scenario|AddTwoNumbers") =>
        new(line, key);

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4_096)]
    public void Encode_then_TryDecode_recovers_the_line(int line)
    {
        var encoded = RunTestCodeLensTaggerProvider.EncodeElementDescription(line, new[] { Location(line: line) });

        LineElementDescription.TryDecode(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(line);
    }

    // ── Revision component: must vary with content, else lenses never refresh ──

    [Fact]
    public void The_encoding_changes_when_the_scenario_name_changes()
    {
        var before = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Location(key: "Scenario|AddTwoNumbers") });
        var after  = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Location(key: "Scenario|AddThreeNumbers") });

        after.Should().NotBe(before);
    }

    [Fact]
    public void The_encoding_changes_when_the_scenario_kind_changes()
    {
        // A plain Scenario re-authored as a Scenario Outline (or vice versa) must be treated as a
        // different tag, since the CodeLens label text ("Run Scenario" vs "Run Scenarios") depends
        // on it.
        var before = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Location(key: "Scenario|AddTwoNumbers") });
        var after  = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Location(key: "Scenario Outline|AddTwoNumbers") });

        after.Should().NotBe(before);
    }

    [Fact]
    public void The_encoding_is_stable_for_unchanged_content()
    {
        var entries = new[] { Location() };

        RunTestCodeLensTaggerProvider.EncodeElementDescription(1, entries)
            .Should().Be(RunTestCodeLensTaggerProvider.EncodeElementDescription(1, entries));
    }

    [Fact]
    public void Two_different_lines_encode_differently_even_with_identical_scenario_identity()
    {
        var line1 = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Location(line: 1) });
        var line9 = RunTestCodeLensTaggerProvider.EncodeElementDescription(9, new[] { Location(line: 9) });

        line9.Should().NotBe(line1);
    }

    [Fact]
    public void TryDecode_of_a_line_with_no_tag_still_yields_the_line()
    {
        var encoded = RunTestCodeLensTaggerProvider.EncodeElementDescription(3, System.Array.Empty<RunTestLensLocation>());

        LineElementDescription.TryDecode(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(3);
    }
}
