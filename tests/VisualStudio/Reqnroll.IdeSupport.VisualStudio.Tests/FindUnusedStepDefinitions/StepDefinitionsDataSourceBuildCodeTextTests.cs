using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.FindUnusedStepDefinitions;

/// <summary>
/// The Code-column text of a step-definition row (<see cref="StepDefinitionsDataSource.BuildCodeText"/>),
/// shared by Find Unused Step Definitions and Go To Definition with several matches (issue #757).
/// </summary>
public class StepDefinitionsDataSourceBuildCodeTextTests
{
    [Fact]
    public void Shows_the_method_and_its_attribute_with_the_expression()
    {
        StepDefinitionsDataSource.BuildCodeText("CalculatorSteps", "GivenTheFirstNumberIs", "Given", "the first number is {int}")
            .Should().Be("CalculatorSteps.GivenTheFirstNumberIs - [Given(\"the first number is {int}\")]");
    }

    [Fact]
    public void Shows_the_bare_attribute_for_a_method_name_style_binding()
    {
        StepDefinitionsDataSource.BuildCodeText("CalculatorSteps", "Given_the_first_number_is_P0", "Given", expression: null)
            .Should().Be("CalculatorSteps.Given_the_first_number_is_P0 - [Given]");
    }

    [Fact]
    public void Shows_the_quoted_expression_alone_when_the_keyword_is_unknown()
    {
        StepDefinitionsDataSource.BuildCodeText("Steps", "AStep", stepDefinitionType: null, "a step")
            .Should().Be("Steps.AStep - \"a step\"");
    }

    [Fact]
    public void Shows_only_the_method_when_neither_keyword_nor_expression_is_known()
    {
        StepDefinitionsDataSource.BuildCodeText("Steps", "AStep", stepDefinitionType: null, expression: null)
            .Should().Be("Steps.AStep");
    }

    [Fact]
    public void Says_why_an_unresolved_row_cannot_be_opened()
    {
        StepDefinitionsDataSource.BuildCodeText("Steps", "AStep", "When", "a step", isResolved: false)
            .Should().Be("Steps.AStep - [When(\"a step\")] - (source not on this machine — rebuild locally)");
    }

    [Fact]
    public void Falls_back_when_class_or_method_is_missing()
    {
        StepDefinitionsDataSource.BuildCodeText(null, "AStep", "Then", null).Should().Be("AStep - [Then]");
        StepDefinitionsDataSource.BuildCodeText("Steps", null, "Then", null).Should().Be("Steps - [Then]");
        StepDefinitionsDataSource.BuildCodeText(null, null, "Then", null).Should().Be("(unknown) - [Then]");
    }
}
