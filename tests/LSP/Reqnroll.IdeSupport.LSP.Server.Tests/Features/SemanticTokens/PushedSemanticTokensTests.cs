using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.SemanticTokens;

/// <summary>
/// Unit tests for <see cref="PushedSemanticTokens"/>: which part of the token set a push carries, given the set
/// last sent for the document. Token sets are built as absolute (line, character) tuples and encoded with
/// <see cref="SemanticTokensService.DeltaEncode"/>, so the tests exercise the same wire encoding the server sends.
/// </summary>
public class PushedSemanticTokensTests
{
    private const string Uri = "file:///c:/w/A.feature";

    private readonly PushedSemanticTokens _sut = new();

    private static (int Line, int Char, int Length, int TypeIdx, int ModBits) Tok(int line, int ch, int length = 4, int type = 0)
        => (line, ch, length, type, 0);

    private static int[] Encode(params (int Line, int Char, int Length, int TypeIdx, int ModBits)[] tokens)
        => SemanticTokensService.DeltaEncode(tokens.OrderBy(t => t.Line).ThenBy(t => t.Char).ToList()).ToArray();

    private static List<(int Line, int Char, int Length, int TypeIdx, int ModBits)> Decode(int[] data)
    {
        var result = new List<(int, int, int, int, int)>();
        int line = 0, character = 0;
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            if (data[i] == 0)
            {
                character += data[i + 1];
            }
            else
            {
                line += data[i];
                character = data[i + 1];
            }

            result.Add((line, character, data[i + 2], data[i + 3], data[i + 4]));
        }

        return result;
    }

    /// <summary>What the client does with a push: replace everything, replace one line range, or nothing.</summary>
    private static List<(int Line, int Char, int Length, int TypeIdx, int ModBits)> Apply(
        List<(int Line, int Char, int Length, int TypeIdx, int ModBits)> held, SemanticTokenPush push)
    {
        if (push.Unchanged)
            return held;

        if (!push.IsRanged)
            return Decode(push.Data);

        return held
            .Where(t => t.Line < push.StartLine || t.Line > push.EndLine)
            .Concat(Decode(push.Data))
            .OrderBy(t => t.Line).ThenBy(t => t.Char)
            .ToList();
    }

    [Fact]
    public void The_first_push_for_a_document_is_the_full_set()
    {
        var data = Encode(Tok(0, 0), Tok(2, 2));

        var push = _sut.Plan(Uri, data);

        push.IsRanged.Should().BeFalse();
        push.Unchanged.Should().BeFalse();
        push.Data.Should().Equal(data);
        push.TokenCount.Should().Be(2);
    }

    [Fact]
    public void Identical_tokens_need_no_push()
    {
        var data = Encode(Tok(0, 0), Tok(2, 2));
        _sut.Plan(Uri, data);

        var push = _sut.Plan(Uri, data);

        push.Unchanged.Should().BeTrue();
        push.TokenCount.Should().Be(2);
    }

    [Fact]
    public void A_change_on_one_line_sends_only_that_line()
    {
        _sut.Plan(Uri, Encode(Tok(0, 0), Tok(1, 0), Tok(2, 0, length: 4), Tok(3, 0)));

        var push = _sut.Plan(Uri, Encode(Tok(0, 0), Tok(1, 0), Tok(2, 0, length: 9), Tok(3, 0)));

        push.IsRanged.Should().BeTrue();
        push.StartLine.Should().Be(2);
        push.EndLine.Should().Be(2);
        Decode(push.Data).Should().Equal(Tok(2, 0, length: 9));
        push.TokenCount.Should().Be(4);
    }

    [Fact]
    public void A_token_added_to_a_line_sends_all_of_that_lines_tokens()
    {
        _sut.Plan(Uri, Encode(Tok(0, 0), Tok(1, 0), Tok(2, 0)));

        var push = _sut.Plan(Uri, Encode(Tok(0, 0), Tok(1, 0), Tok(1, 6), Tok(2, 0)));

        push.StartLine.Should().Be(1);
        push.EndLine.Should().Be(1);
        Decode(push.Data).Should().Equal(Tok(1, 0), Tok(1, 6));
        push.TokenCount.Should().Be(4);
    }

    [Fact]
    public void Removing_the_tokens_on_a_line_sends_an_empty_range_so_the_client_clears_it()
    {
        _sut.Plan(Uri, Encode(Tok(0, 0), Tok(1, 0), Tok(2, 0)));

        var push = _sut.Plan(Uri, Encode(Tok(0, 0), Tok(1, 0)));

        push.IsRanged.Should().BeTrue();
        push.StartLine.Should().Be(2);
        push.EndLine.Should().Be(2);
        push.Data.Should().BeEmpty();
        push.TokenCount.Should().Be(2);
    }

    [Fact]
    public void Inserting_a_line_moves_every_later_token_so_the_range_runs_to_the_last_token()
    {
        _sut.Plan(Uri, Encode(Tok(0, 0), Tok(1, 0), Tok(2, 0), Tok(3, 0)));

        // A new token on line 1 pushes the old line 1..3 tokens down to lines 2..4.
        var push = _sut.Plan(Uri, Encode(Tok(0, 0), Tok(1, 0, type: 5), Tok(2, 0), Tok(3, 0), Tok(4, 0)));

        push.StartLine.Should().Be(1);
        push.EndLine.Should().Be(4);
        Decode(push.Data).Should().Equal(Tok(1, 0, type: 5), Tok(2, 0), Tok(3, 0), Tok(4, 0));
        push.TokenCount.Should().Be(5);
    }

    [Fact]
    public void A_full_set_is_sent_again_after_the_resync_interval()
    {
        _sut.Plan(Uri, Encode(Tok(0, 0, length: 1)));

        for (var i = 1; i <= PushedSemanticTokens.FullResyncInterval; i++)
        {
            var ranged = _sut.Plan(Uri, Encode(Tok(0, 0, length: 1 + i)));
            ranged.IsRanged.Should().BeTrue($"push {i} after the full set is within the resync interval");
        }

        var resync = _sut.Plan(Uri, Encode(Tok(0, 0, length: 100)));
        var after = _sut.Plan(Uri, Encode(Tok(0, 0, length: 101)));

        resync.IsRanged.Should().BeFalse("the interval has been reached, so the client gets the whole set again");
        resync.Data.Should().Equal(Encode(Tok(0, 0, length: 100)));
        after.IsRanged.Should().BeTrue("the count starts again after a full push");
    }

    [Fact]
    public void Forgetting_a_document_makes_its_next_push_the_full_set()
    {
        var data = Encode(Tok(0, 0), Tok(1, 0));
        _sut.Plan(Uri, data);

        _sut.Forget(Uri);
        var push = _sut.Plan(Uri, data);

        push.IsRanged.Should().BeFalse();
        push.Unchanged.Should().BeFalse("the client may hold nothing for a document the server has forgotten");
        push.Data.Should().Equal(data);
    }

    [Fact]
    public void Documents_are_tracked_independently()
    {
        _sut.Plan(Uri, Encode(Tok(0, 0)));

        var other = _sut.Plan("file:///c:/w/B.feature", Encode(Tok(0, 0)));

        other.IsRanged.Should().BeFalse("a different document has no baseline yet");
        other.Unchanged.Should().BeFalse();
    }

    [Fact]
    public void Document_uris_are_compared_without_regard_to_case()
    {
        _sut.Plan("file:///c:/w/A.feature", Encode(Tok(0, 0)));

        var push = _sut.Plan("file:///C:/w/A.feature", Encode(Tok(0, 0)));

        push.Unchanged.Should().BeTrue();
    }

    [Fact]
    public void An_empty_document_is_tracked_like_any_other()
    {
        _sut.Plan(Uri, Encode());

        _sut.Plan(Uri, Encode()).Unchanged.Should().BeTrue();
        var push = _sut.Plan(Uri, Encode(Tok(0, 0)));

        push.IsRanged.Should().BeTrue();
        push.StartLine.Should().Be(0);
        push.EndLine.Should().Be(0);
        Decode(push.Data).Should().Equal(Tok(0, 0));
    }

    [Fact]
    public void An_edited_line_is_pushed_even_when_its_tokens_are_the_same_as_before()
    {
        // The client moved its tokens through the edit and may have dropped the one on the edited line, so
        // that line's tokens are sent although the new set equals the old one.
        var tokens = Encode(Tok(0, 0), Tok(1, 0), Tok(2, 0), Tok(3, 0));
        _sut.Plan(Uri, tokens, "Feature: a\nGiven b\nAnd c\nThen d");

        var push = _sut.Plan(Uri, tokens, "Feature: a\nGiven b\nAnd cc\nThen d");

        push.IsRanged.Should().BeTrue();
        push.StartLine.Should().Be(2);
        push.EndLine.Should().Be(2);
        Decode(push.Data).Should().Equal(Tok(2, 0));
        push.TokenCount.Should().Be(4);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void The_edited_line_is_found_whatever_the_line_endings_are(string lineEnding)
    {
        string Text(string third) => string.Join(lineEnding, "Feature: a", "Given b", third, "Then d");

        var tokens = Encode(Tok(0, 0), Tok(1, 0), Tok(2, 0), Tok(3, 0));
        _sut.Plan(Uri, tokens, Text("And c"));

        var push = _sut.Plan(Uri, tokens, Text("And cc"));

        push.IsRanged.Should().BeTrue();
        push.StartLine.Should().Be(2);
        push.EndLine.Should().Be(2);
    }

    [Fact]
    public void An_edit_that_starts_between_the_halves_of_a_crlf_belongs_to_the_line_the_break_ends()
    {
        var tokens = Encode(Tok(0, 0), Tok(1, 0));
        _sut.Plan(Uri, tokens, "a\rb");

        // The break after "a" becomes "\r\n": the first differing character is the "\n", right after the "\r".
        var push = _sut.Plan(Uri, tokens, "a\r\nb");

        push.StartLine.Should().Be(0);
        push.EndLine.Should().Be(0);
    }

    [Fact]
    public void An_edit_on_a_line_without_tokens_needs_no_push()
    {
        var tokens = Encode(Tok(0, 0), Tok(2, 0));
        _sut.Plan(Uri, tokens, "Feature: a\n  a description\nGiven b");

        var push = _sut.Plan(Uri, tokens, "Feature: a\n  a longer description\nGiven b");

        push.Unchanged.Should().BeTrue("neither the server nor the client holds a token on the edited line");
    }

    [Fact]
    public void The_same_text_and_tokens_need_no_push()
    {
        var tokens = Encode(Tok(0, 0), Tok(1, 0));
        _sut.Plan(Uri, tokens, "Feature: a\nGiven b");

        _sut.Plan(Uri, tokens, "Feature: a\nGiven b").Unchanged.Should().BeTrue();
    }

    [Fact]
    public void The_pushed_range_covers_both_the_edited_lines_and_the_changed_tokens()
    {
        _sut.Plan(Uri, Encode(Tok(0, 0), Tok(1, 0), Tok(2, 0), Tok(3, 0), Tok(4, 0), Tok(5, 0)),
            "l0\nl1\nl2\nl3\nl4\nl5");

        // Line 1 is edited with its token unchanged; the token on line 4 changes (a consequence elsewhere).
        var push = _sut.Plan(Uri, Encode(Tok(0, 0), Tok(1, 0), Tok(2, 0), Tok(3, 0), Tok(4, 0, length: 9), Tok(5, 0)),
            "l0\nL1\nl2\nl3\nl4\nl5");

        push.StartLine.Should().Be(1);
        push.EndLine.Should().Be(4);
        Decode(push.Data).Should().Equal(Tok(1, 0), Tok(2, 0), Tok(3, 0), Tok(4, 0, length: 9));
    }

    [Fact]
    public void Deleting_a_line_pushes_the_line_where_the_text_joined()
    {
        _sut.Plan(Uri, Encode(Tok(0, 0), Tok(2, 0)), "a\nb\nc");

        // "b" is deleted: "c" moves from line 2 to line 1, so the tokens differ as well.
        var push = _sut.Plan(Uri, Encode(Tok(0, 0), Tok(1, 0)), "a\nc");

        push.StartLine.Should().Be(1);
        push.EndLine.Should().BeGreaterThanOrEqualTo(1);
        Decode(push.Data).Should().Contain(Tok(1, 0));
    }

    [Fact]
    public void A_push_after_one_without_text_is_the_full_set_because_the_edited_lines_are_unknown()
    {
        var tokens = Encode(Tok(0, 0), Tok(1, 0));
        _sut.Plan(Uri, tokens);

        var push = _sut.Plan(Uri, tokens, "Feature: a\nGiven b");

        push.IsRanged.Should().BeFalse();
        push.Unchanged.Should().BeFalse();
        push.Data.Should().Equal(tokens);
    }

    [Fact]
    public void Applying_each_push_to_what_the_client_holds_always_reproduces_the_current_tokens_when_the_client_shifts_its_tokens()
    {
        // The client moves its tokens through each edit before the next push arrives. Whatever it dropped or
        // resized on the edited lines must be restored by the push, even where the new set equals the old one.
        var lines = Enumerable.Range(0, 30).Select(i => $"Given step {i}").ToList();
        List<(int Line, int Char, int Length, int TypeIdx, int ModBits)> TokensFor(List<string> text)
            => text.Select((l, i) => Tok(i, 0, length: 5, type: 1)).ToList();

        var held = Decode(_sut.Plan(Uri, Encode(TokensFor(lines).ToArray()), string.Join('\n', lines)).Data);
        var random = new Random(20261120);

        for (var step = 0; step < 300; step++)
        {
            // The edit: change the text of a line (a user retyping a keyword), and the client drops that
            // line's token because the edit replaced it; the server's token set for the new text is unchanged.
            var line = random.Next(lines.Count);
            lines[line] = $"Given edited {step}";
            held = held.Where(t => t.Line != line).ToList();

            var current = TokensFor(lines);
            var push = _sut.Plan(Uri, Encode(current.ToArray()), string.Join('\n', lines));
            held = Apply(held, push);

            held.Should().Equal(current, $"the client must hold the server's tokens after step {step}");
        }
    }

    [Fact]
    public void Applying_each_push_to_what_the_client_holds_always_reproduces_the_current_tokens()
    {
        // A client that applies every push in order must end up holding exactly the server's token set, for any
        // sequence of edits (changed, added, removed tokens and lines shifted up or down), across resyncs.
        var random = new Random(20261119);
        var current = new List<(int Line, int Char, int Length, int TypeIdx, int ModBits)>();
        for (var line = 0; line < 40; line++)
            current.Add(Tok(line, 0, length: 1 + line % 7, type: line % 10));

        var held = Decode(_sut.Plan(Uri, Encode(current.ToArray())).Data);
        var rangedPushes = 0;
        var fullPushes = 0;

        for (var step = 0; step < 400; step++)
        {
            current = Mutate(current, random);
            var push = _sut.Plan(Uri, Encode(current.ToArray()));

            held = Apply(held, push);

            held.Should().Equal(
                current.OrderBy(t => t.Line).ThenBy(t => t.Char).ToList(),
                $"the client must hold the server's tokens after step {step}");
            push.TokenCount.Should().Be(current.Count);
            if (push.IsRanged) rangedPushes++;
            if (!push.IsRanged && !push.Unchanged) fullPushes++;
        }

        rangedPushes.Should().BeGreaterThan(100, "most edits are small and should be sent as ranges");
        fullPushes.Should().BeGreaterThan(0, "the periodic full push must have been exercised");
    }

    private static List<(int Line, int Char, int Length, int TypeIdx, int ModBits)> Mutate(
        List<(int Line, int Char, int Length, int TypeIdx, int ModBits)> tokens, Random random)
    {
        var next = tokens.ToList();
        switch (random.Next(6))
        {
            case 0 when next.Count > 0: // change one token
                var change = random.Next(next.Count);
                next[change] = (next[change].Line, next[change].Char, 1 + random.Next(20), random.Next(10), 0);
                break;
            case 1: // add a token on an existing or new line
                var line = random.Next(60);
                var ch = random.Next(30);
                if (!next.Any(t => t.Line == line && t.Char == ch))
                    next.Add(Tok(line, ch, 1 + random.Next(9), random.Next(10)));
                break;
            case 2 when next.Count > 0: // remove a token
                next.RemoveAt(random.Next(next.Count));
                break;
            case 3: // insert a line: everything from a line onwards moves down
                var insertAt = random.Next(60);
                next = next.Select(t => t.Line >= insertAt ? (t.Line + 1, t.Char, t.Length, t.TypeIdx, t.ModBits) : t).ToList();
                break;
            case 4: // delete a line: its tokens go and everything after it moves up
                var deleteAt = random.Next(60);
                next = next.Where(t => t.Line != deleteAt)
                    .Select(t => t.Line > deleteAt ? (t.Line - 1, t.Char, t.Length, t.TypeIdx, t.ModBits) : t)
                    .ToList();
                break;
            default: // no change
                break;
        }

        return next;
    }
}
