using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.SemanticTokens;

/// <summary>
/// Unit tests for <see cref="SemanticTokenService.ResolveOverlaps"/>, the token-overlap-splitting
/// algorithm underlying <c>Encode</c> (issue #279) -- exercised directly against synthetic
/// (Line, Char, Length, TypeIdx, ModBits) tuples rather than through Gherkin tag collection, so
/// boundary cases in the interval math can be verified in isolation.
/// </summary>
public class SemanticTokenServiceResolveOverlapsTests
{
    private static (int Line, int Char, int Length, int TypeIdx, int ModBits) Entry(
        int line, int ch, int length, int typeIdx = 0, int modBits = 0) => (line, ch, length, typeIdx, modBits);

    [Fact]
    public void No_overlap_returns_entries_unchanged()
    {
        var entries = new List<(int, int, int, int, int)> { Entry(0, 0, 5, 1), Entry(0, 10, 5, 2) };

        var resolved = SemanticTokenService.ResolveOverlaps(entries);

        resolved.Should().Equal(entries);
    }

    [Fact]
    public void Single_inner_token_in_the_middle_produces_gap_before_and_after()
    {
        // "I enter {string} as the username"   DefinedStep col 0..33, StepParameter col 8..13
        var entries = new List<(int, int, int, int, int)>
        {
            Entry(0, 0, 33, 1), // outer: DefinedStep
            Entry(0, 8, 5, 2),  // inner: StepParameter
        };

        var resolved = SemanticTokenService.ResolveOverlaps(entries);

        resolved.Should().Equal(
            Entry(0, 0, 8, 1),   // gap before
            Entry(0, 8, 5, 2),   // inner token itself
            Entry(0, 13, 20, 1)  // gap after
        );
    }

    [Fact]
    public void Inner_token_at_the_very_start_has_no_leading_gap()
    {
        var entries = new List<(int, int, int, int, int)>
        {
            Entry(0, 0, 20, 1),
            Entry(0, 0, 5, 2), // inner starts exactly where outer starts
        };

        var resolved = SemanticTokenService.ResolveOverlaps(entries);

        resolved.Should().Equal(
            Entry(0, 0, 5, 2),
            Entry(0, 5, 15, 1)
        );
    }

    [Fact]
    public void Inner_token_at_the_very_end_has_no_trailing_gap()
    {
        var entries = new List<(int, int, int, int, int)>
        {
            Entry(0, 0, 20, 1),
            Entry(0, 15, 5, 2), // inner ends exactly where outer ends (15 + 5 == 20)
        };

        var resolved = SemanticTokenService.ResolveOverlaps(entries);

        resolved.Should().Equal(
            Entry(0, 0, 15, 1),
            Entry(0, 15, 5, 2)
        );
    }

    [Fact]
    public void Multiple_inner_tokens_are_all_preserved_with_gaps_between()
    {
        // "I have {int} and {int} cukes" -- two StepParameter tokens inside one DefinedStep span.
        var entries = new List<(int, int, int, int, int)>
        {
            Entry(0, 0, 30, 1),  // outer
            Entry(0, 8, 2, 2),   // first param
            Entry(0, 18, 2, 2),  // second param
        };

        var resolved = SemanticTokenService.ResolveOverlaps(entries);

        resolved.Should().Equal(
            Entry(0, 0, 8, 1),
            Entry(0, 8, 2, 2),
            Entry(0, 10, 8, 1),
            Entry(0, 18, 2, 2),
            Entry(0, 20, 10, 1)
        );
    }

    [Fact]
    public void A_token_starting_exactly_at_the_previous_span_end_is_not_treated_as_contained()
    {
        // spanEnd for the first entry is 10; a second entry starting AT 10 is adjacent, not
        // overlapping -- the "kCh >= spanEnd" boundary check must exclude it.
        var entries = new List<(int, int, int, int, int)>
        {
            Entry(0, 0, 10, 1),
            Entry(0, 10, 5, 2),
        };

        var resolved = SemanticTokenService.ResolveOverlaps(entries);

        resolved.Should().Equal(entries);
    }

    [Fact]
    public void Entries_on_different_lines_are_never_treated_as_overlapping()
    {
        var entries = new List<(int, int, int, int, int)>
        {
            Entry(0, 0, 100, 1), // would "contain" the next entry by column alone
            Entry(1, 5, 5, 2),   // but it's on a different line
        };

        var resolved = SemanticTokenService.ResolveOverlaps(entries);

        resolved.Should().Equal(entries);
    }

    [Fact]
    public void Empty_input_returns_empty_output()
    {
        SemanticTokenService.ResolveOverlaps(new List<(int, int, int, int, int)>()).Should().BeEmpty();
    }
}
