using Reqnroll.IdeSupport.LSP.Core.Rename;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Rename;

/// <summary>
/// Issue #568: <see cref="StepExpressionParameters"/> is the slot-extraction/counting logic that
/// both <c>NewNameReconciler</c> and <c>FeatureStepTextBuilder</c> depend on for rename
/// correctness, with zero direct test coverage anywhere in the codebase.
/// </summary>
public class StepExpressionParametersTests
{
    // ── SlotLengthAt: Cucumber placeholders ────────────────────────────────────────

    [Fact]
    public void SlotLengthAt_returns_the_length_of_a_cucumber_placeholder()
    {
        StepExpressionParameters.SlotLengthAt("{int} cukes", 0).Should().Be(5);
    }

    [Fact]
    public void SlotLengthAt_returns_the_length_of_an_empty_placeholder()
    {
        StepExpressionParameters.SlotLengthAt("{} cukes", 0).Should().Be(2);
    }

    [Fact]
    public void SlotLengthAt_returns_zero_for_an_unterminated_placeholder()
    {
        StepExpressionParameters.SlotLengthAt("{int cukes", 0).Should().Be(0);
    }

    // ── SlotLengthAt: regex capturing groups ────────────────────────────────────────

    [Fact]
    public void SlotLengthAt_returns_the_length_of_a_simple_capturing_group()
    {
        StepExpressionParameters.SlotLengthAt(@"(\d+) cukes", 0).Should().Be(5);
    }

    [Fact]
    public void SlotLengthAt_returns_the_length_of_a_nested_capturing_group()
    {
        const string s = @"((\d+) or (\d+)) cukes";
        StepExpressionParameters.SlotLengthAt(s, 0).Should().Be(@"((\d+) or (\d+))".Length);
    }

    [Fact]
    public void SlotLengthAt_returns_zero_for_a_non_capturing_group()
    {
        StepExpressionParameters.SlotLengthAt("(?:abc)", 0).Should().Be(0);
    }

    [Theory]
    [InlineData("(?=abc)")]
    [InlineData("(?!abc)")]
    [InlineData("(?<=abc)")]
    public void SlotLengthAt_returns_zero_for_look_around_groups(string s)
    {
        StepExpressionParameters.SlotLengthAt(s, 0).Should().Be(0);
    }

    [Fact]
    public void SlotLengthAt_returns_zero_for_an_escaped_open_paren()
    {
        const string s = @"a\(b";
        StepExpressionParameters.SlotLengthAt(s, 2).Should().Be(0);
    }

    [Fact]
    public void SlotLengthAt_returns_zero_for_an_unclosed_group()
    {
        StepExpressionParameters.SlotLengthAt("(abc", 0).Should().Be(0);
    }

    [Fact]
    public void SlotLengthAt_returns_zero_for_a_plain_character()
    {
        StepExpressionParameters.SlotLengthAt("abc", 0).Should().Be(0);
    }

    // ── ExtractSlots ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractSlots_returns_no_slots_for_plain_text()
    {
        StepExpressionParameters.ExtractSlots("I have plain text").Should().BeEmpty();
    }

    [Fact]
    public void ExtractSlots_returns_cucumber_placeholders_in_order()
    {
        StepExpressionParameters.ExtractSlots("I have {int} cukes and {string} apples")
            .Should().Equal("{int}", "{string}");
    }

    [Fact]
    public void ExtractSlots_returns_regex_capturing_groups_in_order()
    {
        StepExpressionParameters.ExtractSlots(@"I have (\d+) cukes and (\d+) apples")
            .Should().Equal(@"(\d+)", @"(\d+)");
    }

    [Fact]
    public void ExtractSlots_handles_adjacent_slots_with_no_text_between_them()
    {
        StepExpressionParameters.ExtractSlots("{int}{string}").Should().Equal("{int}", "{string}");
    }

    // ── StaticSegments ─────────────────────────────────────────────────────────────

    [Fact]
    public void StaticSegments_of_plain_text_returns_a_single_segment_equal_to_the_whole_string()
    {
        StepExpressionParameters.StaticSegments("I have plain text").Should().Equal("I have plain text");
    }

    [Fact]
    public void StaticSegments_of_one_slot_returns_two_segments()
    {
        StepExpressionParameters.StaticSegments("I have {int} cukes").Should().Equal("I have ", " cukes");
    }

    [Fact]
    public void StaticSegments_of_two_slots_returns_three_segments()
    {
        StepExpressionParameters.StaticSegments("I have {int} cukes and {int} apples")
            .Should().Equal("I have ", " cukes and ", " apples");
    }

    [Fact]
    public void StaticSegments_of_adjacent_slots_returns_empty_segments_between_them()
    {
        StepExpressionParameters.StaticSegments("{int}{string}").Should().Equal("", "", "");
    }

    [Fact]
    public void StaticSegments_count_is_always_one_more_than_the_slot_count()
    {
        var expression = @"I have {int} cukes and (\d+) apples";

        var slots = StepExpressionParameters.ExtractSlots(expression);
        var segments = StepExpressionParameters.StaticSegments(expression);

        segments.Should().HaveCount(slots.Count + 1);
    }
}
