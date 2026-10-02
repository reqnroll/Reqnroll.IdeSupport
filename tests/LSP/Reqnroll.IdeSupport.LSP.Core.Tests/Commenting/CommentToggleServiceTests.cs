#nullable enable

using Reqnroll.IdeSupport.LSP.Core.Commenting;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Commenting;

public class CommentToggleServiceTests
{
    private static CommentToggleService CreateSut() => new();

    // ── Toggle ON (comment uncommented lines) ─────────────────────────────

    [Fact]
    public void Single_uncommented_line_adds_hash_and_space()
    {
        var result = CreateSut().ToggleComment("Given a step\n", rangeStartLine: 0, rangeEndLine: 0);
        result.Edits.Should().ContainSingle()
            .Which.NewText.Should().Be("# Given a step");
    }

    [Fact]
    public void Multiple_uncommented_lines_all_get_hash_and_space()
    {
        var text = "Given step1\nWhen step2\nThen step3\n";
        var result = CreateSut().ToggleComment(text, 0, 2);
        result.Edits.Should().HaveCount(3);
        result.Edits[0].NewText.Should().Be("# Given step1");
        result.Edits[1].NewText.Should().Be("# When step2");
        result.Edits[2].NewText.Should().Be("# Then step3");
    }

    [Fact]
    public void Empty_line_gets_only_hash_without_space()
    {
        var result = CreateSut().ToggleComment("\n", 0, 0);
        result.Edits.Should().ContainSingle()
            .Which.NewText.Should().Be("#");
    }

    // ── Toggle OFF (uncomment all-commented lines) ────────────────────────

    [Fact]
    public void Single_commented_line_removes_hash_and_space()
    {
        var result = CreateSut().ToggleComment("# Given a step\n", 0, 0);
        result.Edits.Should().ContainSingle()
            .Which.NewText.Should().Be("Given a step");
    }

    [Fact]
    public void Single_hash_only_line_becomes_empty()
    {
        var result = CreateSut().ToggleComment("#\n", 0, 0);
        result.Edits.Should().ContainSingle()
            .Which.NewText.Should().Be("");
    }

    [Fact]
    public void All_commented_lines_all_get_uncommented()
    {
        var text = "# Given step1\n# When step2\n# Then step3\n";
        var result = CreateSut().ToggleComment(text, 0, 2);
        result.Edits.Should().HaveCount(3);
        result.Edits[0].NewText.Should().Be("Given step1");
        result.Edits[1].NewText.Should().Be("When step2");
        result.Edits[2].NewText.Should().Be("Then step3");
    }

    [Fact]
    public void Lines_with_leading_spaces_then_hash_are_uncommented()
    {
        var result = CreateSut().ToggleComment("    # indented comment\n", 0, 0);
        result.Edits.Should().ContainSingle()
            .Which.NewText.Should().Be("    indented comment");
    }

    // ── Mixed state (some commented, some not) → toggle all to comment ────

    [Fact]
    public void Mixed_commented_and_uncommented_lines_all_get_commented()
    {
        var text = "Given step1\n# When step2\nThen step3\n";
        var result = CreateSut().ToggleComment(text, 0, 2);
        result.Edits.Should().HaveCount(3);
        result.Edits[0].NewText.Should().Be("# Given step1");
        result.Edits[1].NewText.Should().Be("# # When step2");  // nested hash!
        result.Edits[2].NewText.Should().Be("# Then step3");
    }

    // ── Range in the middle of the document ───────────────────────────────

    [Fact]
    public void Only_lines_in_range_are_affected()
    {
        var text = "Feature: F\nGiven step1\nWhen step2\nThen step3\n";
        // comment only lines 1-2 (Given, When)
        var result = CreateSut().ToggleComment(text, rangeStartLine: 1, rangeEndLine: 2);
        result.Edits.Should().HaveCount(2);
        result.Edits[0].NewText.Should().Be("# Given step1");
        result.Edits[1].NewText.Should().Be("# When step2");
    }

    // ── Uncomment mixed → like mixed state, all turn into comment ─────────

    [Fact]
    public void Mixed_state_toggles_all_to_comment()
    {
        var text = "# Given step1\nWhen step2\n# Then step3\n";
        var result = CreateSut().ToggleComment(text, 0, 2);
        result.Edits.Should().HaveCount(3);
        result.Edits[0].NewText.Should().Be("# # Given step1");
        result.Edits[1].NewText.Should().Be("# When step2");
        result.Edits[2].NewText.Should().Be("# # Then step3");
    }

    // ── Explicit Comment mode (VS Edit.CommentSelection, issue #747) ──────

    [Fact]
    public void Comment_mode_adds_another_hash_even_when_every_line_is_commented()
    {
        var text = "# Given step1\n# When step2\n";
        var result = CreateSut().ToggleComment(text, 0, 1, CommentToggleMode.Comment);
        result.Edits.Should().HaveCount(2);
        result.Edits[0].NewText.Should().Be("# # Given step1");
        result.Edits[1].NewText.Should().Be("# # When step2");
    }

    [Fact]
    public void Comment_mode_comments_uncommented_lines()
    {
        var result = CreateSut().ToggleComment("Given a step\n", 0, 0, CommentToggleMode.Comment);
        result.Edits.Should().ContainSingle()
            .Which.NewText.Should().Be("# Given a step");
    }

    // ── Explicit Uncomment mode (VS Edit.UncommentSelection, issue #747) ──

    [Fact]
    public void Uncomment_mode_uncomments_only_commented_lines_in_a_mixed_selection()
    {
        var text = "# Given step1\nWhen step2\n    # Then step3\n";
        var result = CreateSut().ToggleComment(text, 0, 2, CommentToggleMode.Uncomment);
        result.Edits.Should().HaveCount(2);
        result.Edits[0].Should().Be(new GherkinCommentEdit(0, 0, "Given step1"));
        result.Edits[1].Should().Be(new GherkinCommentEdit(2, 2, "    Then step3"));
    }

    [Fact]
    public void Uncomment_mode_removes_only_one_level_of_nested_comments()
    {
        var result = CreateSut().ToggleComment("# # Given a step\n", 0, 0, CommentToggleMode.Uncomment);
        result.Edits.Should().ContainSingle()
            .Which.NewText.Should().Be("# Given a step");
    }

    [Fact]
    public void Uncomment_mode_on_uncommented_lines_produces_no_edits()
    {
        var result = CreateSut().ToggleComment("Given step1\n\nWhen step2\n", 0, 2, CommentToggleMode.Uncomment);
        result.Edits.Should().BeEmpty();
    }

    [Fact]
    public void Toggle_is_the_default_mode()
    {
        var text = "# Given step1\n";
        CreateSut().ToggleComment(text, 0, 0).Edits
            .Should().BeEquivalentTo(CreateSut().ToggleComment(text, 0, 0, CommentToggleMode.Toggle).Edits);
    }
}
