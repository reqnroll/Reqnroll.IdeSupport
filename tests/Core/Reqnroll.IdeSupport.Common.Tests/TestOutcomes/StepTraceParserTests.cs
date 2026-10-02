using AwesomeAssertions;
using Reqnroll.IdeSupport.Common.TestOutcomes;
using Xunit;

namespace Reqnroll.IdeSupport.Common.Tests.TestOutcomes;

/// <summary>
/// <see cref="StepTraceParser"/> against real captured stdout (the MsTestReqnroll fixture run through
/// the bundled logger on 2026-09-16, MSTest's <c>TestContext Messages:</c> header and Reqnroll's
/// plugin-loading tool output included) plus synthetic samples for the outcomes the fixture doesn't
/// exercise — the seven-outcome vocabulary is Test-Runner-Integration-Design §6's table, decompiled from
/// <c>Reqnroll.Tracing.TestTracer</c>.
/// </summary>
public class StepTraceParserTests
{
    private const string PassingScenario =
        "-> Loading plugin C:\\repo\\bin\\Debug\\net10.0\\MsTestReqnroll.Fixture.dll\n" +
        "-> Loading plugin C:\\repo\\bin\\Debug\\net10.0\\Reqnroll.MSTest.ReqnrollPlugin.dll\n" +
        "-> Using default config\n" +
        "\n\n" +
        "TestContext Messages:\n" +
        "Given the first number is 50\n" +
        "-> done: CalculatorSteps.GivenTheFirstNumberIs(50) (0.0s)\n" +
        "And the second number is 70\n" +
        "-> done: CalculatorSteps.GivenTheSecondNumberIs(70) (0.0s)\n" +
        "When the two numbers are added\n" +
        "-> done: CalculatorSteps.WhenTheTwoNumbersAreAdded() (0.0s)\n" +
        "Then the result should be 120\n" +
        "-> done: CalculatorSteps.ThenTheResultShouldBe(120) (0.0s)\n";

    private const string FailingMiddleStep =
        "\r\n\r\nTestContext Messages:\r\n" +
        "Given the first number is 1\r\n" +
        "-> done: CalculatorSteps.GivenTheFirstNumberIs(1) (0.0s)\r\n" +
        "When the calculation explodes\r\n" +
        "-> error: deliberate failure in the middle step (0.0s)\r\n" +
        "Then the result should be 2\r\n" +
        "-> skipped because of previous errors\r\n";

    [Fact]
    public void Parses_a_passing_scenario_ignoring_plugin_and_header_noise()
    {
        var steps = StepTraceParser.Parse(PassingScenario);

        steps.Should().HaveCount(4);
        steps.Select(s => s.Index).Should().Equal(0, 1, 2, 3);
        steps.Select(s => s.StepText).Should().Equal(
            "Given the first number is 50", "And the second number is 70", "When the two numbers are added", "Then the result should be 120");
        steps.Should().OnlyContain(s => s.Outcome == StepTraceOutcome.Done);
        steps[0].Detail.Should().Be("CalculatorSteps.GivenTheFirstNumberIs(50)");
        steps[0].DurationSeconds.Should().Be(0.0);
        steps.Should().OnlyContain(s => !s.IsFailure);
    }

    [Fact]
    public void Attributes_the_failure_to_the_step_that_threw_not_the_last_one()
    {
        var steps = StepTraceParser.Parse(FailingMiddleStep);

        steps.Should().HaveCount(3);
        steps[0].Outcome.Should().Be(StepTraceOutcome.Done);
        steps[1].Outcome.Should().Be(StepTraceOutcome.Error);
        steps[1].StepText.Should().Be("When the calculation explodes");
        steps[1].Detail.Should().Be("deliberate failure in the middle step");
        steps[1].IsFailure.Should().BeTrue();
        steps[2].Outcome.Should().Be(StepTraceOutcome.SkippedBecauseOfPreviousErrors);
        steps[2].Detail.Should().BeNull();
        steps[2].DurationSeconds.Should().BeNull();
    }

    [Fact]
    public void Error_messages_containing_parentheses_keep_their_text_and_still_yield_the_duration()
    {
        var steps = StepTraceParser.Parse(
            "Then the result should be 11\n" +
            "-> error: Assert.AreEqual failed. Expected:<11>. Actual:<10>.  (0.0s)\n");

        steps.Should().ContainSingle();
        steps[0].Detail.Should().Be("Assert.AreEqual failed. Expected:<11>. Actual:<10>. ");
        steps[0].DurationSeconds.Should().Be(0.0);
    }

    [Fact]
    public void Durations_use_the_current_culture_so_a_comma_decimal_separator_is_accepted()
    {
        var steps = StepTraceParser.Parse("Given x\n-> done: Steps.X() (1,5s)\n");

        steps[0].DurationSeconds.Should().Be(1.5);
        steps[0].Detail.Should().Be("Steps.X()");
    }

    [Fact]
    public void Parses_skipped_pending_and_binding_error()
    {
        var steps = StepTraceParser.Parse(
            "Given a skipped step\n" +
            "-> skipped: Not on this platform\n" +
            "When a pending step\n" +
            "-> pending: Steps.Pending(): The step definition is not implemented.\n" +
            "Then the second number is 5\n" +
            "-> binding error: Ambiguous step definitions found for step 'the second number is 5': Steps.A(5), Steps.B(5)\n");

        steps.Select(s => s.Outcome).Should().Equal(StepTraceOutcome.Skipped, StepTraceOutcome.Pending, StepTraceOutcome.BindingError);
        steps[0].Detail.Should().Be("Not on this platform");
        steps[1].Detail.Should().Be("Steps.Pending(): The step definition is not implemented.");
        steps[2].Detail.Should().StartWith("Ambiguous step definitions found");
        steps[2].IsFailure.Should().BeTrue();
        steps.Take(2).Should().OnlyContain(s => !s.IsFailure);
    }

    [Fact]
    public void Undefined_step_swallows_the_indented_skeleton_into_its_detail_and_the_next_step_parses_cleanly()
    {
        var steps = StepTraceParser.Parse(
            "Given an unbound step\n" +
            "-> undefined: No matching step definition found for the step. Use the following code to create one:\n" +
            "        [Given(\"an unbound step\")]\n" +
            "        public void GivenAnUnboundStep()\n" +
            "        {\n" +
            "            throw new PendingStepException();\n" +
            "        }\n" +
            "Then another step\n" +
            "-> skipped because of previous errors\n");

        steps.Should().HaveCount(2);
        steps[0].Outcome.Should().Be(StepTraceOutcome.Undefined);
        steps[0].StepText.Should().Be("Given an unbound step");
        steps[0].Detail.Should().StartWith("No matching step definition found").And.Contain("throw new PendingStepException();");
        steps[0].IsFailure.Should().BeTrue();
        steps[1].StepText.Should().Be("Then another step");
        steps[1].Outcome.Should().Be(StepTraceOutcome.SkippedBecauseOfPreviousErrors);
    }

    [Fact]
    public void Scope_mismatch_preamble_before_undefined_is_ignored()
    {
        var steps = StepTraceParser.Parse(
            "Given a scoped step\n" +
            "-> No matching step definition found for the step. There are matching step definitions, but none of them have matching scope for this step: Steps.Scoped().\n" +
            "-> undefined: Change the scope or use the following code to create a new step definition:\n" +
            "        [Given(\"a scoped step\")]\n");

        steps.Should().ContainSingle();
        steps[0].Outcome.Should().Be(StepTraceOutcome.Undefined);
        steps[0].StepText.Should().Be("Given a scoped step");
    }

    [Fact]
    public void Table_and_docstring_arguments_stay_attached_to_their_step_and_are_not_mistaken_for_steps()
    {
        var steps = StepTraceParser.Parse(
            "And the client added\n" +
            "  --- table step argument ---\n" +
            "  | product         | quantity |\n" +
            "  | Electric guitar | 1        |\n" +
            "-> done: Steps.GivenTheClientAdded(<table>) (0.0s)\n" +
            "When a step with text\n" +
            "  --- multiline step argument ---\n" +
            "  line one\n" +
            "  line two\n" +
            "-> done: Steps.WhenAStepWithText(<text>) (0.0s)\n");

        steps.Select(s => s.StepText).Should().Equal("And the client added", "When a step with text");
    }

    [Fact]
    public void Hook_output_before_a_step_and_binding_output_after_it_do_not_displace_the_step_text()
    {
        var steps = StepTraceParser.Parse(
            "BeforeScenario hook says hello\n" +
            "Given the first number is 1\n" +
            "binding wrote this to the console\n" +
            "-> done: Steps.Given(1) (0.0s)\n");

        steps.Should().ContainSingle().Which.StepText.Should().Be("Given the first number is 1");
    }

    [Fact]
    public void Localized_German_step_keyword_is_recognised_even_with_trailing_binding_output()
    {
        // A German feature file (#language: de) traces "Angenommen ..." verbatim — Reqnroll never
        // translates the keyword to English. Hook output surrounds it on both sides, so this only
        // passes if "Angenommen" is actually recognised as a keyword (picking the *first* matching
        // candidate) rather than falling back to the *last* non-indented line, which here would
        // wrongly be the binding's own console output.
        var steps = StepTraceParser.Parse(
            "Some hook output\n" +
            "Angenommen die erste Zahl ist 1\n" +
            "binding wrote this to the console\n" +
            "-> done: Steps.Given(1) (0.0s)\n");

        steps.Should().ContainSingle().Which.StepText.Should().Be("Angenommen die erste Zahl ist 1");
    }

    [Fact]
    public void Localized_French_step_keyword_with_an_elided_apostrophe_form_is_recognised()
    {
        // French has several synonyms for "Given", including apostrophe-elided forms ("Sachant qu'")
        // — GherkinDialect keeps the apostrophe as part of the keyword itself, no trailing space.
        var steps = StepTraceParser.Parse(
            "Sachant qu'il fait beau\n" +
            "binding wrote this to the console\n" +
            "-> done: Steps.Given() (0.0s)\n");

        steps.Should().ContainSingle().Which.StepText.Should().Be("Sachant qu'il fait beau");
    }

    [Fact]
    public void Without_any_recognisable_keyword_the_last_non_indented_line_before_the_outcome_is_the_step()
    {
        // Genuinely unrecognisable text (not a real Gherkin keyword in any supported language) still
        // falls back to the last non-indented candidate, same as before this parser knew about
        // languages other than English.
        var steps = StepTraceParser.Parse(
            "Some hook output\n" +
            "not a gherkin step at all\n" +
            "-> done: Steps.Given(1) (0.0s)\n");

        steps.Should().ContainSingle().Which.StepText.Should().Be("not a gherkin step at all");
    }

    [Fact]
    public void Duration_and_warning_tool_output_lines_are_ignored()
    {
        var steps = StepTraceParser.Parse(
            "Given a slow step\n" +
            "-> done: Steps.Slow() (0.4s)\n" +
            "-> duration: Steps.Slow(): 0.4s\n" +
            "-> warning: something about the binding\n" +
            "Then a fast step\n" +
            "-> done: Steps.Fast() (0.0s)\n");

        steps.Should().HaveCount(2);
        steps[1].StepText.Should().Be("Then a fast step");
    }

    [Fact]
    public void Ansi_colour_codes_are_stripped()
    {
        var steps = StepTraceParser.Parse(
            "\u001b[36mGiven\u001b[0m the first number is 1\n" +
            "-> \u001b[32mdone\u001b[0m: Steps.Given(1) (0.0s)\n");

        steps.Should().ContainSingle();
        steps[0].StepText.Should().Be("Given the first number is 1");
        steps[0].Outcome.Should().Be(StepTraceOutcome.Done);
    }

    [Fact]
    public void An_outcome_with_no_preceding_step_line_is_kept_with_empty_text()
    {
        var steps = StepTraceParser.Parse("-> error: exploded before any step traced (0.0s)\n");

        steps.Should().ContainSingle();
        steps[0].StepText.Should().BeEmpty();
        steps[0].Outcome.Should().Be(StepTraceOutcome.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n\nTestContext Messages:\n")]
    [InlineData("just some console output with no trace at all")]
    public void Empty_or_traceless_output_yields_no_steps(string? stdout)
        => StepTraceParser.Parse(stdout).Should().BeEmpty();

    [Fact]
    public void Reflected_keyword_set_covers_a_realistic_number_of_languages()
    {
        // BuildStepKeywords reflects on Gherkin's private CreateGherkinDialectFor_<code>() naming
        // convention rather than a hand-maintained language-code list, specifically so it never goes
        // stale — but that also means a future Gherkin package version restructuring dialect
        // generation would silently return fewer (in the limit, English-only or zero) keywords
        // instead of failing to compile. 587 is the real, measured count for the referenced Gherkin
        // 39.1.0 package (80 languages); this floor is generous enough not to break on ordinary
        // language additions/removals while still catching a collapse back toward "English only".
        StepTraceParser.StepKeywordCountForTests.Should().BeGreaterThan(300);
    }
}
