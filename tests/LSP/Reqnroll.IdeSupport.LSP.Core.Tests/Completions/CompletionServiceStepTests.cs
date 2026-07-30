using Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Completions.Matching;
using GherkinLocation = Gherkin.Ast.Location;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Completions;

public class CompletionServiceStepTests
{
    private readonly CompletionService _sut = new();
    private readonly ReturnAllCompletionMatcher _matcher = new();

    private static DeveroomGherkinStep MakeStep(ScenarioBlock block)
        => new(new GherkinLocation(1, 1),
               block == ScenarioBlock.Given ? "Given " : block == ScenarioBlock.When ? "When " : "Then ",
               StepKeywordType.Context,
               "step text",
               null!,
               StepKeyword.Given,
               block);

    private static ProjectStepDefinitionBinding Binding(
        ScenarioBlock block,
        string        pattern,
        string        method     = "MyStep",
        string[]?     paramTypes = null,
        bool          valid      = true)
    {
        var regex = valid ? new Regex("^" + pattern + "$") : null!;
        return new ProjectStepDefinitionBinding(
            block, regex, null,
            new ProjectBindingImplementation(
                method,
                paramTypes ?? Array.Empty<string>(),
                new SourceLocation("Steps.cs", 1, 1)),
            specifiedExpression: pattern);
    }

    private static ProjectBindingRegistry RegistryWith(params ProjectStepDefinitionBinding[] bindings)
        => new(bindings, Array.Empty<ProjectHookBinding>(), 0);

    private static Func<ProjectStepDefinitionBinding, int> NoUsages => _ => 0;

    // ── Filtering by ScenarioBlock ─────────────────────────────────────────────

    [Fact]
    public void Given_step_returns_only_Given_bindings()
    {
        var registry = RegistryWith(
            Binding(ScenarioBlock.Given, "I press add"),
            Binding(ScenarioBlock.When,  "I add the numbers"),
            Binding(ScenarioBlock.Then,  "the result is (.*)", paramTypes: new[] { "System.Int32" }));

        var result = _sut.GetStepCompletions(MakeStep(ScenarioBlock.Given), "", registry, NoUsages, _matcher);

        result.Entries.Should().HaveCount(1);
        result.Entries[0].Label.Should().Be("I press add");
    }

    [Fact]
    public void When_step_returns_only_When_bindings()
    {
        var registry = RegistryWith(
            Binding(ScenarioBlock.Given, "I press add"),
            Binding(ScenarioBlock.When,  "I add the numbers"));

        var result = _sut.GetStepCompletions(MakeStep(ScenarioBlock.When), "", registry, NoUsages, _matcher);

        result.Entries.Select(e => e.Label).Should().ContainSingle("I add the numbers");
    }

    // ── Invalid binding excluded ──────────────────────────────────────────────

    [Fact]
    public void Invalid_bindings_are_excluded()
    {
        var registry = RegistryWith(
            Binding(ScenarioBlock.Given, "I press add"),
            Binding(ScenarioBlock.Given, "invalid step", valid: false));

        var result = _sut.GetStepCompletions(MakeStep(ScenarioBlock.Given), "", registry, NoUsages, _matcher);

        result.Entries.Select(e => e.Label).Should().ContainSingle("I press add");
    }

    // ── Method-name-style bindings excluded (issue #344) ────────────────────────

    [Fact]
    public void Method_name_style_bindings_have_no_authored_expression_to_sample_and_are_excluded()
    {
        // No explicit attribute expression -> the binding's regex is auto-generated from the
        // method name/parameters, not valid Gherkin step text; offering/inserting it as a
        // completion would corrupt the .feature file (issue #344).
        var methodNameStyle = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given,
            new Regex(@"^(?i)The(?:[^\w\p{Sc}]*)First(?:[^\w\p{Sc}]*)Number(?:[^\w\p{Sc}]*)Is(?:[^\w\p{Sc}]*)(?<p0>.*?)(?:[^\w\p{Sc}]*)$"),
            null,
            new ProjectBindingImplementation("The_First_Number_Is_P0", new[] { "System.Int32" }, new SourceLocation("Steps.cs", 1, 1)));
        var registry = RegistryWith(
            Binding(ScenarioBlock.Given, "I press add"),
            methodNameStyle);

        var result = _sut.GetStepCompletions(MakeStep(ScenarioBlock.Given), "", registry, NoUsages, _matcher);

        result.Entries.Select(e => e.Label).Should().ContainSingle("I press add");
    }

    // ── Literal sample insert text ────────────────────────────────────────────

    [Fact]
    public void InsertText_is_literal_sample_with_type_placeholder()
    {
        var registry = RegistryWith(
            Binding(ScenarioBlock.Given, "I have entered (.*) into the calculator",
                    paramTypes: new[] { "System.Int32" }));

        var result = _sut.GetStepCompletions(MakeStep(ScenarioBlock.Given), "", registry, NoUsages, _matcher);

        result.Entries.Should().HaveCount(1);
        result.Entries[0].InsertText.Should().Be("I have entered [int] into the calculator");
        result.Entries[0].FilterText.Should().Be("I have entered [int] into the calculator");
    }

    // ── Deduplication ─────────────────────────────────────────────────────────

    [Fact]
    public void Identical_samples_are_deduplicated()
    {
        var registry = RegistryWith(
            Binding(ScenarioBlock.Given, "I press add", method: "M1"),
            Binding(ScenarioBlock.Given, "I press add", method: "M2"));

        var result = _sut.GetStepCompletions(MakeStep(ScenarioBlock.Given), "", registry, NoUsages, _matcher);

        result.Entries.Select(e => e.Label).Should().ContainSingle("I press add");
    }

    // ── SortText ──────────────────────────────────────────────────────────────

    [Fact]
    public void Entries_have_zero_padded_sort_text()
    {
        var registry = RegistryWith(
            Binding(ScenarioBlock.Given, "alpha"),
            Binding(ScenarioBlock.Given, "beta"),
            Binding(ScenarioBlock.Given, "gamma"));

        var result = _sut.GetStepCompletions(MakeStep(ScenarioBlock.Given), "", registry, NoUsages, _matcher);

        result.Entries[0].SortText.Should().Be("000000");
        result.Entries[1].SortText.Should().Be("000001");
        result.Entries[2].SortText.Should().Be("000002");
    }

    // ── UsageCount wired to matcher ───────────────────────────────────────────

    [Fact]
    public void UsageCount_is_passed_from_usageCounter_to_matcher()
    {
        var binding = Binding(ScenarioBlock.Given, "I press add");
        var registry = RegistryWith(binding);

        var capturedCandidates = new List<StepCandidate>();
        var spyMatcher = Substitute.For<ICompletionMatcher>();
        spyMatcher.IsIncomplete.Returns(false);
        spyMatcher
            .Rank(Arg.Any<string>(), Arg.Any<IReadOnlyList<StepCandidate>>())
            .Returns(ci =>
            {
                var cands = ci.Arg<IReadOnlyList<StepCandidate>>();
                capturedCandidates.AddRange(cands);
                return cands.Select(c => new ScoredCandidate(c.Sample, 0)).ToList();
            });

        Func<ProjectStepDefinitionBinding, int> usageCounter = sd => 42;

        _sut.GetStepCompletions(MakeStep(ScenarioBlock.Given), "", registry, usageCounter, spyMatcher);

        capturedCandidates.Should().HaveCount(1);
        capturedCandidates[0].UsageCount.Should().Be(42);
    }

    // ── Empty registry ────────────────────────────────────────────────────────

    [Fact]
    public void Empty_registry_returns_empty_result()
    {
        var result = _sut.GetStepCompletions(
            MakeStep(ScenarioBlock.Given), "", RegistryWith(), NoUsages, _matcher);

        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Invalid_registry_returns_empty_result()
    {
        var result = _sut.GetStepCompletions(
            MakeStep(ScenarioBlock.Given), "", ProjectBindingRegistry.Invalid, NoUsages, _matcher);

        result.Entries.Should().BeEmpty();
    }

    // ── IsIncomplete propagated ───────────────────────────────────────────────

    [Fact]
    public void IsIncomplete_propagated_from_matcher()
    {
        var incompleteMatcher = Substitute.For<ICompletionMatcher>();
        incompleteMatcher.IsIncomplete.Returns(true);
        incompleteMatcher
            .Rank(Arg.Any<string>(), Arg.Any<IReadOnlyList<StepCandidate>>())
            .Returns(new List<ScoredCandidate>());

        var result = _sut.GetStepCompletions(
            MakeStep(ScenarioBlock.Given), "", RegistryWith(), NoUsages, incompleteMatcher);

        result.IsIncomplete.Should().BeTrue();
    }
}
