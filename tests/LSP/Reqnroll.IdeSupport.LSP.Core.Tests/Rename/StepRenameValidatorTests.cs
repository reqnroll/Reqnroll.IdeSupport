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

    [Fact]
    public void ValidateNewName_cucumber_expression_literal_slash_fails()
    {
        // Unlike '$'/'^'/etc., '/' is genuinely significant in Cucumber Expression syntax
        // everywhere it appears — confirmed against the real Cucumber.CucumberExpressions parser:
        // "{int}/{int}/{int}" parses as two ALTERNATION_NODEs, not literal slashes, so even a
        // date-like "01/02/2026" or a file path would silently mean something other than what it
        // looks like. Must stay forbidden outside a parameter slot.
        var result = StepRenameValidator.ValidateNewName("the date is {int}", "the date is {int}/{int}");
        result.Should().NotBeNull();
        result.Message.Should().Be("The non-parameter parts cannot contain expression operators");
        result.Scope.Should().Be("rename");
    }

    [Fact]
    public void ValidateNewName_cucumber_expression_literal_backslash_fails()
    {
        // '\' is Cucumber Expression's escape character - confirmed against the real parser: an
        // unescaped '\' followed by a non-escapable character (e.g. a Windows path like
        // "C:\Users") throws a CucumberExpressionException at compile time, so it must stay
        // forbidden here rather than let a rename produce an expression that fails to compile.
        var result = StepRenameValidator.ValidateNewName(
            "the path is {word}", @"the path is C:\Users\{word}");
        result.Should().NotBeNull();
        result.Message.Should().Be("The non-parameter parts cannot contain expression operators");
        result.Scope.Should().Be("rename");
    }

    // ── Rule 3: only newly-introduced operators are rejected (issue #1126) ─────

    [Fact]
    public void ValidateNewName_regex_keeps_original_anchors_passes()
    {
        // Visual Studio's "Rename Step" command seeds its prompt with the binding's own
        // expression and submits the edited text verbatim, so a regex binding's own ^...$
        // anchors arrive in newName without the user having introduced anything. A wording
        // change that keeps them must be accepted.
        var result = StepRenameValidator.ValidateNewName("^I have (.*) cukes$", "^I have (.*) gourds$");
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_regex_keeps_trailing_anchor_only_passes()
    {
        var result = StepRenameValidator.ValidateNewName("I have (.*) cukes$", "I have (.*) gourds$");
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_regex_keeps_original_quantifier_passes()
    {
        // The optional-character quantifier in "colou?r" is part of the original expression, not
        // something the rename introduces, so it must survive a wording change.
        var result = StepRenameValidator.ValidateNewName("^colou?r (.*) step$", "^colou?r (.*) action$");
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_regex_keeps_original_operators_with_escaped_digit_group_passes()
    {
        var result = StepRenameValidator.ValidateNewName(@"^I have (\d+) cukes$", @"^I have (\d+) gourds$");
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_regex_adding_a_second_anchor_still_fails()
    {
        // Adding one more of an operator the original already had is still an introduction.
        var result = StepRenameValidator.ValidateNewName("^I have (.*) cukes$", "^^I have (.*) cukes$");
        result.Should().NotBeNull();
        result.Message.Should().Be("The non-parameter parts cannot contain expression operators");
        result.Scope.Should().Be("rename");
    }

    [Fact]
    public void ValidateNewName_regex_adding_an_operator_absent_from_the_original_still_fails()
    {
        var result = StepRenameValidator.ValidateNewName("^I have (.*) cukes$", "^I have (.*) cukes?$");
        result.Should().NotBeNull();
        result.Message.Should().Be("The non-parameter parts cannot contain expression operators");
        result.Scope.Should().Be("rename");
    }

    [Fact]
    public void ValidateNewName_regex_dropping_an_anchor_passes()
    {
        // Removal was never policed (an expression without operators trivially passed the old
        // rule too) - this fix must not start rejecting it.
        var result = StepRenameValidator.ValidateNewName("^I have (.*) cukes$", "I have (.*) gourds$");
        result.Should().BeNull();
    }

    // ── Real-world repros from the Experimental-instance logs (issue #1126) ────
    // The binding is `[Then(@"the basket price should be (\d+(\.\d+)?)")]` in a Quickstart
    // project; Rename Step seeds the prompt with the source literal and submits the edited text
    // verbatim. Both the anchored and the unanchored form were rejected.

    [Fact]
    public void ValidateNewName_regex_keeps_nested_capture_groups_passes()
    {
        var result = StepRenameValidator.ValidateNewName(
            @"the basket price should be (\d+(\.\d+)?)",
            @"the price should be (\d+(\.\d+)?)");
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_regex_keeps_anchors_and_nested_capture_groups_passes()
    {
        var result = StepRenameValidator.ValidateNewName(
            @"^the basket price should be \$(\d+(\.\d+)?)$",
            @"^the price should be \$(\d+(\.\d+)?)$");
        result.Should().BeNull();
    }

    // ── Rule 4, parameter slots are syntax-aware (issue #960) ───────────────────

    [Fact]
    public void ValidateNewName_regex_dropping_a_non_capturing_group_passes()
    {
        // "(?:red|blue)" is not a parameter slot, so dropping it is not a parameter-count change.
        StepRenameValidator.ValidateNewName(@"I have (?:red|blue) (.*) cukes", @"I have (.*) cukes")
            .Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_regex_dropping_a_named_capturing_group_still_fails()
    {
        var result = StepRenameValidator.ValidateNewName(@"I have (?<n>\d+) cukes", "I have cukes");
        result.Should().NotBeNull();
        result.Message.Should().Be("Parameter count mismatch");
    }

    [Fact]
    public void ValidateNewName_regex_dropping_a_real_capturing_group_still_fails()
    {
        var result = StepRenameValidator.ValidateNewName(@"I have (.*) cukes", "I have cukes");
        result.Should().NotBeNull();
        result.Message.Should().Be("Parameter count mismatch");
    }

    [Fact]
    public void ValidateNewName_regex_escaped_parens_are_not_parameters()
    {
        StepRenameValidator.ValidateNewName(@"I have \(literal\) (.*) cukes", @"I have (.*) cukes")
            .Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_cucumber_optional_text_dropped_passes()
    {
        // In a Cucumber Expression "(s)" is optional text, not a parameter.
        StepRenameValidator.ValidateNewName("I have cucumber(s)", "I have cucumber").Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_cucumber_optional_text_kept_passes()
    {
        StepRenameValidator.ValidateNewName("I have cucumber(s)", "I have gherkin(s)").Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_cucumber_optional_text_added_alongside_a_placeholder_passes()
    {
        // The parentheses of matched optional text are syntax, not forbidden operators.
        StepRenameValidator.ValidateNewName("I have {int} cukes", "I have {int} gherkin(s)").Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_cucumber_unmatched_open_paren_still_fails_alongside_optional_text()
    {
        var result = StepRenameValidator.ValidateNewName("I have cucumber(s) {int}", "I have cucumber(s) {int} (");
        result.Should().NotBeNull();
        result.Message.Should().Be("The non-parameter parts cannot contain expression operators");
    }

    [Fact]
    public void ValidateNewName_cucumber_empty_placeholder_does_not_block_rename()
    {
        // "{}" is a valid untyped Cucumber placeholder; it used to count as no slot and its
        // braces were flagged as forbidden operators, rejecting every rename.
        StepRenameValidator.ValidateNewName("I have {} cukes", "I have {} apples").Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_cucumber_dropping_a_real_parameter_still_fails()
    {
        var result = StepRenameValidator.ValidateNewName("I have {int} cukes", "I have cukes");
        result.Should().NotBeNull();
        result.Message.Should().Be("Parameter count mismatch");
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

    // ── Nested groups (issue #1131) ─────────────────────────────────────────────

    [Theory]
    // The whole group is one parameter slot; the wording change introduces nothing.
    [InlineData(@"the basket price should be (\d+(\.\d+)?)", @"the price should be (\d+(\.\d+)?)")]
    [InlineData(@"^the basket price should be (\d+(\.\d+)?)$", @"^the price should be (\d+(\.\d+)?)$")]
    // Adding an optional nested group INSIDE a slot is still a single slot, with no operator left in the text.
    [InlineData(@"the price should be (\d+)", @"the price should be (\d+(\.\d+)?)")]
    [InlineData(@"the price is (\d+(\.(\d+(,\d+)?))?)", @"the cost is (\d+(\.(\d+(,\d+)?))?)")]
    // Non-capturing, named and inline-option groups keep behaving as a single slot each.
    [InlineData(@"I pick (?:a|an) item", @"I choose (?:a|an) item")]
    [InlineData(@"I pick (?<name>\w+(-\w+)?) now", @"I choose (?<name>\w+(-\w+)?) now")]
    [InlineData(@"(?i)I pick (.*)", @"(?i)I choose (.*)")]
    public void ValidateNewName_treats_a_group_with_nested_groups_as_one_parameter_slot(string original, string newName)
    {
        StepRenameValidator.ValidateNewName(original, newName).Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_rejects_a_nested_group_rename_that_changes_the_parameter_count()
    {
        var result = StepRenameValidator.ValidateNewName(
            @"the price should be (\d+(\.\d+)?)", @"the price should be (\d+(\.\d+)?) (.*)");

        result.Should().NotBeNull();
        result.Message.Should().Be("Parameter count mismatch");
    }

    [Fact]
    public void ValidateNewName_rejects_a_quantifier_added_after_a_nested_group()
    {
        // The trailing '?' makes the whole parameter optional and sits outside the slot, so it is an
        // introduced operator (Rule 3), not part of the group.
        var result = StepRenameValidator.ValidateNewName(
            @"the price should be (\d+(\.\d+)?)", @"the price should be (\d+(\.\d+)?)?");

        result.Should().NotBeNull();
        result.Message.Should().Be("The non-parameter parts cannot contain expression operators");
    }

    [Fact]
    public void ValidateNewName_still_rejects_an_operator_introduced_outside_the_nested_group()
    {
        var result = StepRenameValidator.ValidateNewName(
            @"the price should be (\d+(\.\d+)?)", @"the price* should be (\d+(\.\d+)?)");

        result.Should().NotBeNull();
        result.Message.Should().Be("The non-parameter parts cannot contain expression operators");
    }

    [Fact]
    public void ValidateNewName_ignores_escaped_parentheses_when_counting_slots()
    {
        // \( and \) are literal text, so this expression has one real slot, (\d+).
        StepRenameValidator.ValidateNewName(@"the total \((\d+)\)", @"the sum \((\d+)\)").Should().BeNull();
    }

    // ── Rule 4 across several parameter slots (#1131) ───────────────────────────
    // Rule 4 compares the NUMBER of parameter slots, not their identity, so a rename may re-shape
    // or reorder slots as long as the binding keeps the same number of them. An expression with
    // more than one slot is where a truncated slot scan (the #1131 defect) shows up as a wrong
    // verdict rather than a count that happens to come out right on a single-slot expression.

    [Theory]
    // Cucumber-authored: two and three placeholders, wording-only changes (the '$' is a plain
    // literal in a Cucumber Expression - issue #649).
    [InlineData("I have {int} cukes and {int} gourds", "I have {int} apples and {int} pears")]
    [InlineData("the basket has {int} apples, {int} pears and {int} plums", "the cart holds {int} apples, {int} pears and {int} plums")]
    [InlineData("the price of {word} is ${float} and the discount is {float}", "the cost of {word} is ${float} and the rebate is {float}")]
    // Regex-authored: two and three capturing groups, two of them containing a nested group.
    [InlineData(@"^I have (\d+) cukes and (\d+) gourds$", @"^I have (\d+) apples and (\d+) pears$")]
    [InlineData(@"^(\d+(\.\d+)?) to (\d+(\.\d+)?) costs (\d+)$", @"^(\d+(\.\d+)?) from (\d+(\.\d+)?) costs (\d+)$")]
    public void ValidateNewName_several_parameter_slots_with_a_wording_change_pass(string original, string newName)
    {
        StepRenameValidator.ValidateNewName(original, newName).Should().BeNull();
    }

    [Theory]
    // The added group sits inside one of the two slots, so the slot count stays 2 and no operator
    // leaks into the non-parameter text - whichever of the two slots gains the nested group.
    [InlineData(@"^I have (\d+) cukes and (\d+) gourds$", @"^I have (\d+(,\d+)?) cukes and (\d+) gourds$")]
    [InlineData(@"^I have (\d+) cukes and (\d+) gourds$", @"^I have (\d+) cukes and (\d+(,\d+)?) gourds$")]
    public void ValidateNewName_adding_a_nested_group_to_one_of_two_slots_passes(string original, string newName)
    {
        StepRenameValidator.ValidateNewName(original, newName).Should().BeNull();
    }

    [Fact]
    public void ValidateNewName_re_shaping_one_of_two_nested_slots_keeps_the_count_so_it_passes()
    {
        var result = StepRenameValidator.ValidateNewName(
            @"^(\d+(\.\d+)?) to (\d+(\.\d+)?)$",
            @"^(\d+(\.\d+)?) to (\d+(,\d+)?)$");

        result.Should().BeNull();
    }

    [Theory]
    // Dropping one of two Cucumber placeholders, and adding a third.
    [InlineData("I have {int} cukes and {int} gourds", "I have {int} gourds")]
    [InlineData("I have {int} cukes and {int} gourds", "I have {int} cukes, {int} gourds and {int} plums")]
    // Dropping one of two regex groups, and adding a second to a single-slot expression.
    [InlineData(@"^I have (\d+) cukes and (\d+) gourds$", @"^I have (\d+) gourds$")]
    [InlineData(@"^I have (\d+) cukes$", @"^I have (\d+) cukes and (\d+) gourds$")]
    // Three same-shaped slots: removing one is still a mismatch, so the slots are not compared as a set.
    [InlineData(@"^(\d+) (\d+) (\d+)$", @"^(\d+) (\d+)$")]
    // Two slots that each contain a nested group: dropping one of them.
    [InlineData(@"^(\d+(\.\d+)?) to (\d+(\.\d+)?)$", @"^(\d+(\.\d+)?)$")]
    public void ValidateNewName_changing_the_number_of_parameter_slots_fails(string original, string newName)
    {
        var result = StepRenameValidator.ValidateNewName(original, newName);

        result.Should().NotBeNull();
        result.Message.Should().Be("Parameter count mismatch");
        result.Scope.Should().Be("rename");
    }

    [Theory]
    // An operator introduced outside the slots is reported as the Rule 3 failure even when the slot
    // count also changed: Rule 3 is evaluated first, so the message the user sees names the operator.
    [InlineData(@"^I have (\d+) cukes and (\d+) gourds$", @"^I have (\d+) cukes? and (\d+) gourds?$")]
    [InlineData(@"^I have (\d+) cukes and (\d+) gourds$", @"^I have (\d+) cukes?$")]
    public void ValidateNewName_operator_introduced_outside_several_slots_reports_the_operator(string original, string newName)
    {
        var result = StepRenameValidator.ValidateNewName(original, newName);

        result.Should().NotBeNull();
        result.Message.Should().Be("The non-parameter parts cannot contain expression operators");
        result.Scope.Should().Be("rename");
    }

    [Fact]
    public void ValidateNewName_reordering_two_slots_keeps_the_count_so_it_passes()
    {
        // Rule 4 is a count, not a positional comparison - swapping two slots or changing a
        // placeholder's type keeps the count and is accepted.
        StepRenameValidator.ValidateNewName(
            @"^I have (\d+) cukes and (\w+) gourds$",
            @"^I have (\w+) gourds and (\d+) cukes$").Should().BeNull();

        StepRenameValidator.ValidateNewName(
            "I have {int} cukes and {word} gourds",
            "I have {float} cukes and {word} gourds").Should().BeNull();
    }
}
