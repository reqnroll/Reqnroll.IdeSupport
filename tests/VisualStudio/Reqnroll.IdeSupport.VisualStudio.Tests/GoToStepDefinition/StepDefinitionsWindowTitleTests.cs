using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.GoToStepDefinition;

/// <summary>
/// The Find All References window title for Go To Definition with several matching step
/// definitions (<see cref="StepDefinitionsWindowTitle.Build"/>, issue #757).
/// </summary>
public class StepDefinitionsWindowTitleTests
{
    [Fact]
    public void Names_the_whole_step_including_its_keyword()
    {
        StepDefinitionsWindowTitle.Build("    Given the first number is 50", 2)
            .Should().Be("Reqnroll: 2 step definitions for 'Given the first number is 50'");
    }

    [Fact]
    public void Keeps_scenario_outline_placeholders_verbatim()
    {
        StepDefinitionsWindowTitle.Build("\tGiven the second number is <secondNumber>\r\n", 3)
            .Should().Be("Reqnroll: 3 step definitions for 'Given the second number is <secondNumber>'");
    }

    [Fact]
    public void Collapses_internal_whitespace_runs()
    {
        StepDefinitionsWindowTitle.Build("When   the two\tnumbers are added", 2)
            .Should().Be("Reqnroll: 2 step definitions for 'When the two numbers are added'");
    }

    [Fact]
    public void Uses_the_singular_noun_for_one_definition()
    {
        StepDefinitionsWindowTitle.Build("Then it works", 1)
            .Should().Be("Reqnroll: 1 step definition for 'Then it works'");
    }

    [Fact]
    public void Truncates_long_steps_with_an_ellipsis()
    {
        var step  = "Given " + new string('x', 200);
        var title = StepDefinitionsWindowTitle.Build(step, 2);

        var quoted = title.Substring(title.IndexOf('\'') + 1).TrimEnd('\'');
        quoted.Should().HaveLength(StepDefinitionsWindowTitle.MaxStepLength);
        quoted.Should().StartWith("Given xxx").And.EndWith("…");
    }

    [Fact]
    public void Omits_the_step_when_the_line_is_blank()
    {
        StepDefinitionsWindowTitle.Build("   ", 2).Should().Be("Reqnroll: 2 step definitions");
    }
}
