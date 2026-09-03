using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Protocol.Documents;

/// <summary>
/// Issue #568: <see cref="GherkinRangeExtensions"/> (the OmniSharp-typed conversion surface every
/// LSP response position/range passes through) has zero direct test references anywhere in the
/// codebase.
/// </summary>
public class GherkinRangeExtensionsTests
{
    private const string Text = "Feature: F\nScenario: S\n\tGiven a step\n";

    private static LspTextSnapshot MakeSnapshot(string text = Text) => new("doc", 1, text);

    [Fact]
    public void ToLspStartPosition_returns_the_zero_based_line_and_character_of_the_range_start()
    {
        var snapshot = MakeSnapshot();
        var startOffset = Text.IndexOf("Given", StringComparison.Ordinal);
        var range = GherkinRange.FromPoint(snapshot, startOffset, "Given a step".Length);

        var position = range.ToLspStartPosition();

        position.Line.Should().Be(2);
        position.Character.Should().Be(1); // after the leading tab
    }

    [Fact]
    public void ToLspEndPosition_returns_the_zero_based_line_and_character_of_the_range_end()
    {
        var snapshot = MakeSnapshot();
        var startOffset = Text.IndexOf("Given", StringComparison.Ordinal);
        var range = GherkinRange.FromPoint(snapshot, startOffset, "Given a step".Length);

        var position = range.ToLspEndPosition();

        position.Line.Should().Be(2);
        position.Character.Should().Be(1 + "Given a step".Length);
    }

    [Fact]
    public void ToLspRange_combines_the_start_and_end_positions()
    {
        var snapshot = MakeSnapshot();
        var startOffset = Text.IndexOf("Scenario", StringComparison.Ordinal);
        var range = GherkinRange.FromPoint(snapshot, startOffset, "Scenario: S".Length);

        var lspRange = range.ToLspRange();

        lspRange.Start.Should().Be(range.ToLspStartPosition());
        lspRange.End.Should().Be(range.ToLspEndPosition());
    }

    [Fact]
    public void ToOffset_converts_a_line_and_character_back_to_the_matching_absolute_offset()
    {
        var snapshot = MakeSnapshot();
        var expectedOffset = Text.IndexOf("Given", StringComparison.Ordinal);

        var offset = snapshot.ToOffset(line: 2, character: 1);

        offset.Should().Be(expectedOffset);
    }

    [Fact]
    public void ToOffset_and_the_ranges_start_position_round_trip()
    {
        var snapshot = MakeSnapshot();
        var startOffset = Text.IndexOf("Given", StringComparison.Ordinal);
        var range = GherkinRange.FromPoint(snapshot, startOffset, "Given a step".Length);

        var (line, character) = range.StartLinePosition;
        var roundTripped = snapshot.ToOffset(line, character);

        roundTripped.Should().Be(startOffset);
    }

    [Fact]
    public void ToOffset_clamps_to_the_snapshot_length_when_the_line_exceeds_the_last_line()
    {
        var snapshot = MakeSnapshot();

        var offset = snapshot.ToOffset(line: 999, character: 0);

        offset.Should().Be(snapshot.Length);
    }
}
