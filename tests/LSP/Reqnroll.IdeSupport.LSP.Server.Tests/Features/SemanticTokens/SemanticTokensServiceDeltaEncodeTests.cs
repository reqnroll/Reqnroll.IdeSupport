using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.SemanticTokens;

/// <summary>
/// Unit tests for <see cref="SemanticTokensService.DeltaEncode"/>, the delta-encoding arithmetic
/// underlying <c>Encode</c> (issue #593) -- exercised directly against an already-ordered list of
/// synthetic (Line, Char, Length, TypeIdx, ModBits) tuples, mirroring
/// <see cref="SemanticTokenServiceResolveOverlapsTests"/>'s approach for the neighboring
/// overlap-resolution phase, so the delta math can be verified without a full parse.
/// </summary>
public class SemanticTokensServiceDeltaEncodeTests
{
    private static (int Line, int Char, int Length, int TypeIdx, int ModBits) Entry(
        int line, int ch, int length, int typeIdx = 0, int modBits = 0) => (line, ch, length, typeIdx, modBits);

    [Fact]
    public void Empty_input_produces_empty_output()
    {
        var result = SemanticTokensService.DeltaEncode(new List<(int, int, int, int, int)>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void First_token_is_encoded_relative_to_the_document_origin()
    {
        var result = SemanticTokensService.DeltaEncode(new List<(int, int, int, int, int)>
        {
            Entry(3, 5, 10, 1, 2)
        });

        result.Should().Equal(3, 5, 10, 1, 2);
    }

    [Fact]
    public void A_second_token_on_the_same_line_gets_a_char_delta_relative_to_the_previous_start()
    {
        var result = SemanticTokensService.DeltaEncode(new List<(int, int, int, int, int)>
        {
            Entry(0, 5, 3, 1, 0),
            Entry(0, 12, 4, 2, 0),
        });

        result.Should().Equal(
            0, 5, 3, 1, 0,   // first token: absolute
            0, 7, 4, 2, 0);  // deltaLine=0, deltaChar=12-5=7
    }

    [Fact]
    public void A_token_on_a_new_line_gets_an_absolute_char_not_a_delta_from_the_previous_line()
    {
        var result = SemanticTokensService.DeltaEncode(new List<(int, int, int, int, int)>
        {
            Entry(0, 20, 3, 1, 0),
            Entry(2, 4, 5, 2, 0),
        });

        result.Should().Equal(
            0, 20, 3, 1, 0,
            2, 4, 5, 2, 0);  // deltaLine=2-0=2 -> char is absolute (4), not 4-20
    }

    [Fact]
    public void Three_tokens_chain_their_deltas_from_each_previous_token_in_order()
    {
        var result = SemanticTokensService.DeltaEncode(new List<(int, int, int, int, int)>
        {
            Entry(1, 0, 4, 1, 0),
            Entry(1, 10, 4, 2, 0),
            Entry(4, 2, 6, 3, 1),
        });

        result.Should().Equal(
            1, 0, 4, 1, 0,
            0, 10, 4, 2, 0,  // same line as previous: deltaChar = 10-0
            3, 2, 6, 3, 1);  // new line: deltaLine = 4-1, char absolute
    }
}
