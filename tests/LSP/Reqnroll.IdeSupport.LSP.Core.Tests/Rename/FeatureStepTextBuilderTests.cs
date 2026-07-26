#nullable enable

using System.Text.RegularExpressions;
using Reqnroll.IdeSupport.LSP.Core.Rename;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Rename;

public class FeatureStepTextBuilderTests
{
    [Fact]
    public void Build_single_param_replaces_value_preserving_concrete_value()
    {
        var result = FeatureStepTextBuilder.Build(
            "the first no is (.*)", "the first number is (.*)",
            new Regex("^the first number is (.*)$"),
            "the first number is 50");

        result.Should().Be("the first no is 50");
    }

    [Fact]
    public void Build_multiple_params_preserve_values_in_order()
    {
        var result = FeatureStepTextBuilder.Build(
            "the (.*) was (\\d+)", "the (.*) is (\\d+)",
            new Regex("^the (.*) is (\\d+)$"),
            "the foo is 42");

        result.Should().Be("the foo was 42");
    }

    [Fact]
    public void Build_cucumber_expression_param_preserves_value()
    {
        var result = FeatureStepTextBuilder.Build(
            "I ate {int} cukes", "I have {int} cukes",
            new Regex("^I have (\\d+) cukes$"),
            "I have 42 cukes");

        result.Should().Be("I ate 42 cukes");
    }

    [Fact]
    public void Build_no_parameters_returns_new_expression()
    {
        var result = FeatureStepTextBuilder.Build(
            "hello there", "hello world",
            new Regex("^hello world$"),
            "hello world");

        result.Should().Be("hello there");
    }

    [Fact]
    public void Build_static_text_does_not_align_returns_new_expression()
    {
        var result = FeatureStepTextBuilder.Build(
            "new (.*)", "first (.*)",
            new Regex("^first (.*)$"),
            "does not match");

        result.Should().Be("new (.*)");
    }

    [Fact]
    public void Build_null_stepText_returns_new_expression()
    {
        var result = FeatureStepTextBuilder.Build(
            "hi (.*)", "hello (.*)",
            new Regex("^hello (.*)$"), null);

        result.Should().Be("hi (.*)");
    }

    [Fact]
    public void Build_empty_stepText_returns_new_expression()
    {
        var result = FeatureStepTextBuilder.Build(
            "hi (.*)", "hello (.*)",
            new Regex("^hello (.*)$"), "");

        result.Should().Be("hi (.*)");
    }

    // ── Scenario Outline placeholder: the parameter value is "<secondNumber>", which does not
    //    match the binding's numeric regex. It must be preserved, not replaced by "{int}". ─────

    [Fact]
    public void Build_preserves_scenario_outline_placeholder()
    {
        var result = FeatureStepTextBuilder.Build(
            "the second no is {int}", "the second number is {int}",
            new Regex("^the second number is (-?\\d+)$"),
            "the second number is <secondNumber>");

        result.Should().Be("the second no is <secondNumber>");
    }

    [Fact]
    public void Build_preserves_quoted_string_argument_including_quotes()
    {
        // The {string} regex captures the inner text without quotes; static-segment substitution
        // keeps the quoted value verbatim.
        var result = FeatureStepTextBuilder.Build(
            "the two numbers {string} summed", "the two numbers {string} added",
            new Regex("^the two numbers (?:\"([^\"]*)\"|'([^']*)') added$"),
            "the two numbers 'are' added");

        result.Should().Be("the two numbers 'are' summed");
    }

    // ── Regex fallback: static text contains regex syntax (a non-capturing group) that does not
    //    appear verbatim in the step, so static-segment alignment fails and the regex path runs. ─

    [Fact]
    public void Build_falls_back_to_regex_when_static_text_is_regex_syntax()
    {
        var result = FeatureStepTextBuilder.Build(
            "I consumed (\\d+) (?:green )?bananas", "I ate (\\d+) (?:green )?bananas",
            new Regex("^I ate (\\d+) (?:green )?bananas$"),
            "I ate 5 green bananas");

        result.Should().Be("I consumed 5 (?:green )?bananas");
    }

    [Fact]
    public void Build_cucumber_multi_param_preserves_values()
    {
        var result = FeatureStepTextBuilder.Build(
            "I ate {int} {string}", "I have {int} {string}",
            new Regex("^I have (\\d+) (\\w+)$"),
            "I have 42 apples");

        result.Should().Be("I ate 42 apples");
    }

    [Fact]
    public void Build_no_oldExpression_falls_back_to_regex()
    {
        var result = FeatureStepTextBuilder.Build(
            "hi (.*)", oldExpression: null,
            new Regex("^hello (.*)$"),
            "hello world");

        result.Should().Be("hi world");
    }

    [Fact]
    public void Build_recognizes_unescaped_capturing_group_after_an_escaped_backslash()
    {
        // Two literal backslashes before '(' is an *escaped backslash* followed by a genuine,
        // unescaped capturing group -- not an escaped '(' (which would be a single backslash).
        // A naive one-character-lookback escape check misclassifies this as escaped and skips
        // the group entirely, silently leaving the captured value un-injected (issue #273).
        var result = FeatureStepTextBuilder.Build(
            @"value \\(is captured)", oldExpression: null,
            new Regex(@"^value (\d+)$"),
            "value 42");

        result.Should().Be(@"value \\42");
    }

    // ── Scenario Outline with divergent step text ──────────────────────────────
    //    When the step text has been edited independently (e.g. user typed changes in
    //    the feature file), both static-segment alignment and regex may fail because
    //    the old expression text no longer appears in the step.  The outline-placeholder
    //    strategy must detect <...> tokens and map them to the new expression's slots. ──

    [Fact]
    public void Build_preserves_outline_placeholder_when_step_text_diverged()
    {
        // step text has "might be <result>" but old expression says "shouild be {int}"
        // Regex '^the result shouild be (\d+)$' doesn't match "might be <result>"
        // Static segments fail: "the result shouild be " not found in "the result might be <result>"
        // Outline-placeholder detects <result> and maps to {int} slot
        var result = FeatureStepTextBuilder.Build(
            "the result should be {int}", "the result shouild be {int}",
            new Regex("^the result shouild be (-?\\d+)$"),
            "the result might be <result>");

        result.Should().Be("the result should be <result>");
    }

    [Fact]
    public void Build_outline_placeholder_multi_param_preserved_when_diverged()
    {
        var result = FeatureStepTextBuilder.Build(
            "I ate {int} {string}", "I have {int} {string}",
            new Regex("^I have (\\d+) (\\w+)$"),
            "I consumed <count> <item>");

        result.Should().Be("I ate <count> <item>");
    }

    [Fact]
    public void Build_no_outline_placeholders_falls_through_to_newExpression()
    {
        // Both static and regex fail, but step has no <...> placeholders.
        var result = FeatureStepTextBuilder.Build(
            "the result is {int}", "the result shouild be {int}",
            new Regex("^the result shouild be (-?\\d+)$"),
            "the result should be awesome");

        result.Should().Be("the result is {int}");
    }

    [Fact]
    public void Build_outline_placeholder_count_mismatch_falls_through()
    {
        // 2 placeholders in step but only 1 slot in expression.
        var result = FeatureStepTextBuilder.Build(
            "the result is {int}", "the result is {int} {int}",
            new Regex("^the result is (\\d+) (\\w+)$"),
            "the result is <count> <item>");

        result.Should().Be("the result is {int}");
    }

    // ── DeriveExpressionFromEditedText: the inverse operation, used when a .feature-triggered
    //    rename hands back the edited concrete step text (real parameter values) instead of an
    //    abstract expression, so the rest of the rename pipeline can keep working with the
    //    abstract form. ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeriveExpressionFromEditedText_single_param_preserves_slot_and_wording_edit()
    {
        var result = FeatureStepTextBuilder.DeriveExpressionFromEditedText(
            "I have {int} cukes", "I have 5 cukes", "I have 5 pickles");

        result.Should().Be("I have {int} pickles");
    }

    [Fact]
    public void DeriveExpressionFromEditedText_multiple_params_preserve_slots_in_order()
    {
        var result = FeatureStepTextBuilder.DeriveExpressionFromEditedText(
            "the {int} was {string}", "the 3 was \"ok\"", "the 3 became \"ok\"");

        result.Should().Be("the {int} became {string}");
    }

    [Fact]
    public void DeriveExpressionFromEditedText_no_parameters_returns_edited_text_verbatim()
    {
        var result = FeatureStepTextBuilder.DeriveExpressionFromEditedText(
            "to be or not to be", "to be or not to be", "to be and not to be");

        result.Should().Be("to be and not to be");
    }

    [Fact]
    public void DeriveExpressionFromEditedText_parameter_value_changed_returns_null()
    {
        // The user changed the value (5 → 6), not just the wording — this rename flow only
        // supports renaming the step's wording, so it should be rejected rather than silently
        // renaming the binding to accept a different concrete value.
        var result = FeatureStepTextBuilder.DeriveExpressionFromEditedText(
            "I have {int} cukes", "I have 5 cukes", "I have 6 cukes");

        result.Should().BeNull();
    }
}
