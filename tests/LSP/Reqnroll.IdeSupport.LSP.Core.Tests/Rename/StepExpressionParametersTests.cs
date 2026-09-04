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

    // ── IsEscaped / SlotLengthAt: backslash parity (issue #591) ────────────────────

    [Fact]
    public void IsEscaped_is_false_for_a_character_with_no_preceding_backslash()
    {
        StepExpressionParameters.IsEscaped("abc", 1).Should().BeFalse();
    }

    [Fact]
    public void IsEscaped_is_true_for_a_single_preceding_backslash()
    {
        StepExpressionParameters.IsEscaped(@"a\b", 2).Should().BeTrue();
    }

    [Fact]
    public void IsEscaped_is_false_for_two_preceding_backslashes()
    {
        // \\ is an escaped backslash; the character after it is NOT itself escaped.
        StepExpressionParameters.IsEscaped(@"a\\b", 3).Should().BeFalse();
    }

    [Fact]
    public void IsEscaped_is_true_for_three_preceding_backslashes()
    {
        StepExpressionParameters.IsEscaped(@"a\\\b", 4).Should().BeTrue();
    }

    [Fact]
    public void SlotLengthAt_recognizes_a_capturing_group_after_an_escaped_backslash()
    {
        // \\( is an escaped backslash followed by a genuine, unescaped capturing group -- a
        // naive single-character lookback would misclassify the '(' as escaped and return 0.
        const string s = @"a\\(\d+) cukes";
        StepExpressionParameters.SlotLengthAt(s, 3).Should().Be(@"(\d+)".Length);
    }

    // ── ReplaceSlotsWithValues (issue #591) ─────────────────────────────────────────

    [Fact]
    public void ReplaceSlotsWithValues_returns_the_expression_unchanged_when_it_has_no_slots()
    {
        StepExpressionParameters.ReplaceSlotsWithValues("no slots here", new List<string> { "unused" })
            .Should().Be("no slots here");
    }

    [Fact]
    public void ReplaceSlotsWithValues_substitutes_a_single_cucumber_placeholder()
    {
        StepExpressionParameters.ReplaceSlotsWithValues("I have {int} cukes", new List<string> { "5" })
            .Should().Be("I have 5 cukes");
    }

    [Fact]
    public void ReplaceSlotsWithValues_substitutes_multiple_slots_in_order()
    {
        StepExpressionParameters.ReplaceSlotsWithValues(
                @"I have (\d+) cukes and {int} apples", new List<string> { "5", "3" })
            .Should().Be("I have 5 cukes and 3 apples");
    }

    [Fact]
    public void ReplaceSlotsWithValues_drops_extra_slots_when_there_are_fewer_values_than_slots()
    {
        // Mirrors TryBuildViaRegex's original tolerance: a slot beyond the last available value
        // is dropped (its surrounding static text survives) rather than the call failing.
        StepExpressionParameters.ReplaceSlotsWithValues(
                @"I have (\d+) cukes and {int} apples", new List<string> { "5" })
            .Should().Be("I have 5 cukes and  apples");
    }

    [Fact]
    public void ReplaceSlotsWithValues_handles_adjacent_slots_with_no_static_text_between_them()
    {
        StepExpressionParameters.ReplaceSlotsWithValues("{int}{string}", new List<string> { "5", "\"x\"" })
            .Should().Be("5\"x\"");
    }
}
