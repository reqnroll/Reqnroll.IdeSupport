using Cucumber.TagExpressions;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.TagExpressions;

/// <summary>
/// Issue #568: <see cref="ReqnrollTagExpressionParser"/> has real branching (single-literal vs.
/// multi-term @-prefix enforcement, recursive prefix confirmation over Binary/Not/Literal nodes,
/// exception→<see cref="InvalidTagExpression"/> conversion with offset reporting) with no direct
/// test coverage anywhere in the codebase.
/// </summary>
public class ReqnrollTagExpressionParserTests
{
    private static ReqnrollTagExpressionParser CreateSut() => new();

    // ── Single literal, @-prefix auto-added ──────────────────────────────────────

    [Fact]
    public void Parse_of_a_single_tag_with_at_prefix_matches_that_tag()
    {
        var result = CreateSut().Parse("@foo");

        result.Evaluate(new[] { "@foo" }).Should().BeTrue();
        result.Evaluate(new[] { "@bar" }).Should().BeFalse();
    }

    [Fact]
    public void Parse_of_a_single_tag_without_at_prefix_is_auto_prefixed()
    {
        var result = CreateSut().Parse("foo");

        result.Evaluate(new[] { "@foo" }).Should().BeTrue("a bare single-literal expression should be rewritten with a leading '@'");
    }

    [Fact]
    public void Parse_of_an_empty_string_returns_a_null_expression_matching_everything()
    {
        var result = CreateSut().Parse("");

        result.Evaluate(new[] { "@anything" }).Should().BeTrue();
        result.Evaluate(Array.Empty<string>()).Should().BeTrue();
    }

    // ── Multi-term expressions ────────────────────────────────────────────────────

    [Fact]
    public void Parse_of_an_and_expression_requires_both_tags()
    {
        var result = CreateSut().Parse("@foo and @bar");

        result.Evaluate(new[] { "@foo", "@bar" }).Should().BeTrue();
        result.Evaluate(new[] { "@foo" }).Should().BeFalse();
        result.Evaluate(new[] { "@bar" }).Should().BeFalse();
    }

    [Fact]
    public void Parse_of_an_or_expression_requires_either_tag()
    {
        var result = CreateSut().Parse("@foo or @bar");

        result.Evaluate(new[] { "@foo" }).Should().BeTrue();
        result.Evaluate(new[] { "@bar" }).Should().BeTrue();
        result.Evaluate(new[] { "@baz" }).Should().BeFalse();
    }

    [Fact]
    public void Parse_of_a_not_expression_negates_the_tag()
    {
        var result = CreateSut().Parse("not @foo");

        result.Evaluate(new[] { "@foo" }).Should().BeFalse();
        result.Evaluate(new[] { "@bar" }).Should().BeTrue();
    }

    [Fact]
    public void Parse_of_a_nested_expression_evaluates_correctly()
    {
        var result = CreateSut().Parse("@foo and (@bar or @baz)");

        result.Evaluate(new[] { "@foo", "@bar" }).Should().BeTrue();
        result.Evaluate(new[] { "@foo", "@baz" }).Should().BeTrue();
        result.Evaluate(new[] { "@foo" }).Should().BeFalse();
        result.Evaluate(new[] { "@bar", "@baz" }).Should().BeFalse("the @foo term is not satisfied");
    }

    // ── Multi-term @ prefix enforcement ────────────────────────────────────────────

    [Fact]
    public void Parse_of_a_multi_term_expression_missing_at_prefix_returns_an_invalid_expression()
    {
        var result = CreateSut().Parse("foo and @bar");

        result.Should().BeOfType<InvalidTagExpression>();
        ((InvalidTagExpression)result).Message.Should().Contain("must start with '@'");
    }

    [Fact]
    public void Evaluating_an_invalid_multi_term_expression_throws()
    {
        var result = CreateSut().Parse("foo and @bar");

        var act = () => result.Evaluate(new[] { "@foo", "@bar" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Cannot evaluate an invalid tag expression*");
    }

    [Fact]
    public void ToString_of_an_invalid_expression_formats_the_parse_failure_reason()
    {
        var result = CreateSut().Parse("foo and @bar");

        result.ToString().Should().StartWith("Invalid Tag Expression: ");
    }

    [Fact]
    public void Parse_of_a_multi_term_or_expression_with_both_terms_prefixed_is_valid()
    {
        var result = CreateSut().Parse("@foo and @bar");

        result.Should().NotBeOfType<InvalidTagExpression>();
    }

    // ── Malformed input ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_of_a_malformed_expression_returns_an_invalid_expression_instead_of_throwing()
    {
        var act = () => CreateSut().Parse("@foo and");

        var result = act.Should().NotThrow().Subject;
        result.Should().BeOfType<InvalidTagExpression>();
    }

    // ── CreateTagLiteral ───────────────────────────────────────────────────────────

    [Fact]
    public void CreateTagLiteral_builds_a_literal_node_matching_only_the_given_tag()
    {
        var literal = ReqnrollTagExpressionParser.CreateTagLiteral("@foo");

        literal.Evaluate(new[] { "@foo" }).Should().BeTrue();
        literal.Evaluate(new[] { "@bar" }).Should().BeFalse();
    }

    // ── ReqnrollTagExpression wrapper ────────────────────────────────────────────

    [Fact]
    public void Parse_result_exposes_the_original_tag_expression_text()
    {
        var result = CreateSut().Parse("@foo and @bar");

        result.As<ReqnrollTagExpression>().TagExpressionText.Should().Be("@foo and @bar");
    }

    [Fact]
    public void ToString_of_a_valid_expression_delegates_to_the_inner_expression()
    {
        var result = CreateSut().Parse("@foo");

        result.ToString().Should().Contain("foo");
    }
}
