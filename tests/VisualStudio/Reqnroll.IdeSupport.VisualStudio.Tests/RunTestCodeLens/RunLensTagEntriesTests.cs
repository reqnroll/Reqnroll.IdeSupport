using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.RunTestCodeLens;

/// <summary>
/// Serialises the tests that read or bump <c>RunTestCodeLensRedirect</c>'s process-wide revision counters,
/// so a refresh in one class cannot change a description another class is comparing.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RunTestCodeLensRevisionCollection
{
    public const string Name = "RunTestCodeLensRevision";
}

/// <summary>
/// Unit tests for <see cref="RunLensTagEntries.ToTagEntry"/> — the Run-CodeLens-specific
/// revision-key formatting — as encoded by <see cref="FeatureCodeLensTaggerProvider.EncodeElementDescription"/>
/// into the shared <see cref="LineElementDescription"/> envelope (keyed off <see cref="RunTestLensLocation"/>
/// rather than the resolved <see cref="RunTestTargetEntry"/>, since the tagger does not resolve
/// targets at all). Mirrors <c>HookElementDescriptionTests</c>'s shape.
/// </summary>
/// <remarks>
/// The revision component is load-bearing rather than cosmetic: <c>LineKeyedCodeLensTagger{TEntry}</c>
/// reuses a tag instance for as long as its <c>ElementDescription</c> is unchanged, so if the
/// encoding failed to vary with a line's scenario identity, a renamed/re-kinded scenario would never
/// get its data point recreated.
/// </remarks>
[Collection(RunTestCodeLensRevisionCollection.Name)]
public class RunLensTagEntriesTests
{
    private static RunTestLensLocation Location(int line = 1, string key = "Scenario|AddTwoNumbers") =>
        new(line, key);

    private static string Encode(int line, IEnumerable<RunTestLensLocation> entries) =>
        FeatureCodeLensTaggerProvider.EncodeElementDescription(line, entries.Select(RunLensTagEntries.ToTagEntry));

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4_096)]
    public void Encode_then_TryDecode_recovers_the_line(int line)
    {
        var encoded = Encode(line, new[] { Location(line: line) });

        LineElementDescription.TryDecode(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(line);
    }

    // ── Revision component: must vary with content, else lenses never refresh ──

    [Fact]
    public void The_encoding_changes_when_the_scenario_name_changes()
    {
        var before = Encode(1, new[] { Location(key: "Scenario|AddTwoNumbers") });
        var after  = Encode(1, new[] { Location(key: "Scenario|AddThreeNumbers") });

        after.Should().NotBe(before);
    }

    [Fact]
    public void The_encoding_changes_when_the_scenario_kind_changes()
    {
        // A plain Scenario re-authored as a Scenario Outline (or vice versa) must be treated as a
        // different tag, since the CodeLens label text ("Run Scenario" vs "Run Scenarios") depends
        // on it.
        var before = Encode(1, new[] { Location(key: "Scenario|AddTwoNumbers") });
        var after  = Encode(1, new[] { Location(key: "Scenario Outline|AddTwoNumbers") });

        after.Should().NotBe(before);
    }

    [Fact]
    public void The_encoding_is_stable_for_unchanged_content()
    {
        var entries = new[] { Location() };

        Encode(1, entries)
            .Should().Be(Encode(1, entries));
    }

    [Fact]
    public void The_encoding_changes_when_targets_are_invalidated()
    {
        // A lens that resolved to no target before the build must be re-created after the refresh that
        // follows it, so the descriptor has to differ even though the scenario itself is unchanged.
        var entries = new[] { Location() };
        var before = Encode(1, entries);

        RunTestCodeLensRedirect.InvalidateAll();

        Encode(1, entries).Should().NotBe(before);
    }

    [Fact]
    public void Two_different_lines_encode_differently_even_with_identical_scenario_identity()
    {
        var line1 = Encode(1, new[] { Location(line: 1) });
        var line9 = Encode(9, new[] { Location(line: 9) });

        line9.Should().NotBe(line1);
    }

    [Fact]
    public void TryDecode_of_a_line_with_no_tag_still_yields_the_line()
    {
        var encoded = Encode(3, System.Array.Empty<RunTestLensLocation>());

        LineElementDescription.TryDecode(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(3);
    }
}
