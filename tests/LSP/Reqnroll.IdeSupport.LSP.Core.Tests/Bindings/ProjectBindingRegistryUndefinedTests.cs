namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;
/*

Undefined
=========
* No candidating step definitions => undef
* OUT: Some match, but with different type => error (now: undef)
* OUT: Some match, but with different scope => error (now: undef)
* SO, but all (incl. empty) undefined => undef

*/

public class ProjectBindingRegistryUndefinedTests : ProjectBindingRegistryTestsBase
{
    // No candidating step definitions => undef

    [Fact]
    public void Matches_undefined()
    {
        _stepDefinitionBindings.Add(CreateStepDefinitionBinding("not used step"));
        var sut = CreateSut();

        var result = sut.MatchStep(CreateStep(text: "my undefined step"), StubGherkinDocument.Instance);
        result.HasUndefined.Should().BeTrue();
    }

    //OUT: Some match, but with different type => error (now: undef)

    [Fact]
    public void Does_not_match_step_definition_of_a_different_type()
    {
        _stepDefinitionBindings.Add(CreateStepDefinitionBinding("my step"));
        var sut = CreateSut();

        var result = sut.MatchStep(CreateStep(text: "my step", stepKeyword: StepKeyword.When),
            StubGherkinDocument.Instance);
        result.HasUndefined.Should().BeTrue();
    }

    //OUT: Some match, but with different scope => error (now: undef)

    [Fact]
    public void Does_not_match_tag_scoped_step_definition_if_not_tagged()
    {
        _stepDefinitionBindings.Add(CreateStepDefinitionBinding("my step", scope: CreateTagScope("mytag")));
        var sut = CreateSut();

        var result = sut.MatchStep(CreateStep(text: "my step"), StubGherkinDocument.Instance);
        result.HasUndefined.Should().BeTrue();
    }

    //SO, but all (incl. empty) undefined => undef

    [Fact]
    public void All_SO_examples_are_undefined()
    {
        _stepDefinitionBindings.Add(CreateStepDefinitionBinding("not used step"));
        var sut = CreateSut();

        var result = sut.MatchStep(CreateStep(text: "my <what> step"),
            CreateScenarioOutlineContext(null, null, "what", new[] {"cool", "other"}));
        result.HasUndefined.Should().BeTrue();
    }

    [Fact]
    public void Empty_SO_Examples()
    {
        _stepDefinitionBindings.Add(CreateStepDefinitionBinding("not used step"));
        var sut = CreateSut();

        var result = sut.MatchStep(CreateStep(text: "my <what> step"),
            CreateScenarioOutlineContext(null, null, "what", new string[0]));
        result.HasUndefined.Should().BeTrue();
    }

    // ── Near-miss error surfacing (issue #514 "cheap first step") ──────────────────

    [Fact]
    public void Undefined_step_surfaces_an_invalid_bindings_error_when_it_would_otherwise_match()
    {
        _stepDefinitionBindings.Add(CreateStepDefinitionBinding("my step", error: "must be static"));
        var sut = CreateSut();

        var result = sut.MatchStep(CreateStep(text: "my step"), StubGherkinDocument.Instance);

        result.HasUndefined.Should().BeTrue();
        result.GetErrorMessage().Should().Be("must be static");
    }

    [Fact]
    public void Undefined_step_has_no_error_message_when_no_binding_would_have_matched_at_all()
    {
        // The near-miss check must not surface an invalid binding's error for a step whose text
        // it doesn't even match structurally — only a genuine near-miss should be reported.
        _stepDefinitionBindings.Add(CreateStepDefinitionBinding("something else entirely", error: "must be static"));
        var sut = CreateSut();

        var result = sut.MatchStep(CreateStep(text: "my step"), StubGherkinDocument.Instance);

        result.HasUndefined.Should().BeTrue();
        result.GetErrorMessage().Should().BeNull();
    }

    [Fact]
    public void Undefined_step_has_no_error_message_when_the_only_structural_match_is_valid_but_wrong_type()
    {
        // A VALID binding of a different ScenarioBlock structurally "matches" the text but is
        // still correctly reported as a plain undefined step (existing behavior,
        // Does_not_match_step_definition_of_a_different_type above) -- confirms the near-miss
        // check doesn't change that for a binding with no Error at all.
        _stepDefinitionBindings.Add(CreateStepDefinitionBinding("my step"));
        var sut = CreateSut();

        var result = sut.MatchStep(CreateStep(text: "my step", stepKeyword: StepKeyword.When),
            StubGherkinDocument.Instance);

        result.HasUndefined.Should().BeTrue();
        result.GetErrorMessage().Should().BeNull();
    }

    [Fact]
    public void Undefined_step_does_not_surface_an_invalid_bindings_error_when_its_scope_does_not_apply()
    {
        // The near-miss check must still honor scope -- an invalid, tag-scoped binding whose tag
        // isn't present must not be reported as "the" reason this step is undefined.
        _stepDefinitionBindings.Add(CreateStepDefinitionBinding("my step",
            scope: CreateTagScope("mytag"), error: "must be static"));
        var sut = CreateSut();

        var result = sut.MatchStep(CreateStep(text: "my step"), StubGherkinDocument.Instance);

        result.HasUndefined.Should().BeTrue();
        result.GetErrorMessage().Should().BeNull();
    }

    [Fact]
    public void Undefined_step_joins_distinct_errors_from_multiple_invalid_near_misses()
    {
        _stepDefinitionBindings.Add(CreateStepDefinitionBinding("my step",
            error: "reason A", methodName: "MethodA"));
        _stepDefinitionBindings.Add(CreateStepDefinitionBinding("my step",
            error: "reason B", methodName: "MethodB"));
        var sut = CreateSut();

        var result = sut.MatchStep(CreateStep(text: "my step"), StubGherkinDocument.Instance);

        result.HasUndefined.Should().BeTrue();
        result.GetErrorMessage().Should().Contain("reason A").And.Contain("reason B");
    }
}
