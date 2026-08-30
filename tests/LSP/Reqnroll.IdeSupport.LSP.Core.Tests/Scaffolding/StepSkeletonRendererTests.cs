#nullable enable

using Gherkin;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Scaffolding;

public class StepSkeletonRendererTests
{
    // ── BuildDescriptor — expression building ─────────────────────────────────

    [Theory]
    [InlineData("I press add",  SnippetExpressionStyle.CucumberExpression, "I press add")]
    [InlineData("I press add",  SnippetExpressionStyle.RegularExpression,  "I press add")]
    public void Plain_step_text_with_no_literals_is_preserved(
        string text, SnippetExpressionStyle style, string expectedExpression)
    {
        var step       = MakeStep(text, ScenarioBlock.When);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, style);
        descriptor.ExpressionText.Should().Be(expectedExpression);
    }

    [Theory]
    [InlineData("the operand {int} has been entered", "the operand {int} has been entered", "int",    "p0")]
    [InlineData("the client added {int} pcs",         "the client added {int} pcs",          "int",    "p0")]
    [InlineData("{string} is entered",                "{string} is entered",                 "string", "p0")]
    public void Cucumber_explicit_placeholder_preserved_in_expression_and_typed_in_params(
        string text, string expectedExpression, string expectedType, string expectedName)
    {
        var step       = MakeStep(text, ScenarioBlock.Given);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.CucumberExpression);

        descriptor.ExpressionText.Should().Be(expectedExpression);
        descriptor.Parameters.Should().HaveCount(1);
        descriptor.Parameters[0].Type.Should().Be(expectedType);
        descriptor.Parameters[0].Name.Should().Be(expectedName);
    }

    // ── Parameter inference from step text literals ───────────────────────────

    [Theory]
    [InlineData("the result is 42",          "the result is {int}",   "int",    "p0")]
    [InlineData("I enter 3.14 in the field", "I enter {float} in the field", "float", "p0")]
    [InlineData("I search for 'hello world'","I search for {string}", "string", "p0")]
    [InlineData("I search for \"hello\"",    "I search for {string}", "string", "p0")]
    public void Cucumber_infers_single_parameter_from_literal_value(
        string text, string expectedExpression, string expectedType, string expectedName)
    {
        var step       = MakeStep(text, ScenarioBlock.When);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.CucumberExpression);

        descriptor.ExpressionText.Should().Be(expectedExpression);
        descriptor.Parameters.Should().HaveCount(1);
        descriptor.Parameters[0].Type.Should().Be(expectedType);
        descriptor.Parameters[0].Name.Should().Be(expectedName);
    }

    [Fact]
    public void Cucumber_infers_multiple_typed_parameters_from_literals()
    {
        var step       = MakeStep("the walrus said 'hello' and the result was 1100", ScenarioBlock.Then);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.CucumberExpression);

        descriptor.ExpressionText.Should().Be("the walrus said {string} and the result was {int}");
        descriptor.Parameters.Should().HaveCount(2);
        descriptor.Parameters[0].Should().Be(("string", "p0"));
        descriptor.Parameters[1].Should().Be(("int",    "p1"));
    }

    [Fact]
    public void Cucumber_float_takes_priority_over_int_when_decimal_present()
    {
        var step       = MakeStep("the ratio is 3.14 exactly", ScenarioBlock.Then);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.CucumberExpression);

        descriptor.ExpressionText.Should().Be("the ratio is {float} exactly");
        descriptor.Parameters.Should().HaveCount(1);
        descriptor.Parameters[0].Type.Should().Be("float");
    }

    [Fact]
    public void Cucumber_ordinal_like_1st_is_not_inferred_as_parameter()
    {
        var step       = MakeStep("the 1st result is shown", ScenarioBlock.Then);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.CucumberExpression);

        // "1" inside "1st" must not be extracted as a parameter
        descriptor.Parameters.Should().BeEmpty();
        descriptor.ExpressionText.Should().Be("the 1st result is shown");
    }

    // ── Method name generation ────────────────────────────────────────────────

    [Theory]
    [InlineData("I press add",                    ScenarioBlock.When,  false, "WhenIPressAdd")]
    [InlineData("the result is calculated",       ScenarioBlock.Then,  false, "ThenTheResultIsCalculated")]
    [InlineData("the operands have been entered", ScenarioBlock.Given, false, "GivenTheOperandsHaveBeenEntered")]
    [InlineData("I press add",                    ScenarioBlock.When,  true,  "WhenIPressAddAsync")]
    public void Method_name_is_PascalCase_of_keyword_plus_step_text(
        string text, ScenarioBlock block, bool isAsync, string expected)
    {
        var style = isAsync ? SnippetExpressionStyle.AsyncCucumberExpression
                            : SnippetExpressionStyle.CucumberExpression;
        var step       = MakeStep(text, block);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, style);
        descriptor.MethodName.Should().Be(expected);
    }

    [Fact]
    public void Explicit_placeholder_excluded_from_method_name()
    {
        var step       = MakeStep("the operand {int} has been entered", ScenarioBlock.Given);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.CucumberExpression);
        descriptor.MethodName.Should().Be("GivenTheOperandHasBeenEntered");
    }

    [Fact]
    public void Literal_values_excluded_from_method_name()
    {
        var step       = MakeStep("the walrus said 'hello' and the result was 1100", ScenarioBlock.Then);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.CucumberExpression);
        descriptor.MethodName.Should().Be("ThenTheWalrusSaidAndTheResultWas");
    }

    // ── Escaping — CucumberExpression ─────────────────────────────────────────

    [Theory]
    [InlineData("I use (parenthesis)",          "I use \\(parenthesis)")]
    [InlineData("I use {curly braces}",         "I use \\{curly braces}")]
    [InlineData("I use \\ backslash",           "I use \\\\ backslash")]
    public void Cucumber_expression_escapes_special_chars(string text, string expected)
    {
        var result = StepSkeletonRenderer.EscapeForCucumber(text);
        result.Should().Be(expected);
    }

    // ── Escaping — Regex ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("I use (parenthesis)",      "I use \\(parenthesis\\)")]
    [InlineData("I use {curly braces}",     "I use \\{curly braces}")]
    [InlineData("I use \\ backslash",       "I use \\\\ backslash")]
    [InlineData("I use . period",           "I use \\. period")]
    public void Regex_expression_escapes_special_chars(string text, string expected)
    {
        var result = StepSkeletonRenderer.EscapeForRegex(text);
        result.Should().Be(expected);
    }

    // ── Render — output format ────────────────────────────────────────────────

    [Fact]
    public void Renders_cucumber_sync_method()
    {
        var step       = MakeStep("I press add", ScenarioBlock.When);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.CucumberExpression);
        var snippet    = StepSkeletonRenderer.Render(descriptor, "    ", "\n");

        snippet.Should().Contain("[When(\"I press add\")]");
        snippet.Should().Contain("public void WhenIPressAdd()");
        snippet.Should().Contain("throw new PendingStepException();");
    }

    [Fact]
    public void Renders_regex_sync_method()
    {
        var step       = MakeStep("I press add", ScenarioBlock.When);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.RegularExpression);
        var snippet    = StepSkeletonRenderer.Render(descriptor, "    ", "\n");

        snippet.Should().Contain("[When(@\"I press add\")]");
        snippet.Should().Contain("public void WhenIPressAdd()");
    }

    [Fact]
    public void Renders_async_method()
    {
        var step       = MakeStep("I press add", ScenarioBlock.When);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.AsyncCucumberExpression);
        var snippet    = StepSkeletonRenderer.Render(descriptor, "    ", "\n");

        snippet.Should().Contain("public async Task WhenIPressAddAsync()");
    }

    [Fact]
    public void Renders_inferred_int_parameter_in_method_signature()
    {
        var step       = MakeStep("the result is 42", ScenarioBlock.Then);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.CucumberExpression);
        var snippet    = StepSkeletonRenderer.Render(descriptor, "    ", "\n");

        snippet.Should().Contain("[Then(\"the result is {int}\")]");
        snippet.Should().Contain("public void ThenTheResultIs(int p0)");
    }

    [Fact]
    public void Renders_inferred_string_and_int_parameters_in_method_signature()
    {
        var step       = MakeStep("the walrus said 'hello' and the result was 1100", ScenarioBlock.Then);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.CucumberExpression);
        var snippet    = StepSkeletonRenderer.Render(descriptor, "    ", "\n");

        snippet.Should().Contain("[Then(\"the walrus said {string} and the result was {int}\")]");
        snippet.Should().Contain("public void ThenTheWalrusSaidAndTheResultWas(string p0, int p1)");
    }

    [Fact]
    public void Renders_explicit_placeholder_parameter_in_method_signature()
    {
        var step       = MakeStep("the operand {int} has been entered", ScenarioBlock.Given);
        var descriptor = StepSkeletonRenderer.BuildDescriptor(step, SnippetExpressionStyle.CucumberExpression);
        var snippet    = StepSkeletonRenderer.Render(descriptor, "    ", "\n");

        snippet.Should().Contain("[Given(\"the operand {int} has been entered\")]");
        snippet.Should().Contain("public void GivenTheOperandHasBeenEntered(int p0)");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static UndefinedStepDescriptor MakeStep(string text, ScenarioBlock block)
    {
        var keyword = block switch
        {
            ScenarioBlock.Given => "Given ",
            ScenarioBlock.When  => "When ",
            ScenarioBlock.Then  => "Then ",
            _                   => "Given "
        };
        var gherkinStep = new IdeSupportGherkinStep(
            new global::Gherkin.Ast.Location(0, 0), keyword, StepKeywordType.Context,
            text, argument: null!, StepKeyword.Given, block);
        return new UndefinedStepDescriptor(gherkinStep, text);
    }
}
