using Reqnroll.IdeSupport.LSP.Core.Completions;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Completions;

/// <summary>
/// Issue #568: <see cref="RegexStepDefinitionExpressionAnalyzer"/> (regex-group analysis feeding
/// the completion sampler) and <see cref="AnalyzedStepDefinitionExpression"/> have zero direct
/// test coverage anywhere in the codebase.
/// </summary>
public class RegexStepDefinitionExpressionAnalyzerTests
{
    private static RegexStepDefinitionExpressionAnalyzer CreateSut() => new();

    [Fact]
    public void Parse_of_plain_text_with_no_groups_returns_a_single_simple_text_part()
    {
        var result = CreateSut().Parse("I have plain text");

        result.Parts.Should().ContainSingle();
        var part = result.Parts[0].Should().BeOfType<AnalyzedStepDefinitionExpressionSimpleTextPart>().Subject;
        part.Text.Should().Be("I have plain text");
        part.UnescapedText.Should().Be("I have plain text");
        result.ContainsOnlySimpleText.Should().BeTrue();
        result.ParameterParts.Should().BeEmpty();
    }

    [Fact]
    public void Parse_splits_text_around_a_single_capturing_group()
    {
        var result = CreateSut().Parse(@"I have (\d+) cukes");

        result.Parts.Should().HaveCount(3);
        result.Parts[0].Should().BeOfType<AnalyzedStepDefinitionExpressionSimpleTextPart>()
            .Which.Text.Should().Be("I have ");
        var parameter = result.Parts[1].Should().BeOfType<AnalyzedStepDefinitionExpressionParameterPart>().Subject;
        parameter.ParameterExpression.Should().Be(@"(\d+)");
        result.Parts[2].Should().BeOfType<AnalyzedStepDefinitionExpressionSimpleTextPart>()
            .Which.Text.Should().Be(" cukes");
    }

    [Fact]
    public void Parse_splits_text_around_multiple_capturing_groups()
    {
        var result = CreateSut().Parse(@"I have (\d+) cukes and (\d+) apples");

        result.ParameterParts.Select(p => p.ParameterExpression).Should().Equal(@"(\d+)", @"(\d+)");
        result.Parts.Should().HaveCount(5);
    }

    [Fact]
    public void Parse_with_a_trailing_capturing_group_produces_an_empty_trailing_text_part()
    {
        var result = CreateSut().Parse(@"I have (\d+)");

        result.Parts.Should().HaveCount(3);
        result.Parts[2].Should().BeOfType<AnalyzedStepDefinitionExpressionSimpleTextPart>()
            .Which.Text.Should().BeEmpty();
    }

    [Fact]
    public void Parse_with_a_leading_capturing_group_produces_an_empty_leading_text_part()
    {
        var result = CreateSut().Parse(@"(\d+) cukes");

        result.Parts.Should().HaveCount(3);
        result.Parts[0].Should().BeOfType<AnalyzedStepDefinitionExpressionSimpleTextPart>()
            .Which.Text.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ignores_non_capturing_groups_treating_them_as_operator_text()
    {
        var result = CreateSut().Parse(@"I have (?:some|any) cukes");

        result.ParameterParts.Should().BeEmpty("a non-capturing group is not a step-definition parameter");
        result.ContainsOnlySimpleText.Should().BeFalse("the (?:...) syntax counts as a regex operator outside a capturing group");
    }

    [Fact]
    public void Parse_treats_an_escaped_open_paren_as_literal_text_not_a_group()
    {
        var result = CreateSut().Parse(@"I have \(no group\)");

        result.ParameterParts.Should().BeEmpty();
        var part = result.Parts.Should().ContainSingle().Which.Should()
            .BeOfType<AnalyzedStepDefinitionExpressionSimpleTextPart>().Subject;
        part.UnescapedText.Should().Be("I have (no group)");
    }

    [Fact]
    public void Parse_unescapes_escaped_characters_in_the_unescaped_text()
    {
        var result = CreateSut().Parse(@"cost is \$5");

        var part = result.Parts.Should().ContainSingle().Which.Should()
            .BeOfType<AnalyzedStepDefinitionExpressionSimpleTextPart>().Subject;
        part.Text.Should().Be(@"cost is \$5");
        part.UnescapedText.Should().Be("cost is $5");
    }

    [Fact]
    public void Parse_marks_text_with_regex_operators_as_not_simple()
    {
        var result = CreateSut().Parse("I have.*cukes");

        result.ContainsOnlySimpleText.Should().BeFalse();
        result.Parts.Should().ContainSingle().Which.Should()
            .BeOfType<AnalyzedStepDefinitionExpressionWithOperatorsTextPart>();
    }

    [Fact]
    public void Parse_handles_nested_capturing_groups_as_a_single_parameter()
    {
        var result = CreateSut().Parse(@"I have ((\d+) or (\d+)) cukes");

        result.ParameterParts.Should().ContainSingle()
            .Which.ParameterExpression.Should().Be(@"((\d+) or (\d+))");
    }

    [Fact]
    public void Parse_of_an_empty_expression_returns_a_single_empty_simple_text_part()
    {
        var result = CreateSut().Parse("");

        result.Parts.Should().ContainSingle();
        result.Parts[0].Should().BeOfType<AnalyzedStepDefinitionExpressionSimpleTextPart>()
            .Which.Text.Should().BeEmpty();
        result.ContainsOnlySimpleText.Should().BeTrue();
    }

    [Fact]
    public void ExpressionText_reflects_the_parts_own_text()
    {
        var result = CreateSut().Parse(@"I have (\d+) cukes");

        result.Parts.Select(p => p.ExpressionText).Should().Equal("I have ", @"(\d+)", " cukes");
    }
}
