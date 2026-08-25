using System.Collections.Immutable;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

/// <summary>
/// Tests for <see cref="StepLiteralIndex"/>: the literal prefilter must never exclude a binding
/// that would genuinely match (soundness is the one property it can't get wrong), narrow the
/// candidate set for literal-anchored bindings, and correctly treat Cucumber Expression
/// alternation/optional-text as non-literal rather than requiring their surface text verbatim.
/// </summary>
public class StepLiteralIndexTests
{
    private static ProjectStepDefinitionBinding Binding(string regexPattern, string? specifiedExpression = null)
    {
        var implementation = new ProjectBindingImplementation(
            "Method" + Guid.NewGuid().ToString("N"), null, new SourceLocation("MyClass.cs", 2, 5));
        return new ProjectStepDefinitionBinding(
            ScenarioBlock.Given, new Regex(regexPattern, RegexOptions.CultureInvariant), null,
            implementation, specifiedExpression);
    }

    private static StepLiteralIndex CreateSut(params ProjectStepDefinitionBinding[] bindings) =>
        StepLiteralIndex.Build(bindings.ToImmutableArray());

    // ── Basic narrowing ──────────────────────────────────────────────────────

    [Fact]
    public void A_binding_with_no_matching_literal_is_excluded()
    {
        var target = Binding(@"^I have (-?\d+) cukes$");
        var other  = Binding(@"^the sky is blue$");
        var sut = CreateSut(target, other);

        var candidates = sut.GetCandidates("I have 5 cukes").ToArray();

        candidates.Should().Contain(target);
        candidates.Should().NotContain(other);
    }

    [Fact]
    public void A_binding_whose_literal_is_present_is_included()
    {
        var target = Binding(@"^I have (-?\d+) cukes$");
        var sut = CreateSut(target);

        sut.GetCandidates("I have 5 cukes").Should().Contain(target);
    }

    [Fact]
    public void A_binding_requiring_multiple_literal_segments_needs_all_of_them_present()
    {
        // "I put (-?\d+) cukes in my belly" -> literal segments "I put " and " cukes in my belly"
        var target = Binding(@"^I put (-?\d+) cukes in my belly$");
        var sut = CreateSut(target);

        sut.GetCandidates("I put 5 cukes in my belly").Should().Contain(target);
        // Only the first literal segment present -- must not be a candidate.
        sut.GetCandidates("I put 5 pancakes on my plate").Should().NotContain(target);
    }

    // ── No false negatives (the property that matters most) ─────────────────

    [Fact]
    public void A_bare_wildcard_binding_with_no_literal_is_always_a_candidate()
    {
        var target = Binding(@"^(.*)$");
        var sut = CreateSut(target);

        sut.GetCandidates("literally anything at all").Should().Contain(target);
        sut.GetCandidates(string.Empty).Should().Contain(target);
    }

    [Fact]
    public void A_method_name_style_binding_with_no_specified_expression_is_still_correctly_handled()
    {
        // Method-name-style bindings still produce a real regex (BuildMethodNameRegex); the index
        // must work from the compiled regex regardless of SpecifiedExpression being null.
        var target = Binding(@"^the first number is (-?\d+)$");
        var sut = CreateSut(target);

        sut.GetCandidates("the first number is 5").Should().Contain(target);
        sut.GetCandidates("something unrelated").Should().NotContain(target);
    }

    [Fact]
    public void Short_literal_segments_below_the_length_threshold_do_not_cause_a_false_negative()
    {
        // A single-character literal segment is deliberately not indexed (not discriminative
        // enough to be worth it) -- the binding must fall back to "always a candidate" rather
        // than requiring a literal that was never indexed.
        var target = Binding(@"^a (-?\d+) b$");
        var sut = CreateSut(target);

        sut.GetCandidates("a 5 b").Should().Contain(target);
    }

    [Fact]
    public void A_non_capturing_group_containing_nested_capturing_groups_does_not_leak_its_closing_paren_as_literal()
    {
        // (?:(cool)|(bad)) is what Cucumber Expression alternation compiles to when combined with
        // another parameter -- a non-capturing wrapper around nested capturing groups. Depth
        // tracking must correctly consume the whole non-capturing span (including its closing
        // paren) rather than leaving it to be misread as literal text in whatever follows.
        var target = Binding(@"^my (?:(cool)|(bad)) step$");
        var sut = CreateSut(target);

        sut.GetCandidates("my cool step").Should().Contain(target);
        sut.GetCandidates("my bad step").Should().Contain(target);
        // A step missing both required literal segments ("my " / " step") must still be excluded.
        sut.GetCandidates("something else entirely").Should().NotContain(target);
    }

    [Fact]
    public void A_case_insensitive_binding_still_matches_a_differently_cased_step()
    {
        // (?i) makes the regex match regardless of case, but the literal "First"/"Number" in the
        // pattern text is capitalized -- the index must fold both sides to a consistent case
        // rather than requiring an exact-case substring the step text may never contain.
        var target = Binding(@"^(?i)The First Number Is (?<p0>.*?)$");
        var sut = CreateSut(target);

        sut.GetCandidates("The first number is 5").Should().Contain(target);
        sut.GetCandidates("THE FIRST NUMBER IS 5").Should().Contain(target);
    }

    // ── Cucumber Expression alternation / optional text: must NOT be treated as literal ──

    [Fact]
    public void Alternation_compiled_from_a_slash_expression_does_not_require_either_branch_as_literal()
    {
        // "a red/blue ball" compiles (Cucumber Expressions) to (?:red|blue) — neither "red" nor
        // "blue" nor the literal text "red/blue" is required text; the step could say either.
        var target = Binding(@"^a (?:red|blue) ball$");
        var sut = CreateSut(target);

        sut.GetCandidates("a red ball").Should().Contain(target);
        sut.GetCandidates("a blue ball").Should().Contain(target);
    }

    [Fact]
    public void Optional_text_compiled_from_parens_does_not_require_the_optional_part_as_literal()
    {
        // "I have {int} apple(s)" compiles to "...apple(?:s)?" — "apples" is not required text,
        // "apple" alone must still match.
        var target = Binding(@"^I have (-?\d+) apple(?:s)?$");
        var sut = CreateSut(target);

        sut.GetCandidates("I have 1 apple").Should().Contain(target);
        sut.GetCandidates("I have 2 apples").Should().Contain(target);
    }

    [Fact]
    public void A_plain_regex_alternation_outside_any_cucumber_expression_context_is_also_not_treated_as_literal()
    {
        var target = Binding(@"^(foo|bar) count$");
        var sut = CreateSut(target);

        sut.GetCandidates("foo count").Should().Contain(target);
        sut.GetCandidates("bar count").Should().Contain(target);
    }

    // ── Multiple bindings sharing literals ───────────────────────────────────

    [Fact]
    public void Bindings_sharing_the_same_literal_segment_are_both_returned_when_it_matches()
    {
        var a = Binding(@"^I have (-?\d+) cukes$");
        var b = Binding(@"^I have (-?\d+) apples$");
        var sut = CreateSut(a, b);

        var candidates = sut.GetCandidates("I have 5 cukes").ToArray();

        candidates.Should().Contain(a);
        candidates.Should().NotContain(b);
    }

    [Fact]
    public void Empty_registry_returns_no_candidates()
    {
        var sut = CreateSut();
        sut.GetCandidates("anything").Should().BeEmpty();
    }

    [Fact]
    public void A_binding_with_an_invalid_null_regex_never_becomes_a_required_literal_and_is_always_a_candidate()
    {
        var implementation = new ProjectBindingImplementation("M", null, new SourceLocation("MyClass.cs", 2, 5));
        var invalid = new ProjectStepDefinitionBinding(ScenarioBlock.Given, null!, null, implementation);
        var sut = CreateSut(invalid);

        sut.GetCandidates("anything at all").Should().Contain(invalid);
    }
}
