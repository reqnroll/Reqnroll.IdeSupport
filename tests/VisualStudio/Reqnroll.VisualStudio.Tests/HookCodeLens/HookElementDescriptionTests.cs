using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.HookCodeLens;

/// <summary>
/// Unit tests for <see cref="HookCodeLensTaggerProvider.EncodeElementDescription"/> (the
/// hook-feature-specific revision-key formatting) and <see cref="LineElementDescription"/> (the
/// shared pipe-delimited envelope it's built on) — the string the in-process tagger smuggles
/// through <c>ICodeLensDescriptor.ElementDescription</c> to the out-of-process data point providers
/// (issue #372), reworked in issue #400 to identify a <em>line</em> rather than an individual lens,
/// and split into a shared envelope plus per-feature formatting in the issue #262 follow-up refactor.
/// </summary>
/// <remarks>
/// The revision component is load-bearing rather than cosmetic: <c>LineKeyedCodeLensTagger{TEntry}</c>
/// reuses a tag instance for as long as its <c>ElementDescription</c> is unchanged, so if the
/// encoding failed to vary with a line's lens content, a changed hook-match count would never reach
/// the editor — the same class of silent-staleness bug as issue #400 itself.
/// </remarks>
public class HookElementDescriptionTests
{
    private static HookFeatureLensEntry Entry(
        int line = 1,
        string title = "1 hook",
        int navLine = 1,
        int navChar = 0,
        bool ownLevelOnly = true,
        bool alwaysShowPicker = false) =>
        new(line, title, navLine, navChar, ownLevelOnly, alwaysShowPicker);

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4_096)]
    public void Encode_then_TryDecode_recovers_the_line(int line)
    {
        var encoded = HookCodeLensTaggerProvider.EncodeElementDescription(line, new[] { Entry(line: line) });

        LineElementDescription.TryDecode(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(line);
    }

    [Fact]
    public void Encode_of_a_line_carrying_both_lens_kinds_still_decodes_to_that_one_line()
    {
        // The whole point of the issue #400 rework: one descriptor per line, shared by the
        // own-level and step-hooks providers.
        var encoded = HookCodeLensTaggerProvider.EncodeElementDescription(1, new[]
        {
            Entry(title: "1 hook",       navLine: 1, navChar: 0, alwaysShowPicker: false),
            Entry(title: "2 step hooks", navLine: 2, navChar: 4, alwaysShowPicker: true),
        });

        LineElementDescription.TryDecode(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(1);
    }

    // ── Revision component: must vary with content, else lenses never refresh ──

    [Fact]
    public void The_encoding_changes_when_a_count_changes()
    {
        var before = HookCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(title: "1 hook") });
        var after  = HookCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(title: "2 hooks") });

        after.Should().NotBe(before);
    }

    [Fact]
    public void The_encoding_changes_when_a_lens_kind_is_added_to_the_line()
    {
        var ownLevelOnly = HookCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(title: "1 hook") });
        var bothKinds    = HookCodeLensTaggerProvider.EncodeElementDescription(1, new[]
        {
            Entry(title: "1 hook",       alwaysShowPicker: false),
            Entry(title: "2 step hooks", alwaysShowPicker: true),
        });

        bothKinds.Should().NotBe(ownLevelOnly);
    }

    [Fact]
    public void The_encoding_changes_when_only_the_nav_target_moves()
    {
        // A step-hooks lens whose first step shifted still needs a new descriptor, because the
        // data point resolves its nav target from the entry rather than the descriptor.
        var before = HookCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(navLine: 2, alwaysShowPicker: true) });
        var after  = HookCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(navLine: 7, alwaysShowPicker: true) });

        after.Should().NotBe(before);
    }

    [Fact]
    public void The_encoding_is_stable_for_unchanged_content()
    {
        // Tag reuse depends on this: an unrelated line changing must not churn this line's tag.
        var entries = new[] { Entry(title: "1 hook"), Entry(title: "2 step hooks", alwaysShowPicker: true) };

        HookCodeLensTaggerProvider.EncodeElementDescription(1, entries)
            .Should().Be(HookCodeLensTaggerProvider.EncodeElementDescription(1, entries));
    }

    [Fact]
    public void The_encoding_does_not_depend_on_the_order_entries_arrive_in()
    {
        // The server's CodeLens[] ordering is not contractual; a reordered-but-equivalent response
        // must not look like a change and churn the tag.
        var ownLevel  = Entry(title: "1 hook",       navLine: 1, navChar: 0, alwaysShowPicker: false);
        var stepHooks = Entry(title: "2 step hooks", navLine: 2, navChar: 4, alwaysShowPicker: true);

        HookCodeLensTaggerProvider.EncodeElementDescription(1, new[] { ownLevel, stepHooks })
            .Should().Be(HookCodeLensTaggerProvider.EncodeElementDescription(1, new[] { stepHooks, ownLevel }));
    }

    [Fact]
    public void Two_different_lines_encode_differently_even_with_identical_lens_content()
    {
        var line1 = HookCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(line: 1) });
        var line9 = HookCodeLensTaggerProvider.EncodeElementDescription(9, new[] { Entry(line: 9) });

        line9.Should().NotBe(line1);
    }

    // ── Malformed / hostile input ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-separator-present")]
    [InlineData("|")]
    [InlineData("|revision-only")]
    [InlineData("notanumber|revision")]
    public void TryDecode_rejects_input_that_carries_no_usable_line(string? elementDescription)
    {
        LineElementDescription.TryDecode(elementDescription, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_of_a_line_with_no_entries_still_yields_the_line()
    {
        // Defensive: an empty revision is structurally valid, and the providers must not reject
        // the descriptor outright — GetDataAsync resolves emptiness from a live fetch instead.
        var encoded = HookCodeLensTaggerProvider.EncodeElementDescription(3, System.Array.Empty<HookFeatureLensEntry>());

        LineElementDescription.TryDecode(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(3);
    }
}
