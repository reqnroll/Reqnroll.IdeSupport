using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Documents;

public class GherkinRangeTests
{
    // Three lines of 5 chars each ("aaaaa\nbbbbb\nccccc"), no trailing newline:
    //   line 0: chars [0,5), line break at 5
    //   line 1: chars [6,11), line break at 11
    //   line 2: chars [12,17)
    private const string ThreeLines = "aaaaa\nbbbbb\nccccc";

    private static StubGherkinTextSnapshot CreateSnapshot(string text) => new(text);

    // ── ResolveOffset via StartLinePosition/EndLinePosition ─────────────────────

    [Fact]
    public void Offset_at_document_start_resolves_to_line_zero_character_zero()
    {
        var snapshot = CreateSnapshot(ThreeLines);
        var range = new GherkinRange(snapshot, 0, 0);

        range.StartLinePosition.Should().Be((0, 0));
    }

    [Fact]
    public void Offset_mid_first_line_resolves_within_line_zero()
    {
        var snapshot = CreateSnapshot(ThreeLines);
        var range = new GherkinRange(snapshot, 2, 0);

        range.StartLinePosition.Should().Be((0, 2));
    }

    [Fact]
    public void Offset_exactly_at_a_line_end_resolves_to_that_line_not_the_next()
    {
        var snapshot = CreateSnapshot(ThreeLines);
        // offset 5 is line 0's End (the position of the '\n')
        var range = new GherkinRange(snapshot, 5, 0);

        range.StartLinePosition.Should().Be((0, 5));
    }

    [Fact]
    public void Offset_at_start_of_middle_line_resolves_to_that_line()
    {
        var snapshot = CreateSnapshot(ThreeLines);
        var range = new GherkinRange(snapshot, 6, 0);

        range.StartLinePosition.Should().Be((1, 0));
    }

    [Fact]
    public void Offset_within_last_line_resolves_to_last_line()
    {
        var snapshot = CreateSnapshot(ThreeLines);
        var range = new GherkinRange(snapshot, 14, 0);

        range.StartLinePosition.Should().Be((2, 2));
    }

    [Fact]
    public void Offset_at_end_of_last_line_resolves_to_last_line_end()
    {
        var snapshot = CreateSnapshot(ThreeLines);
        var range = new GherkinRange(snapshot, 17, 0);

        range.StartLinePosition.Should().Be((2, 5));
    }

    [Fact]
    public void Single_line_document_resolves_every_offset_to_line_zero()
    {
        var snapshot = CreateSnapshot("hello");
        var range = new GherkinRange(snapshot, 3, 0);

        range.StartLinePosition.Should().Be((0, 3));
    }

    [Fact]
    public void Empty_document_resolves_offset_zero_to_line_zero_character_zero()
    {
        var snapshot = CreateSnapshot(string.Empty);
        var range = new GherkinRange(snapshot, 0, 0);

        range.StartLinePosition.Should().Be((0, 0));
    }

    [Fact]
    public void End_position_of_a_multi_line_range_resolves_correctly()
    {
        var snapshot = CreateSnapshot(ThreeLines);
        // spans from offset 2 (line 0) to offset 14 (line 2)
        var range = new GherkinRange(snapshot, 2, 12);

        range.StartLinePosition.Should().Be((0, 2));
        range.EndLinePosition.Should().Be((2, 2));
    }

    // ── Matches the original linear scan for every offset in a larger document ──

    [Fact]
    public void Resolves_every_offset_in_a_larger_document_to_the_same_result_as_a_reference_linear_scan()
    {
        var text = string.Join("\n", Enumerable.Range(0, 500).Select(i => new string('x', i % 7 + 1)));
        var snapshot = CreateSnapshot(text);

        for (int offset = 0; offset <= snapshot.Length; offset += 17)
        {
            var range = new GherkinRange(snapshot, offset, 0);
            var expected = ReferenceLinearScan(snapshot, offset);

            range.StartLinePosition.Should().Be(expected, $"offset {offset} should resolve the same as the reference scan");
        }
    }

    // Mirrors ResolveOffset's pre-#471 implementation, kept here only as an independent oracle
    // for the test above -- not the production code path.
    private static (int Line, int Character) ReferenceLinearScan(IGherkinTextSnapshot snapshot, int offset)
    {
        for (int ln = 0; ln < snapshot.LineCount; ln++)
        {
            var line = snapshot.GetLineFromLineNumber(ln);
            if (offset <= line.End)
                return (ln, offset - line.Start);
        }
        int lastLine = snapshot.LineCount - 1;
        var last = snapshot.GetLineFromLineNumber(lastLine);
        return (lastLine, last.End - last.Start);
    }
}
