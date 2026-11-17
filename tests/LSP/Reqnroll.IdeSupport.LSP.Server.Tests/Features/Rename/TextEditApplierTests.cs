using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Rename;

public class TextEditApplierTests
{
    private static TextEdit Edit(int startLine, int startChar, int endLine, int endChar, string newText) =>
        new() { Range = new LspRange(new Position(startLine, startChar), new Position(endLine, endChar)), NewText = newText };

    [Fact]
    public void No_edits_returns_the_text_unchanged()
    {
        TextEditApplier.Apply("Feature: F\n", Array.Empty<TextEdit>()).Should().Be("Feature: F\n");
    }

    [Fact]
    public void Replaces_a_range_on_one_line()
    {
        TextEditApplier.Apply("    Given old step\n", new[] { Edit(0, 10, 0, 18, "new step") })
            .Should().Be("    Given new step\n");
    }

    [Fact]
    public void Applies_several_edits_that_all_refer_to_the_original_text()
    {
        const string text = "Given old step\nWhen old step\n";

        var result = TextEditApplier.Apply(text, new[]
        {
            Edit(0, 6, 0, 14, "a much longer new step"),
            Edit(1, 5, 1, 13, "x"),
        });

        result.Should().Be("Given a much longer new step\nWhen x\n");
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Counts_every_kind_of_line_break_as_one_line(string lineBreak)
    {
        var text = $"Feature: F{lineBreak}Scenario: S{lineBreak}  Given old{lineBreak}";

        TextEditApplier.Apply(text, new[] { Edit(2, 8, 2, 11, "new") })
            .Should().Be($"Feature: F{lineBreak}Scenario: S{lineBreak}  Given new{lineBreak}");
    }

    [Fact]
    public void A_character_past_the_end_of_the_line_means_the_end_of_the_line()
    {
        TextEditApplier.Apply("Given old\nnext\n", new[] { Edit(0, 6, 0, 99, "new") })
            .Should().Be("Given new\nnext\n");
    }

    [Fact]
    public void Several_inserts_at_one_position_keep_their_order()
    {
        TextEditApplier.Apply("ac", new[] { Edit(0, 1, 0, 1, "b"), Edit(0, 1, 0, 1, "B") })
            .Should().Be("abBc");
    }

    [Fact]
    public void An_edit_on_a_line_the_text_does_not_have_is_rejected()
    {
        TextEditApplier.Apply("one line\n", new[] { Edit(5, 0, 5, 1, "x") }).Should().BeNull();
    }

    [Fact]
    public void Overlapping_edits_are_rejected()
    {
        TextEditApplier.Apply("abcdef", new[] { Edit(0, 1, 0, 4, "x"), Edit(0, 3, 0, 5, "y") }).Should().BeNull();
    }

    [Fact]
    public void An_edit_whose_end_comes_before_its_start_is_rejected()
    {
        TextEditApplier.Apply("abcdef", new[] { Edit(0, 4, 0, 1, "x") }).Should().BeNull();
    }
}
