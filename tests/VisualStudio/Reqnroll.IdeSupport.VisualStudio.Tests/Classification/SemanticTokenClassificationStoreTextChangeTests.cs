using System.Collections.Generic;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.Classification;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.Classification;

/// <summary>
/// Moving the cached tokens through edits made to the text, so they keep describing the text until the server's next
/// push arrives. Line and character numbers are 0-based.
/// </summary>
public class SemanticTokenClassificationStoreTextChangeTests
{
    private static readonly string Key = SemanticTokenClassificationStore.NormalizeKey(@"c:\w\A.feature")!;

    private static ClassifiedToken Token(int line, int start, int length, string type = "reqnroll.keyword") =>
        new(line, start, length, type);

    private static SemanticTokenClassificationStore StoreWith(params ClassifiedToken[] tokens)
    {
        var store = new SemanticTokenClassificationStore();
        store.SetTokens(Key, new List<ClassifiedToken>(tokens));
        return store;
    }

    private static (int Line, int Start, int Length)[] TokensOf(SemanticTokenClassificationStore store)
    {
        store.TryGetTokens(Key, out var tokens).Should().BeTrue();
        var result = new (int, int, int)[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
            result[i] = (tokens[i].Line, tokens[i].StartChar, tokens[i].Length);
        return result;
    }

    private static TokenTextChange Change(int oldStartLine, int oldStartChar, int oldEndLine, int oldEndChar, int newEndLine, int newEndChar) =>
        new(oldStartLine, oldStartChar, oldEndLine, oldEndChar, newEndLine, newEndChar);

    [Fact]
    public void A_line_added_at_the_end_of_a_line_moves_the_tokens_below_it_down_and_leaves_the_ones_above()
    {
        var store = StoreWith(Token(1, 2, 5), Token(2, 2, 4), Token(3, 4, 5), Token(4, 0, 3));

        // Enter at the end of line 2, which is 10 characters long.
        store.ApplyTextChanges(Key, new[] { Change(2, 10, 2, 10, 3, 0) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 2, 5), (2, 2, 4), (4, 4, 5), (5, 0, 3));
    }

    [Fact]
    public void A_deleted_line_drops_its_tokens_and_moves_the_tokens_below_it_up()
    {
        var store = StoreWith(Token(1, 2, 5), Token(2, 2, 4), Token(2, 8, 3), Token(3, 4, 5), Token(5, 0, 3));

        // Line 2, including its line break, is deleted.
        store.ApplyTextChanges(Key, new[] { Change(2, 0, 3, 0, 2, 0) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 2, 5), (2, 4, 5), (4, 0, 3));
    }

    [Fact]
    public void Typing_inside_a_word_lengthens_its_token_and_moves_the_tokens_after_it_on_the_line()
    {
        var store = StoreWith(Token(1, 2, 6), Token(1, 10, 3), Token(2, 2, 4));

        // One character typed at column 5, inside the token that spans columns 2 to 8.
        store.ApplyTextChanges(Key, new[] { Change(1, 5, 1, 5, 1, 6) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 2, 7), (1, 11, 3), (2, 2, 4));
    }

    [Fact]
    public void Deleting_a_character_inside_a_word_shortens_its_token()
    {
        var store = StoreWith(Token(1, 2, 6), Token(1, 10, 3));

        store.ApplyTextChanges(Key, new[] { Change(1, 5, 1, 6, 1, 5) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 2, 5), (1, 9, 3));
    }

    [Fact]
    public void Typing_right_after_a_token_does_not_extend_it()
    {
        var store = StoreWith(Token(1, 2, 6), Token(1, 10, 3));

        // The token ends at column 8; a character typed there is new text that the server has not coloured yet.
        store.ApplyTextChanges(Key, new[] { Change(1, 8, 1, 8, 1, 9) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 2, 6), (1, 11, 3));
    }

    [Fact]
    public void Typing_right_before_a_token_moves_it()
    {
        var store = StoreWith(Token(1, 2, 6));

        store.ApplyTextChanges(Key, new[] { Change(1, 2, 1, 2, 1, 3) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 3, 6));
    }

    [Fact]
    public void A_line_break_typed_in_the_middle_of_a_line_moves_what_follows_to_the_new_line()
    {
        var store = StoreWith(Token(1, 0, 4), Token(1, 5, 4), Token(1, 10, 3), Token(1, 14, 2), Token(2, 2, 5));

        // Enter at column 6: the token spanning it (columns 5 to 9) is dropped, the ones after move to the new line
        // and shift left by the six characters that stayed behind.
        store.ApplyTextChanges(Key, new[] { Change(1, 6, 1, 6, 2, 0) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 0, 4), (2, 4, 3), (2, 8, 2), (3, 2, 5));
    }

    [Fact]
    public void Joining_two_lines_moves_the_tokens_of_the_second_onto_the_first()
    {
        var store = StoreWith(Token(1, 2, 4), Token(2, 3, 5), Token(3, 0, 2));

        // The line break at the end of line 1 (10 characters long) is deleted.
        store.ApplyTextChanges(Key, new[] { Change(1, 10, 2, 0, 1, 10) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 2, 4), (1, 13, 5), (2, 0, 2));
    }

    [Fact]
    public void An_edit_across_the_start_of_a_token_drops_it()
    {
        var store = StoreWith(Token(1, 4, 6), Token(1, 12, 2));

        // Columns 2 to 6 replaced by three characters: the token at columns 4 to 10 is cut into.
        store.ApplyTextChanges(Key, new[] { Change(1, 2, 1, 6, 1, 5) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 11, 2));
    }

    [Fact]
    public void Deleting_exactly_a_token_drops_it()
    {
        var store = StoreWith(Token(1, 4, 6), Token(1, 12, 2));

        store.ApplyTextChanges(Key, new[] { Change(1, 4, 1, 10, 1, 4) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 6, 2));
    }

    [Fact]
    public void Several_edits_in_one_event_apply_later_ones_first()
    {
        var store = StoreWith(Token(1, 0, 2), Token(1, 4, 2), Token(1, 12, 2));

        // A character inserted at column 8 and another at column 2 of the text before the edit.
        store.ApplyTextChanges(Key, new[] { Change(1, 8, 1, 8, 1, 9), Change(1, 2, 1, 2, 1, 3) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 0, 2), (1, 5, 2), (1, 14, 2));
    }

    [Fact]
    public void Several_edits_that_add_lines_move_the_tokens_below_all_of_them()
    {
        var store = StoreWith(Token(1, 0, 2), Token(3, 0, 2), Token(6, 0, 2));

        // A line break at the end of line 4, then one at the end of line 2.
        store.ApplyTextChanges(Key, new[] { Change(4, 5, 4, 5, 5, 0), Change(2, 5, 2, 5, 3, 0) }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 0, 2), (4, 0, 2), (8, 0, 2));
    }

    [Fact]
    public void An_edit_after_every_token_changes_nothing_and_raises_nothing()
    {
        var store = StoreWith(Token(1, 0, 2), Token(2, 0, 2));
        store.TokensChanged += (_, _, _) => throw new Xunit.Sdk.XunitException("no event expected");

        store.ApplyTextChanges(Key, new[] { Change(5, 0, 5, 0, 5, 3) }).Should().BeFalse();

        TokensOf(store).Should().Equal((1, 0, 2), (2, 0, 2));
    }

    [Fact]
    public void Typing_after_the_last_token_of_a_line_leaves_the_token_set_as_it_is()
    {
        var store = StoreWith(Token(1, 0, 2), Token(1, 4, 3), Token(2, 0, 2));
        store.TryGetTokens(Key, out var before);
        store.TokensChanged += (_, _, _) => throw new Xunit.Sdk.XunitException("no event expected");

        store.ApplyTextChanges(Key, new[] { Change(1, 12, 1, 12, 1, 13) }).Should().BeFalse();

        store.TryGetTokens(Key, out var after);
        after.Should().BeSameAs(before, "the tokens of the later lines do not move, so nothing is copied");
    }

    [Fact]
    public void A_file_without_tokens_is_left_alone()
    {
        var store = new SemanticTokenClassificationStore();

        store.ApplyTextChanges(Key, new[] { Change(0, 0, 0, 0, 1, 0) }).Should().BeFalse();

        store.TryGetTokens(Key, out _).Should().BeFalse();
    }

    [Fact]
    public void An_edit_that_adds_a_line_raises_TokensChanged_from_its_line_to_the_end_of_the_document()
    {
        var store = StoreWith(Token(1, 0, 2), Token(4, 0, 2));
        var raised = new List<(string Key, int First, int Last)>();
        store.TokensChanged += (key, first, last) => raised.Add((key, first, last));

        store.ApplyTextChanges(Key, new[] { Change(2, 3, 2, 3, 3, 0) });

        raised.Should().ContainSingle().Which.Should().Be((Key, 2, int.MaxValue));
    }

    [Fact]
    public void An_edit_within_a_line_raises_TokensChanged_for_just_that_line()
    {
        var store = StoreWith(Token(2, 0, 2), Token(2, 6, 2), Token(4, 0, 2));
        var raised = new List<(string Key, int First, int Last)>();
        store.TokensChanged += (key, first, last) => raised.Add((key, first, last));

        store.ApplyTextChanges(Key, new[] { Change(2, 3, 2, 3, 2, 4) });

        raised.Should().ContainSingle().Which.Should().Be((Key, 2, 2));
    }

    [Fact]
    public void A_ranged_push_after_an_edit_replaces_the_moved_tokens_by_line()
    {
        // After the edit moved the tokens, the server's push (in the coordinates of the new text) lands on them.
        var store = StoreWith(Token(1, 0, 2), Token(2, 0, 2), Token(3, 0, 2));
        store.ApplyTextChanges(Key, new[] { Change(1, 5, 1, 5, 2, 0) });

        store.ApplyRange(Key, 2, 2, new[] { Token(2, 1, 4, "reqnroll.tag") }).Should().BeTrue();

        TokensOf(store).Should().Equal((1, 0, 2), (2, 1, 4), (3, 0, 2), (4, 0, 2));
    }
}
