namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

/// <summary>
/// Unit tests for the Roslyn-based (source-level) binding discovery (design doc feature F2).
/// These exercise the critical paths directly against <see cref="StepDefinitionFileParser"/>
/// without requiring the connector / discovery pipeline.
/// </summary>
public class StepDefinitionFileParserTests
{
    private const string FilePath = @"C:\Project\Steps.cs";

    private static Task<StepDefinitionFileBindings> ParseBindings(string body)
    {
        var content = $@"
using Reqnroll;
namespace TestProject
{{
    [Binding]
    public class Steps
    {{
{body}
    }}
}}";
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        return new StepDefinitionFileParser().ParseBindings(file);
    }

    private static async Task<List<ProjectStepDefinitionBinding>> ParseStepDefinitions(string body) =>
        (await ParseBindings(body)).StepDefinitions.ToList();

    [Theory]
    [InlineData("Given", ScenarioBlock.Given)]
    [InlineData("When", ScenarioBlock.When)]
    [InlineData("Then", ScenarioBlock.Then)]
    public async Task Discovers_each_step_definition_attribute_kind(string attribute, ScenarioBlock expectedBlock)
    {
        var stepDefinitions = await ParseStepDefinitions(
            $@"[{attribute}(""I do something"")]
               public void Method() {{ }}");

        stepDefinitions.Should().ContainSingle();
        var binding = stepDefinitions[0];
        binding.StepDefinitionType.Should().Be(expectedBlock);
        binding.Expression.Should().Be("I do something");
        binding.Regex.ToString().Should().Be("^I do something$");
        binding.IsValid.Should().BeTrue();
        binding.Implementation.Method.Should().Be("TestProject.Steps.Method");
    }

    [Fact]
    public async Task StepDefinition_attribute_registers_for_all_three_blocks()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[StepDefinition(""shared step"")]
              public void Method() { }");

        stepDefinitions.Select(b => b.StepDefinitionType)
            .Should().BeEquivalentTo(new[] { ScenarioBlock.Given, ScenarioBlock.When, ScenarioBlock.Then });
        stepDefinitions.Should().OnlyContain(b => b.Expression == "shared step");
    }

    [Fact]
    public async Task Discovers_multiple_attributes_on_one_method()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""a"")]
              [When(""a"")]
              public void Method() { }");

        stepDefinitions.Select(b => b.StepDefinitionType)
            .Should().BeEquivalentTo(new[] { ScenarioBlock.Given, ScenarioBlock.When });
    }

    [Theory]
    [InlineData("GivenAttribute")]
    [InlineData("Reqnroll.Given")]
    [InlineData("global::Reqnroll.GivenAttribute")]
    public async Task Recognizes_qualified_and_suffixed_attribute_names(string attribute)
    {
        var stepDefinitions = await ParseStepDefinitions(
            $@"[{attribute}(""qualified"")]
               public void Method() {{ }}");

        stepDefinitions.Should().ContainSingle()
            .Which.StepDefinitionType.Should().Be(ScenarioBlock.Given);
    }

    [Theory]
    [InlineData("[Binding]")]
    [InlineData("[Obsolete]")]
    [InlineData("[Test]")]
    [InlineData("[CustomThing(\"x\", 5)]")]
    public async Task Ignores_unrelated_attributes_without_throwing(string attribute)
    {
        var stepDefinitions = await ParseStepDefinitions(
            $@"{attribute}
               public void Method() {{ }}");

        stepDefinitions.Should().BeEmpty();
    }

    [Fact]
    public async Task Handles_expression_bodied_method_without_crashing()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[When(""pressed"")]
              public void Method() => System.Console.WriteLine(""x"");");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Expression.Should().Be("pressed");
        binding.Implementation.SourceLocation.Should().NotBeNull();
    }

    [Fact]
    public async Task Reads_expression_from_named_argument()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[When(Expression = ""named expression"")]
              public void Method() { }");

        stepDefinitions.Should().ContainSingle()
            .Which.Expression.Should().Be("named expression");
    }

    [Fact]
    public async Task Ignores_extra_named_arguments_alongside_expression()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""the expression"", Culture = ""en-US"")]
              public void Method() { }");

        stepDefinitions.Should().ContainSingle()
            .Which.Expression.Should().Be("the expression");
    }

    [Fact]
    public async Task Reads_verbatim_string_expression()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(@""path \to\ thing"")]
              public void Method() { }");

        stepDefinitions.Should().ContainSingle()
            .Which.Expression.Should().Be(@"path \to\ thing");
    }

    [Fact]
    public async Task Parameterless_attribute_derives_regex_from_method_name()
    {
        // Issue #268: an attribute with no expression is a method-name-style binding at
        // runtime (Reqnroll.Bindings.StepDefinitionRegexCalculator), not an invalid one.
        var stepDefinitions = await ParseStepDefinitions(
            @"[When]
              public void Method() { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.StepDefinitionType.Should().Be(ScenarioBlock.When);
        binding.Regex.Should().NotBeNull();
        binding.IsValid.Should().BeTrue("the method name itself is a valid, if trivial, matching expression");
        binding.Regex!.IsMatch("Method").Should().BeTrue();
    }

    [Fact]
    public async Task Method_name_binding_reproduces_issue_268_example()
    {
        // [Given] public void The_First_Number_Is_P0(int value) — the trailing "_P0" identifies
        // the position-0 parameter by its positional placeholder.
        // NOTE: the issue's own example names the parameter "p", but the runtime algorithm
        // being ported here (Reqnroll.Bindings.StepDefinitionRegexCalculator.CalculateParamPosition)
        // tries a parameter's *own* name before its "P{index}" placeholder, and a parameter
        // literally named "p" collides with the leading "P" of "P0" — only "_P" would then be
        // consumed, leaving a stray literal "0" that must match too. That collision is inherent
        // to Reqnroll's real algorithm (faithfully reproduced here, not something this port
        // introduces); a non-colliding parameter name is used to demonstrate the P{index}
        // convention working as intended.
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given]
              public void The_First_Number_Is_P0(int value) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.IsValid.Should().BeTrue();
        binding.Regex!.IsMatch("the first number is 42").Should().BeTrue();
        binding.Regex.Match("the first number is 42").Groups["value"].Value.Should().Be("42");
    }

    [Fact]
    public async Task Method_name_binding_strips_the_block_keyword_prefix()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given]
              public void GivenAPrecondition() { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Regex!.IsMatch("a precondition").Should().BeTrue(
            "the leading 'Given' should be stripped as the block keyword, not matched literally");
    }

    [Fact]
    public async Task Method_name_binding_splits_on_camel_case_and_underscore_word_boundaries()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[When]
              public void The_UserLogsIn() { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Regex!.IsMatch("the user logs in").Should().BeTrue();
    }

    [Fact]
    public async Task Method_name_binding_locates_parameter_by_its_own_name()
    {
        // The placeholder segment must be ALL CAPS to be recognized by name — the runtime
        // algorithm uppercases the parameter name but matches it case-sensitively against the
        // (otherwise mixed-case) method name, so the leading mixed-case "Result" is just an
        // ordinary word; only the trailing all-caps "RESULT" is recognized as the placeholder.
        var stepDefinitions = await ParseStepDefinitions(
            @"[Then]
              public void The_Result_Is_RESULT(string result) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        var match = binding.Regex!.Match("the result is success");
        match.Success.Should().BeTrue();
        match.Groups["result"].Value.Should().Be("success");
    }

    [Fact]
    public async Task Method_name_binding_is_computed_per_block_for_StepDefinition_attribute()
    {
        // [StepDefinition] registers the same method for Given/When/Then simultaneously; the
        // prefix stripped depends on which block is being matched.
        var stepDefinitions = await ParseStepDefinitions(
            @"[StepDefinition]
              public void WhenSomethingHappens() { }");

        var whenBinding = stepDefinitions.Single(b => b.StepDefinitionType == ScenarioBlock.When);
        var givenBinding = stepDefinitions.Single(b => b.StepDefinitionType == ScenarioBlock.Given);

        whenBinding.Regex!.IsMatch("something happens").Should().BeTrue(
            "the 'When' prefix should be stripped when matching as a When step");
        givenBinding.Regex!.IsMatch("something happens").Should().BeFalse(
            "no 'Given' prefix is present on the method name, so it shouldn't be stripped when matching as a Given step");
    }

    // The following tests mirror Reqnroll's own runtime coverage for this algorithm
    // (Tests/Reqnroll.RuntimeTests/Bindings/StepDefinitionRegexCalculatorTests.cs) to verify
    // this port reproduces the same matching behavior, not just the same happy-path shape.

    [Theory]
    [InlineData("When_I_do")]
    [InlineData("WhenIDo")]
    [InlineData("When_do_something")]
    [InlineData("WhenDoSomething")]
    [InlineData("When_do")]
    public async Task Method_name_binding_is_anchored_and_does_not_match_a_substring(string methodName)
    {
        var stepDefinitions = await ParseStepDefinitions(
            $@"[When]
               public void {methodName}() {{ }}");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Regex!.IsMatch("I do something").Should().BeFalse(
            $"'{methodName}' only covers part of the step text, so the anchored regex should not match");
    }

    [Theory]
    [InlineData("When_WHO_does_WHAT_with")]
    [InlineData("WhenWHODoesWHATWith")]
    [InlineData("When_P0_does_P1_with")]
    public async Task Method_name_binding_supports_multiple_parameters(string methodName)
    {
        var stepDefinitions = await ParseStepDefinitions(
            $@"[When]
               public void {methodName}(string who, string what) {{ }}");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        var match = binding.Regex!.Match("Joe does something with");
        match.Success.Should().BeTrue();
        match.Groups["who"].Value.Should().Be("Joe");
        match.Groups["what"].Value.Should().Be("something");
    }

    [Fact]
    public async Task Method_name_binding_ignores_a_parameter_not_reflected_in_the_method_name()
    {
        // Mirrors Reqnroll's SupportsExtraArguments (a trailing Table-typed parameter that
        // never appears in the step text) — any parameter the algorithm can't locate in the
        // method name is simply left out of the regex rather than breaking the match.
        var stepDefinitions = await ParseStepDefinitions(
            @"[When]
              public void When_WHO_does_something(string who, string table) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        var match = binding.Regex!.Match("Joe does something");
        match.Success.Should().BeTrue();
        match.Groups["who"].Value.Should().Be("Joe");
    }

    [Theory]
    [InlineData("that:Joe,does;something.?!")]
    [InlineData("that : Joe , does ; something .?! ")]
    [InlineData("that -Joe - does -something-")]
    [InlineData("that' Joe does \"something\"")]
    public async Task Method_name_binding_word_connector_absorbs_punctuation_between_words(string stepText)
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[When]
              public void When_that_WHO_does_something(string who) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        var match = binding.Regex!.Match(stepText);
        match.Success.Should().BeTrue();
        match.Groups["who"].Value.Should().Be("Joe");
    }

    [Theory]
    [InlineData("!that does not work")]
    [InlineData(" that does not work")]
    public async Task Method_name_binding_does_not_allow_leading_whitespace_or_punctuation(string stepText)
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[When]
              public void When_that_does_not_work() { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Regex!.IsMatch(stepText).Should().BeFalse();
    }

    [Theory]
    [InlineData("this doesn't work", "this_doesnt_work", false)]
    [InlineData("this doesn't work", "this_doesn_t_work", true)]
    [InlineData("this does not work", "this_does_not_work", true)]
    public async Task Method_name_binding_apostrophed_shortenings_need_explicit_underscores(
        string stepText, string methodName, bool shouldMatch)
    {
        // An apostrophe is not a word boundary the algorithm splits on, so "doesnt" (no
        // apostrophe, no underscore) won't match "doesn't" — only an explicit underscore
        // ("doesn_t") introduces the boundary needed to match it.
        var stepDefinitions = await ParseStepDefinitions(
            $@"[When]
               public void {methodName}() {{ }}");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Regex!.IsMatch(stepText).Should().Be(shouldMatch);
    }

    [Theory]
    [InlineData("I_do_something")]
    [InlineData("IDoSomething")]
    public async Task Method_name_binding_works_when_the_block_keyword_is_entirely_absent(string methodName)
    {
        var stepDefinitions = await ParseStepDefinitions(
            $@"[When]
               public void {methodName}() {{ }}");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Regex!.IsMatch("I do something").Should().BeTrue(
            "stripping the block keyword prefix is opportunistic, not required — the whole method name should still match when the keyword is absent");
    }

    [Fact]
    public async Task Captures_parameter_types()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""I have (\d+) and (.*)"")]
              public void Method(int count, string name) { }");

        stepDefinitions.Should().ContainSingle()
            .Which.Implementation.ParameterTypes.Should().Equal("int", "string");
    }

    [Theory]
    [InlineData("Given", @"the number is {int}",  "the number is 42")]
    [InlineData("When",  @"the value is {float}", "the value is 3.14")]
    [InlineData("Then",  @"the word is {word}",   "the word is hello")]
    public async Task Standard_cucumber_param_types_are_converted_to_regex(
        string keyword, string expression, string stepText)
    {
        // The exact regex fragment for each type is Cucumber.CucumberExpressions' own (the same
        // library Reqnroll's runtime depends on) rather than something this parser controls, so
        // only the resulting match behaviour is asserted here.
        var stepDefinitions = await ParseStepDefinitions(
            $@"[{keyword}(""{expression}"")]
               public void Method() {{ }}");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.IsValid.Should().BeTrue();
        binding.Regex.IsMatch(stepText).Should().BeTrue(
            $"the converted regex should match the step text '{stepText}'");
    }

    [Fact]
    public async Task Word_param_type_does_not_match_across_a_space()
    {
        // {word} matches a single non-whitespace run, not an arbitrary phrase.
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""the word is {word}"")]
              public void Method(string value) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Regex!.IsMatch("the word is hello world").Should().BeFalse();
    }

    [Fact]
    public async Task Literal_dollar_sign_before_a_placeholder_is_matched_verbatim()
    {
        // Reproduces the live bug: [Then("the basket price should be ${float}")] has a
        // literal '$' immediately before the placeholder. Only {float} was substituted;
        // the literal '$' was spliced into the pattern unescaped, so it was interpreted as
        // a regex end-of-string anchor mid-pattern instead of matching a literal '$'. That
        // made the binding permanently unmatchable — even though the connector's own
        // runtime-computed regex matched fine — so the step stayed flagged "Step
        // definition not found" forever, no matter how many times bindings were
        // rediscovered.
        var stepDefinitions = await ParseStepDefinitions(
            @"[Then(""the basket price should be ${float}"")]
              public void Method(decimal value) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.IsValid.Should().BeTrue();
        binding.Regex!.IsMatch("the basket price should be $180.0").Should().BeTrue(
            "the literal '$' should be matched verbatim, not treated as an end-of-string anchor");
    }

    [Fact]
    public async Task Literal_regex_metacharacters_around_a_placeholder_are_escaped()
    {
        // Once a {param} placeholder makes this a Cucumber expression, a literal '.' in the
        // surrounding text must not act as a regex wildcard matching any character — it
        // should only match a literal dot, same as the '$' case above.
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""the price is 3.5{int} dollars"")]
              public void Method(int cents) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.IsValid.Should().BeTrue();
        binding.Regex!.IsMatch("the price is 3.599 dollars").Should().BeTrue();
        binding.Regex!.IsMatch("the price is 3X599 dollars").Should().BeFalse(
            "a literal '.' must not act as a wildcard matching any character");
    }

    [Fact]
    public async Task Expression_with_no_placeholder_is_still_used_as_a_plain_regex()
    {
        // Guards the other half of the contract: an expression with no {param} at all keeps
        // being treated as an already-valid raw regex, unescaped — e.g. an existing binding
        // written as [Given("the number is (\\d+)")] must keep working as a capturing group,
        // not be turned into literal parenthesis text.
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""the number is (\\d+)"")]
              public void Method(int n) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.IsValid.Should().BeTrue();
        binding.Regex!.ToString().Should().Be(@"^the number is (\d+)$");
        binding.Regex!.IsMatch("the number is 42").Should().BeTrue();
    }

    [Fact]
    public async Task Custom_cucumber_param_type_falls_back_to_wildcard_and_matches_step()
    {
        // Reproduces the live bug: [When("the two numbers {Verb} added")] uses a custom
        // step-argument transformation type 'Verb'. Without proper conversion the Roslyn
        // binding's regex was ^the two numbers {Verb} added$, which matches the literal
        // text "{Verb}" rather than an actual value — causing the When step to appear
        // unbound even though only the Given expression was edited.
        var stepDefinitions = await ParseStepDefinitions(
            @"[When(""the two numbers {Verb} added"")]
              public void Method(string verb) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.IsValid.Should().BeTrue();
        binding.Regex.ToString().Should().Contain(@"(.*)");
        binding.Regex.IsMatch("the two numbers 'are' added").Should().BeTrue();
        binding.Regex.IsMatch("the two numbers were added").Should().BeTrue();
    }

    [Fact]
    public async Task Malformed_cucumber_expression_yields_an_invalid_binding_instead_of_throwing()
    {
        // The real Cucumber Expression grammar's own validation (e.g. "an alternative may not
        // be empty") can reject input the old hand-rolled parser never did. One malformed
        // attribute must degrade to an invalid (null-regex) binding rather than throwing and
        // aborting discovery of every other binding in the file.
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""a cat/{int}"")]
              public void Method(int n) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.IsValid.Should().BeFalse("an alternative containing only a parameter is invalid Cucumber Expression syntax");
        binding.Regex.Should().BeNull();
    }

    [Fact]
    public async Task Short_is_not_a_recognized_cucumber_parameter_type_name()
    {
        // Reqnroll's own registry only aliases int/float/double/byte/long/decimal to their C#
        // keywords -- {short} resolves solely via the CLR type name Int16
        // (RuntimeBindingType.Name => Type.Name), so it's undefined in a real Reqnroll project.
        // Recognizing "short" here would falsely validate it via Roslyn discovery while the
        // connector-discovered path leaves it undefined -- the same class of divergence #476
        // exists to fix, just for a different name.
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""the count is {short}"")]
              public void Method(short n) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.IsValid.Should().BeTrue("unknown types fall back to a wildcard rather than being rejected");
        binding.Regex!.IsMatch("the count is anything").Should().BeTrue(
            "an unrecognized type name falls back to matching any text, not a digits-only pattern");
    }

    [Theory]
    [InlineData("I have a cat/dog", "I have a cat", true)]
    [InlineData("I have a cat/dog", "I have a dog", true)]
    [InlineData("I have a cat/dog", "I have a cat/dog", false)]
    [InlineData("I have a cat/dog", "I have a fish", false)]
    public async Task Alternative_text_matches_either_alternative_not_the_literal_slash(
        string expression, string stepText, bool shouldMatch)
    {
        // Reproduces issue #476: a Cucumber Expression using '/' alternative text (e.g.
        // "cat/dog") must match either alternative, the same as the connector-discovered
        // regex computed by Reqnroll's runtime cucumber-expressions library. Since there's
        // no {param} placeholder here, plant one so the expression is still recognized as a
        // Cucumber expression rather than a plain regex.
        var stepDefinitions = await ParseStepDefinitions(
            $@"[Given(""{expression} {{int}}"")]
               public void Method(int n) {{ }}");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Regex!.IsMatch($"{stepText} 1").Should().Be(shouldMatch);
    }

    [Theory]
    [InlineData("I eat(s) apples", "I eat apples", true)]
    [InlineData("I eat(s) apples", "I eats apples", true)]
    [InlineData("I eat(s) apples", "I eat(s) apples", false)]
    public async Task Optional_text_makes_the_parenthesized_text_optional_not_literal_parens(
        string expression, string stepText, bool shouldMatch)
    {
        // Reproduces issue #476: a Cucumber Expression using '(text)' optional text must make
        // the parenthesized text optional, not match the literal parentheses.
        var stepDefinitions = await ParseStepDefinitions(
            $@"[Given(""{expression} and {{int}}"")]
               public void Method(int n) {{ }}");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Regex!.IsMatch($"{stepText} and 1").Should().Be(shouldMatch);
    }

    [Fact]
    public async Task Alternative_and_optional_text_can_combine_in_the_same_expression()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""{int} red/blue disc(s)"")]
              public void Method(int n) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Regex!.IsMatch("3 red discs").Should().BeTrue();
        binding.Regex!.IsMatch("1 blue disc").Should().BeTrue();
        binding.Regex!.IsMatch("2 green discs").Should().BeFalse();
    }

    [Fact]
    public async Task Backslash_escapes_alternation_and_optional_syntax_to_literal_characters()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""a literal cat\\/dog and \\(parens\\) plus {int}"")]
              public void Method(int n) { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.Regex!.IsMatch("a literal cat/dog and (parens) plus 1").Should().BeTrue();
        binding.Regex!.IsMatch("a literal cat plus 1").Should().BeFalse();
    }

    [Fact]
    public async Task Source_location_spans_the_full_method_identifier()
    {
        // Issue #514: the range must span the whole identifier (not be zero-width) so consumers
        // like .cs diagnostics squiggles don't need to re-derive the span via a live-buffer text
        // search. ToLspLocation() (used for textDocument/definition) still discards the end
        // position and renders zero-width, so this doesn't change Go to Definition's behavior.
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""x"")]
              public void Method()
              {
              }");

        var location = stepDefinitions.Should().ContainSingle().Subject!.Implementation.SourceLocation!;
        location.SourceFile.Should().Be(FilePath);
        location.HasEndPosition.Should().BeTrue();
        location.SourceFileLine.Should().Be(location.SourceFileEndLine!.Value);
        (location.SourceFileEndColumn!.Value - location.SourceFileColumn).Should().Be("Method".Length);
    }

    [Fact]
    public async Task Source_location_points_to_method_name_not_attribute_or_body()
    {
        // Attribute on body-line 1 (template line 8), method identifier on body-line 2 (template line 9).
        // The location must land on "TargetMethod", not on "[Given]" (line 8) or on "{" (line 10).
        // Template adds 7 header lines before the body, so:
        //   line 8  = [Given("x")]
        //   line 9  = public void TargetMethod() { }   <- identifier at column 13 (1-based)
        var stepDefinitions = await ParseStepDefinitions(
            "[Given(\"x\")]\npublic void TargetMethod() { }");

        var location = stepDefinitions.Should().ContainSingle().Subject!.Implementation.SourceLocation!;
        location.SourceFileLine.Should().Be(9);      // method signature line, not attribute (line 8)
        location.SourceFileColumn.Should().Be(13);   // "public void " = 12 chars → identifier at col 13
        location.SourceFileEndLine.Should().Be(9);
        location.SourceFileEndColumn.Should().Be(13 + "TargetMethod".Length);
    }

    // ── Structural validation (issue #514) ────────────────────────────────────────
    // Ports Reqnroll.Bindings.Discovery.BindingSourceProcessor.ValidateType/ValidateMethod/
    // ValidateHook. A binding with a structural error is excluded from matching (IsValid is
    // false whenever Error is set — see ProjectBinding.IsValid) exactly like a connector-reported
    // import error, riding the same mechanism rather than a separate one.

    private static async Task<StepDefinitionFileBindings> ParseRaw(string content)
    {
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        return await new StepDefinitionFileParser().ParseBindings(file);
    }

    [Fact]
    public async Task Struct_binding_type_is_reported_invalid()
    {
        var bindings = await ParseRaw(@"
namespace TestProject
{
    [Binding]
    public struct Steps
    {
        [Given(""x"")]
        public void Method() { }
    }
}");
        var binding = bindings.StepDefinitions.Should().ContainSingle().Subject!;
        binding.Error.Should().Contain("must be a class");
        binding.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Generic_binding_type_is_reported_invalid()
    {
        var bindings = await ParseRaw(@"
namespace TestProject
{
    [Binding]
    public class Steps<T>
    {
        [Given(""x"")]
        public void Method() { }
    }
}");
        var binding = bindings.StepDefinitions.Should().ContainSingle().Subject!;
        binding.Error.Should().Contain("must not be a generic type definition");
        binding.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Non_static_method_on_abstract_binding_type_is_reported_invalid()
    {
        var bindings = await ParseRaw(@"
namespace TestProject
{
    [Binding]
    public abstract class Steps
    {
        [Given(""x"")]
        public void Method() { }
    }
}");
        var binding = bindings.StepDefinitions.Should().ContainSingle().Subject!;
        binding.Error.Should().Contain("must be static");
        binding.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Static_method_on_abstract_binding_type_is_valid()
    {
        var bindings = await ParseRaw(@"
namespace TestProject
{
    [Binding]
    public abstract class Steps
    {
        [Given(""x"")]
        public static void Method() { }
    }
}");
        var binding = bindings.StepDefinitions.Should().ContainSingle().Subject!;
        binding.Error.Should().BeNull();
        binding.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Async_void_method_is_reported_invalid()
    {
        var bindings = await ParseRaw(@"
namespace TestProject
{
    [Binding]
    public class Steps
    {
        [Given(""x"")]
        public async void Method() { await System.Threading.Tasks.Task.Delay(1); }
    }
}");
        var binding = bindings.StepDefinitions.Should().ContainSingle().Subject!;
        binding.Error.Should().Contain("must not be async void");
        binding.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Async_Task_method_is_valid()
    {
        var bindings = await ParseRaw(@"
namespace TestProject
{
    [Binding]
    public class Steps
    {
        [Given(""x"")]
        public async System.Threading.Tasks.Task Method() { await System.Threading.Tasks.Task.Delay(1); }
    }
}");
        var binding = bindings.StepDefinitions.Should().ContainSingle().Subject!;
        binding.Error.Should().BeNull();
        binding.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("BeforeTestRun")]
    [InlineData("AfterTestRun")]
    [InlineData("BeforeFeature")]
    [InlineData("AfterFeature")]
    public async Task Non_static_non_scenario_scoped_hook_is_reported_invalid(string attribute)
    {
        var bindings = await ParseRaw($@"
namespace TestProject
{{
    [Binding]
    public class Hooks
    {{
        [{attribute}]
        public void Method() {{ }}
    }}
}}");
        var hook = bindings.Hooks.Should().ContainSingle().Subject!;
        hook.Error.Should().Contain("must be static");
        hook.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("BeforeTestRun")]
    [InlineData("AfterTestRun")]
    [InlineData("BeforeFeature")]
    [InlineData("AfterFeature")]
    public async Task Static_non_scenario_scoped_hook_is_valid(string attribute)
    {
        var bindings = await ParseRaw($@"
namespace TestProject
{{
    [Binding]
    public class Hooks
    {{
        [{attribute}]
        public static void Method() {{ }}
    }}
}}");
        var hook = bindings.Hooks.Should().ContainSingle().Subject!;
        hook.Error.Should().BeNull();
        hook.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Non_static_scenario_scoped_hook_is_valid()
    {
        // BeforeScenario/AfterScenario etc. run against a scenario-scoped instance, so they're
        // exempt from the static-only rule that applies to the four non-scenario-scoped hooks.
        var bindings = await ParseRaw(@"
namespace TestProject
{
    [Binding]
    public class Hooks
    {
        [BeforeScenario]
        public void Method() { }
    }
}");
        var hook = bindings.Hooks.Should().ContainSingle().Subject!;
        hook.Error.Should().BeNull();
        hook.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Captures_attribute_source_line_for_ast_based_matching()
    {
        // Attribute on body-line 1 (template line 8), method identifier on body-line 2 (template
        // line 9) — see Source_location_points_to_method_name_not_attribute_or_body for the
        // template-offset accounting. AttributeSourceLine must point at the attribute itself
        // (line 8), distinct from Implementation.SourceLocation which points at the method (line 9).
        var stepDefinitions = await ParseStepDefinitions(
            "[Given(\"x\")]\npublic void TargetMethod() { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.AttributeSourceLine.Should().Be(8);
        binding.Implementation.SourceLocation!.SourceFileLine.Should().Be(9);
    }

    [Fact]
    public async Task Captures_attribute_source_line_when_attribute_and_method_share_a_line()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Given(""x"")] public void Method() { }");

        var binding = stepDefinitions.Should().ContainSingle().Subject!;
        binding.AttributeSourceLine.Should().Be(binding.Implementation.SourceLocation!.SourceFileLine);
    }

    [Fact]
    public async Task Captures_distinct_attribute_source_line_per_attribute_on_the_same_method()
    {
        // Stacked attributes each occupy their own line — each resulting binding must record
        // its own attribute's line, not the method's or another attribute's.
        var stepDefinitions = await ParseStepDefinitions(
            "[Given(\"a\")]\n[When(\"a\")]\npublic void Method() { }");

        var given = stepDefinitions.Should().ContainSingle(b => b.StepDefinitionType == ScenarioBlock.Given).Subject!;
        var when = stepDefinitions.Should().ContainSingle(b => b.StepDefinitionType == ScenarioBlock.When).Subject!;

        given.AttributeSourceLine.Should().Be(8);
        when.AttributeSourceLine.Should().Be(9);
    }

    [Fact]
    public async Task Discovers_scope_on_method()
    {
        var stepDefinitions = await ParseStepDefinitions(
            @"[Scope(Tag = ""@web"")]
              [Given(""scoped"")]
              public void Method() { }");

        var scope = stepDefinitions.Should().ContainSingle().Subject!.Scope;
        scope.Should().NotBeNull();
        scope.Tag.Should().NotBeNull();
        scope.Tag!.Evaluate(new[] { "@web" }).Should().BeTrue();
        scope.Tag!.Evaluate(new[] { "@other" }).Should().BeFalse();
    }

    [Fact]
    public async Task Combines_class_level_and_method_level_scope()
    {
        // Class scope @ui AND method scope @smoke -> only matches when both tags are present.
        var content = @"
namespace TestProject
{
    [Binding]
    [Scope(Tag = ""@ui"")]
    public class Steps
    {
        [Scope(Tag = ""@smoke"")]
        [Given(""scoped"")]
        public void Method() { }
    }
}";
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        var bindings = await new StepDefinitionFileParser().ParseBindings(file);

        var scope = bindings.StepDefinitions.Should().ContainSingle().Subject!.Scope;
        scope.Tag!.Evaluate(new[] { "@ui", "@smoke" }).Should().BeTrue();
        scope.Tag!.Evaluate(new[] { "@ui" }).Should().BeFalse();
        scope.Tag!.Evaluate(new[] { "@smoke" }).Should().BeFalse();
    }

    [Fact]
    public async Task Discovers_hooks_with_type()
    {
        var bindings = await ParseBindings(
            @"[BeforeScenario]
              public void Setup() { }
              [AfterScenario]
              public void Teardown() { }");

        bindings.Hooks.Select(h => h.HookType)
            .Should().BeEquivalentTo(new[] { HookType.BeforeScenario, HookType.AfterScenario });
        bindings.StepDefinitions.Should().BeEmpty();
    }

    [Fact]
    public async Task Before_and_after_are_synonyms_for_scenario_hooks()
    {
        var bindings = await ParseBindings(
            @"[Before]
              public void Setup() { }
              [After]
              public void Teardown() { }");

        bindings.Hooks.Select(h => h.HookType)
            .Should().BeEquivalentTo(new[] { HookType.BeforeScenario, HookType.AfterScenario });
    }

    [Fact]
    public async Task Reads_hook_order()
    {
        var bindings = await ParseBindings(
            @"[BeforeScenario(Order = 42)]
              public void Setup() { }");

        bindings.Hooks.Should().ContainSingle().Which.HookOrder.Should().Be(42);
    }

    [Fact]
    public async Task Default_hook_order_is_applied_when_unspecified()
    {
        var bindings = await ParseBindings(
            @"[BeforeScenario]
              public void Setup() { }");

        bindings.Hooks.Should().ContainSingle()
            .Which.HookOrder.Should().Be(ProjectHookBinding.DefaultHookOrder);
    }

    [Fact]
    public async Task Discovers_hook_tags_as_scope()
    {
        var bindings = await ParseBindings(
            @"[BeforeScenario(""web"", ""smoke"")]
              public void Setup() { }");

        var scope = bindings.Hooks.Should().ContainSingle().Subject!.Scope;
        scope.Should().NotBeNull();
        scope.Tag!.Evaluate(new[] { "@web" }).Should().BeTrue();
        scope.Tag!.Evaluate(new[] { "@smoke" }).Should().BeTrue();
        scope.Tag!.Evaluate(new[] { "@other" }).Should().BeFalse();
    }

    [Fact]
    public async Task Discovers_bindings_in_file_scoped_namespace()
    {
        var content = @"
namespace TestProject;

[Binding]
public class Steps
{
    [Given(""x"")]
    public void Method() { }
}";
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        var bindings = await new StepDefinitionFileParser().ParseBindings(file);

        bindings.StepDefinitions.Should().ContainSingle()
            .Which.Implementation.Method.Should().Be("TestProject.Steps.Method");
    }

    [Fact]
    public async Task Discovers_bindings_without_namespace()
    {
        var content = @"
[Binding]
public class Steps
{
    [Given(""x"")]
    public void Method() { }
}";
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        var bindings = await new StepDefinitionFileParser().ParseBindings(file);

        bindings.StepDefinitions.Should().ContainSingle()
            .Which.Implementation.Method.Should().Be("Steps.Method");
    }

    [Fact(Skip = "Documents a known F2 limitation: Roslyn (source-level) discovery is syntactic " +
                 "only and cannot resolve attributes that derive from a Reqnroll binding attribute. " +
                 "These are still discovered by the reflection Connector after a build. See the F2 " +
                 "'Known limitations' section in docs/LSP-IDE-Support-Design.md. Not addressed at this time.")]
    public async Task Does_not_discover_custom_attribute_derived_from_reqnroll_attribute()
    {
        // GivenWebAttribute derives from GivenAttribute, but the parser only matches by attribute
        // name (no semantic model), so [GivenWeb] is not recognized as a Given step definition.
        var content = @"
namespace TestProject
{
    public class GivenWebAttribute : GivenAttribute
    {
        public GivenWebAttribute(string expression) : base(expression) { }
    }

    [Binding]
    public class Steps
    {
        [GivenWeb(""I am on the web"")]
        public void Method() { }
    }
}";
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        var bindings = await new StepDefinitionFileParser().ParseBindings(file);

        // Desired (currently unsupported) behaviour:
        bindings.StepDefinitions.Should().ContainSingle()
            .Which.StepDefinitionType.Should().Be(ScenarioBlock.Given);
    }

    [Fact]
    public async Task ReplaceBindings_replaces_step_definitions_and_hooks_for_a_single_file_only()
    {
        const string otherFilePath = @"C:\Project\Other.cs";

        var changedFile = FileDetails.FromPath(FilePath).WithCSharpContent(@"
namespace TestProject
{
    [Binding]
    public class Changed
    {
        [When(""new expression"")]
        public void Method() { }

        [BeforeScenario]
        public void Setup() { }
    }
}");

        // Registry pre-populated with stale bindings for the changed file plus bindings owned by another file.
        var registry = new ProjectBindingRegistry(
            new[]
            {
                BuildStepDefinition("^stale$", "Changed.Method", FilePath),
                BuildStepDefinition("^other$", "Other.Method", otherFilePath)
            },
            new[]
            {
                BuildHook(HookType.AfterScenario, "Changed.OldHook", FilePath),
                BuildHook(HookType.BeforeFeature, "Other.Hook", otherFilePath)
            },
            projectHash: 0);

        var updated = await registry.ReplaceBindings(changedFile);

        // The changed file's bindings are rediscovered from source...
        updated.StepDefinitions
            .Should().ContainSingle(b => b.Implementation.SourceLocation!.SourceFile == FilePath)
            .Which.Regex.ToString().Should().Be("^new expression$");
        updated.Hooks
            .Should().ContainSingle(h => h.Implementation.SourceLocation!.SourceFile == FilePath)
            .Which.HookType.Should().Be(HookType.BeforeScenario);

        // ...while bindings owned by other files are left untouched.
        updated.StepDefinitions.Should().ContainSingle(b => b.Implementation.SourceLocation!.SourceFile == otherFilePath);
        updated.Hooks.Should().ContainSingle(h => h.Implementation.SourceLocation!.SourceFile == otherFilePath);
    }

    [Fact]
    public async Task ReplaceBindings_replaces_same_file_bindings_despite_drive_letter_case()
    {
        // Reproduces the live bug: the reflection connector records the source path with an
        // upper-case drive letter (from the PDB), while the Roslyn update arrives via a document
        // URI carrying a lower-case drive letter. They are the same physical file and the stale
        // binding must be replaced — otherwise the step keeps matching the old expression.
        const string connectorPath = @"C:\Project\Steps.cs";
        var changedFile = FileDetails.FromPath(@"c:\Project\Steps.cs").WithCSharpContent(@"
namespace S
{
    [Binding]
    public class Steps
    {
        [Given(""the firs number is (.*)"")]
        public void Method(int n) { }
    }
}");

        var registry = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^the first number is (.*)$", "S.Steps.Method", connectorPath) },
            Array.Empty<ProjectHookBinding>(),
            projectHash: 0);

        var updated = await registry.ReplaceBindings(changedFile);

        updated.StepDefinitions.Should().ContainSingle("the stale upper-case-drive binding must be removed")
            .Which.Regex!.ToString().Should().Be("^the firs number is (.*)$");
    }

    // ── HasExpressionChanges ────────────────────────────────────────────────────

    [Fact]
    public void HasExpressionChanges_returns_false_when_expressions_are_unchanged()
    {
        // Simulates an edit that doesn't touch any binding's matched expression (e.g. a method
        // body edit, a comment, or whitespace) -- the attribute text parsed into each binding is
        // identical before and after.
        var before = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^the first number is (.*)$", "Steps.Method", FilePath) },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);
        var after = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^the first number is (.*)$", "Steps.Method", FilePath) },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);

        ProjectBindingRegistry.HasExpressionChanges(before, after, FilePath).Should().BeFalse();
    }

    [Fact]
    public void HasExpressionChanges_returns_true_when_an_expression_text_changes()
    {
        var before = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^the first number is (.*)$", "Steps.Method", FilePath) },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);
        var after = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^the first num is (.*)$", "Steps.Method", FilePath) },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);

        ProjectBindingRegistry.HasExpressionChanges(before, after, FilePath).Should().BeTrue();
    }

    [Fact]
    public void HasExpressionChanges_returns_true_when_a_binding_is_added()
    {
        var before = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^the first number is (.*)$", "Steps.Method", FilePath) },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);
        var after = new ProjectBindingRegistry(
            new[]
            {
                BuildStepDefinition("^the first number is (.*)$", "Steps.Method", FilePath),
                BuildStepDefinition("^the second number is (.*)$", "Steps.Method2", FilePath),
            },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);

        ProjectBindingRegistry.HasExpressionChanges(before, after, FilePath).Should().BeTrue();
    }

    [Fact]
    public void HasExpressionChanges_returns_true_when_a_binding_is_removed()
    {
        var before = new ProjectBindingRegistry(
            new[]
            {
                BuildStepDefinition("^the first number is (.*)$", "Steps.Method", FilePath),
                BuildStepDefinition("^the second number is (.*)$", "Steps.Method2", FilePath),
            },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);
        var after = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^the first number is (.*)$", "Steps.Method", FilePath) },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);

        ProjectBindingRegistry.HasExpressionChanges(before, after, FilePath).Should().BeTrue();
    }

    [Fact]
    public void HasExpressionChanges_ignores_changes_in_other_files()
    {
        const string otherFilePath = @"C:\Project\Other.cs";
        var before = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^stale$", "Other.Method", otherFilePath) },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);
        var after = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^changed$", "Other.Method", otherFilePath) },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);

        // The only change is to a binding owned by a different file than the one being compared.
        ProjectBindingRegistry.HasExpressionChanges(before, after, FilePath).Should().BeFalse();
    }

    [Fact]
    public void HasExpressionChanges_does_not_throw_when_a_method_has_two_attributes_of_the_same_type_and_parameters()
    {
        // Regression test for issue #56: a method with two [When(...)] attributes producing
        // different expressions but identical (StepDefinitionType, Method, ParameterTypes) keys
        // used to crash HasExpressionChanges with a duplicate-key ArgumentException from
        // ToDictionary, instead of tolerating the collision.
        var before = new ProjectBindingRegistry(
            new[]
            {
                BuildStepDefinition("^the two nums are confirmed (.*)$", "Steps.Method", FilePath, ScenarioBlock.When),
                BuildStepDefinition("^the result should be (.*)$", "Steps.Method", FilePath, ScenarioBlock.When),
            },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);
        var after = new ProjectBindingRegistry(
            new[]
            {
                BuildStepDefinition("^the two nums are confirmed (.*)$", "Steps.Method", FilePath, ScenarioBlock.When),
                BuildStepDefinition("^the result should be (.*)$", "Steps.Method", FilePath, ScenarioBlock.When),
            },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);

        var act = () => ProjectBindingRegistry.HasExpressionChanges(before, after, FilePath);

        act.Should().NotThrow();
        act().Should().BeFalse();
    }

    [Fact]
    public void HasExpressionChanges_detects_change_when_one_of_two_colliding_attributes_expression_changes()
    {
        var before = new ProjectBindingRegistry(
            new[]
            {
                BuildStepDefinition("^the two nums are confirmed (.*)$", "Steps.Method", FilePath, ScenarioBlock.When),
                BuildStepDefinition("^the result should be (.*)$", "Steps.Method", FilePath, ScenarioBlock.When),
            },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);
        var after = new ProjectBindingRegistry(
            new[]
            {
                BuildStepDefinition("^the two nums are confirmed (.*)$", "Steps.Method", FilePath, ScenarioBlock.When),
                BuildStepDefinition("^the result should be changed (.*)$", "Steps.Method", FilePath, ScenarioBlock.When),
            },
            Array.Empty<ProjectHookBinding>(), projectHash: 0);

        ProjectBindingRegistry.HasExpressionChanges(before, after, FilePath).Should().BeTrue();
    }

    private static ProjectStepDefinitionBinding BuildStepDefinition(string regex, string method, string sourceFile,
        ScenarioBlock stepDefinitionType = ScenarioBlock.Given) =>
        new(stepDefinitionType, new Regex(regex), null,
            new ProjectBindingImplementation(method, Array.Empty<string>(), new SourceLocation(sourceFile, 0, 0)));

    private static ProjectHookBinding BuildHook(
        HookType hookType, string method, string sourceFile, BindingScope? scope = null, int? hookOrder = null) =>
        new(new ProjectBindingImplementation(method, Array.Empty<string>(), new SourceLocation(sourceFile, 0, 0)),
            scope, hookType, hookOrder, null);

    // ── HasHookChanges ───────────────────────────────────────────────────────────

    [Fact]
    public void HasHookChanges_returns_false_when_scope_and_order_are_unchanged()
    {
        var before = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(),
            new[] { BuildHook(HookType.BeforeScenario, "Hooks.Method", FilePath) }, projectHash: 0);
        var after = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(),
            new[] { BuildHook(HookType.BeforeScenario, "Hooks.Method", FilePath) }, projectHash: 0);

        ProjectBindingRegistry.HasHookChanges(before, after, FilePath).Should().BeFalse();
    }

    [Fact]
    public void HasHookChanges_returns_true_when_a_hook_is_added()
    {
        // The exact scenario reported live: editing a .cs file to add a new [BeforeScenario]
        // hook didn't refresh the feature file's hook-count CodeLens until a full rebuild,
        // because ConnectorBindingRegistryProvider only checked HasExpressionChanges.
        var before = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(), Array.Empty<ProjectHookBinding>(), projectHash: 0);
        var after = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(),
            new[] { BuildHook(HookType.BeforeScenario, "Hooks.Method", FilePath) }, projectHash: 0);

        ProjectBindingRegistry.HasHookChanges(before, after, FilePath).Should().BeTrue();
    }

    [Fact]
    public void HasHookChanges_returns_true_when_a_hook_is_removed()
    {
        var before = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(),
            new[] { BuildHook(HookType.BeforeScenario, "Hooks.Method", FilePath) }, projectHash: 0);
        var after = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(), Array.Empty<ProjectHookBinding>(), projectHash: 0);

        ProjectBindingRegistry.HasHookChanges(before, after, FilePath).Should().BeTrue();
    }

    [Fact]
    public void HasHookChanges_returns_true_when_a_hooks_scope_changes()
    {
        var before = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(),
            new[]
            {
                BuildHook(HookType.BeforeScenario, "Hooks.Method", FilePath,
                    scope: new BindingScope { FeatureTitle = "Old" }),
            }, projectHash: 0);
        var after = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(),
            new[]
            {
                BuildHook(HookType.BeforeScenario, "Hooks.Method", FilePath,
                    scope: new BindingScope { FeatureTitle = "New" }),
            }, projectHash: 0);

        ProjectBindingRegistry.HasHookChanges(before, after, FilePath).Should().BeTrue();
    }

    [Fact]
    public void HasHookChanges_returns_true_when_a_hooks_order_changes()
    {
        var before = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(),
            new[] { BuildHook(HookType.BeforeScenario, "Hooks.Method", FilePath, hookOrder: 100) }, projectHash: 0);
        var after = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(),
            new[] { BuildHook(HookType.BeforeScenario, "Hooks.Method", FilePath, hookOrder: 200) }, projectHash: 0);

        ProjectBindingRegistry.HasHookChanges(before, after, FilePath).Should().BeTrue();
    }

    [Fact]
    public void HasHookChanges_ignores_changes_in_other_files()
    {
        const string otherFilePath = @"C:\Project\Other.cs";
        var before = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(),
            new[] { BuildHook(HookType.BeforeScenario, "Other.Method", otherFilePath) }, projectHash: 0);
        var after = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(), Array.Empty<ProjectHookBinding>(), projectHash: 0);

        // The only change is to a hook owned by a different file than the one being compared.
        ProjectBindingRegistry.HasHookChanges(before, after, FilePath).Should().BeFalse();
    }

    [Fact]
    public void HasHookChanges_ignores_a_step_definition_only_edit()
    {
        var before = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^old$", "Steps.Method", FilePath) },
            new[] { BuildHook(HookType.BeforeScenario, "Hooks.Method", FilePath) }, projectHash: 0);
        var after = new ProjectBindingRegistry(
            new[] { BuildStepDefinition("^new$", "Steps.Method", FilePath) },
            new[] { BuildHook(HookType.BeforeScenario, "Hooks.Method", FilePath) }, projectHash: 0);

        ProjectBindingRegistry.HasHookChanges(before, after, FilePath).Should().BeFalse();
    }
}
