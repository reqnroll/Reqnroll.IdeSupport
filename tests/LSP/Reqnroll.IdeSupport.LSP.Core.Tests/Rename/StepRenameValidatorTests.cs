using Reqnroll.IdeSupport.LSP.Core.Rename;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Rename;

public class StepRenameValidatorTests
{
    // ── ValidateCursorPosition ──────────────────────────────────────────────────

    [Fact]
    public void ValidateCursorPosition_cs_file_returns_null()
    {
        var result = StepRenameValidator.ValidateCursorPosition(new Uri("file:///C:/test/file.cs"));
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateCursorPosition_feature_file_returns_null()
    {
        var result = StepRenameValidator.ValidateCursorPosition(new Uri("file:///C:/test/file.feature"));
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateCursorPosition_other_file_returns_error()
    {
        var result = StepRenameValidator.ValidateCursorPosition(new Uri("file:///C:/test/file.txt"));
        result.Should().NotBeNull();
        result.Message.Should().Be("No step definition found at this position");
        result.Scope.Should().Be("position");
    }

    // ── ValidateExpressionIsStringLiteral ───────────────────────────────────────

    [Fact]
    public void ValidateExpressionIsStringLiteral_valid_returns_null()
    {
        var result = StepRenameValidator.ValidateExpressionIsStringLiteral("I press add");
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateExpressionIsStringLiteral_invalid_returns_error(string? expression)
    {
        var result = StepRenameValidator.ValidateExpressionIsStringLiteral(expression);
        result.Should().NotBeNull();
        result.Message.Should().Be("Step definition expression cannot be detected");
        result.Scope.Should().Be("expression");
    }

    // ── ValidateNewName ─────────────────────────────────────────────────────────

    [Fact]
    public void ValidateNewName_simple_no_params_passes()
    {
        var result = StepRenameValidator.ValidateNewName("I press add", "I choose add");
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_with_params_passes()
    {
        var result = StepRenameValidator.ValidateNewName("I press (.*)", "I choose (.*)");
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_operator_in_non_param_fails()
    {
        var result = StepRenameValidator.ValidateNewName("I press (.*) add", "I pr?ess (.*) add");
        result.Should().NotBeNull();
        result.Message.Should().Be("The non-parameter parts cannot contain expression operators");
        result.Scope.Should().Be("rename");
    }

    [Fact]
    public void ValidateNewName_param_count_mismatch_fails()
    {
        var result = StepRenameValidator.ValidateNewName("I press (.*) add", "I choose (.*) and (.*)");
        result.Should().NotBeNull();
        result.Message.Should().Be("Parameter count mismatch");
        result.Scope.Should().Be("rename");
    }

    [Fact]
    public void ValidateNewName_empty_newName_returns_error()
    {
        var result = StepRenameValidator.ValidateNewName("I press add", "");
        result.Should().NotBeNull();
        result.Message.Should().Be("The new step text cannot be empty");
        result.Scope.Should().Be("rename");
    }

    // ── Rule 3, Cucumber Expression vs regex operator sets (issue #649) ────────

    [Fact]
    public void ValidateNewName_cucumber_expression_literal_dollar_sign_passes()
    {
        // '$' is a plain literal in Cucumber Expression syntax (e.g. a currency amount) -
        // it must not be treated as a forbidden "expression operator" the way it would be in a regex.
        var result = StepRenameValidator.ValidateNewName(
            "the basket price should be ${float}", "the basket price will be ${float}");
        result.Should().BeNull();
    }

    [Theory]
    [InlineData('$')]
    [InlineData('^')]
    [InlineData('*')]
    [InlineData('+')]
    [InlineData('[')]
    [InlineData(']')]
    [InlineData('|')]
    public void ValidateNewName_cucumber_expression_other_regex_only_operators_pass(char literalChar)
    {
        var result = StepRenameValidator.ValidateNewName("I have {int} cukes", $"I own {{int}} cukes{literalChar}");
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_cucumber_expression_real_operator_still_fails()
    {
        // '(' is genuinely special in Cucumber Expressions (optional text) - still forbidden
        // outside of a parameter slot even though it wasn't parsed as one here.
        var result = StepRenameValidator.ValidateNewName("I have {int} cukes", "I own( {int} cukes");
        result.Should().NotBeNull();
        result.Message.Should().Be("The non-parameter parts cannot contain expression operators");
        result.Scope.Should().Be("rename");
    }

    [Fact]
    public void ValidateNewName_regex_literal_dollar_sign_still_fails()
    {
        // Unlike the Cucumber Expression case above, '$' really is a regex anchor/operator when
        // the original binding is a raw regex, so it must remain forbidden there.
        var result = StepRenameValidator.ValidateNewName("I press (.*) add", "I press$ (.*) add");
        result.Should().NotBeNull();
        result.Message.Should().Be("The non-parameter parts cannot contain expression operators");
        result.Scope.Should().Be("rename");
    }

    // ── ValidateProjectState ────────────────────────────────────────────────────

    [Fact]
    public void ValidateProjectState_initialized_with_features_returns_null()
    {
        var result = StepRenameValidator.ValidateProjectState(true, true);
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateProjectState_not_initialized_returns_error()
    {
        var result = StepRenameValidator.ValidateProjectState(false, true);
        result.Should().NotBeNull();
        result.Message.Should().Be("The project is not initialized yet");
        result.Scope.Should().Be("project");
    }

    [Fact]
    public void ValidateProjectState_no_features_returns_error()
    {
        var result = StepRenameValidator.ValidateProjectState(true, false);
        result.Should().NotBeNull();
        result.Message.Should().Be("No Reqnroll project with feature files found");
        result.Scope.Should().Be("project");
    }
}
