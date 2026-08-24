using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Matching;

public class BindingMatchServiceTests
{
    private const string Uri       = "file:///c:/proj/feature1.feature";
    private const string SecondUri = "file:///c:/proj/feature2.feature";

    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ITelemetryService _telemetryService = Substitute.For<ITelemetryService>();
    private readonly IDeveroomConfigurationProvider _configProvider = Substitute.For<IDeveroomConfigurationProvider>();

    public BindingMatchServiceTests()
    {
        _configProvider.GetConfiguration().Returns(new DeveroomConfiguration());
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static ProjectStepDefinitionBinding GivenBinding(
        string pattern, string method = "MyStep", string file = "Steps.cs", int line = 5) =>
        new(ScenarioBlock.Given,
            new Regex("^" + Regex.Escape(pattern) + "$"),
            null,
            new ProjectBindingImplementation(method, null, new SourceLocation(file, line, 1)));

    private static ProjectBindingRegistry RegistryWith(params ProjectStepDefinitionBinding[] bindings) =>
        new(bindings, Array.Empty<ProjectHookBinding>(), 0);

    private IReadOnlyCollection<DeveroomTag> ParseTags(string text, ProjectBindingRegistry registry)
    {
        var parser = new DeveroomTagParser(_logger, _telemetryService, _configProvider);
        return parser.Parse(new StubGherkinTextSnapshot(text), registry);
    }

    private FeatureBindingMatchSet BuildSet(
        string text, ProjectBindingRegistry registry,
        int? version = 1, string docUri = Uri, ProjectOwner owner = default)
    {
        var tags = ParseTags(text, registry);
        return FeatureBindingMatchSet.FromTags(docUri, version, registry.Version, tags, owner);
    }

    private static readonly ProjectOwner OwnerA = new("C:/proj/A.csproj", "net8.0");
    private static readonly ProjectOwner OwnerB = new("C:/proj/B.csproj", "net8.0");

    private const string DefinedFeature  = "Feature: F\nScenario: S\n    Given my step\n";
    private const string UndefinedFeature = "Feature: F\nScenario: S\n    Given no such step\n";

    // ── FromTags / FeatureBindingMatchSet ───────────────────────────────────────

    [Fact]
    public void FromTags_captures_a_defined_step_match()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));

        set.Steps.Should().ContainSingle();
        set.Steps[0].IsDefined.Should().BeTrue();
        set.Defined.Should().ContainSingle();
        set.Undefined.Should().BeEmpty();
    }

    [Fact]
    public void FromTags_captures_an_undefined_step_match()
    {
        var set = BuildSet(UndefinedFeature, RegistryWith(GivenBinding("my step")));

        set.Steps.Should().ContainSingle();
        set.Steps[0].IsUndefined.Should().BeTrue();
        set.Undefined.Should().ContainSingle();
        set.Defined.Should().BeEmpty();
    }

    [Fact]
    public void FromTags_captures_an_ambiguous_step_match()
    {
        var b1 = GivenBinding("my step", method: "Method1", file: "A.cs");
        var b2 = GivenBinding("my step", method: "Method2", file: "B.cs");
        var set = BuildSet(DefinedFeature, RegistryWith(b1, b2));

        set.Steps.Should().ContainSingle();
        set.Steps[0].IsAmbiguous.Should().BeTrue();
        set.Ambiguous.Should().ContainSingle();
    }

    [Fact]
    public void FromTags_captures_feature_name_for_a_scenario_directly_under_the_feature()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));

        set.Steps[0].FeatureName.Should().Be("F");
    }

    [Fact]
    public void FromTags_captures_feature_name_for_a_scenario_nested_under_a_rule()
    {
        // A scenario under a Rule: block has an extra RuleBlock tag between the
        // ScenarioDefinitionBlock and the FeatureBlock, so the feature name is two levels up
        // from the scenario rather than one (issue #238).
        const string feature = "Feature: F\nRule: R\nScenario: S\n    Given my step\n";
        var set = BuildSet(feature, RegistryWith(GivenBinding("my step")));

        set.Steps[0].FeatureName.Should().Be("F");
    }

    [Fact]
    public void FromTags_captures_rule_name_for_a_scenario_nested_under_a_rule()
    {
        const string feature = "Feature: F\nRule: R\nScenario: S\n    Given my step\n";
        var set = BuildSet(feature, RegistryWith(GivenBinding("my step")));

        set.Steps[0].RuleName.Should().Be("R");
    }

    [Fact]
    public void FromTags_leaves_rule_name_null_for_a_scenario_not_under_a_rule()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));

        set.Steps[0].RuleName.Should().BeNull();
    }

    [Fact]
    public void FindAt_returns_the_step_whose_span_contains_the_offset()
    {
        var set  = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));
        var step = set.Steps[0];

        set.FindAt(step.Range.Start).Should().BeSameAs(step);
        set.FindAt(step.Range.End - 1).Should().BeSameAs(step);
        set.FindAt(0).Should().BeNull();
    }

    [Fact]
    public void FindAt_tolerates_offsets_anywhere_on_the_step_line()
    {
        // DefinedFeature line 2 (0-based) is "    Given my step" — the step text span covers
        // just "my step"; the rest of the line (indentation + keyword, and one-past-the-end)
        // should still resolve to the same step (issue #101).
        var set  = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));
        var step = set.Steps[0];
        var line = step.Range.Snapshot.GetLineFromLineNumber(step.Range.StartLinePosition.Line);

        set.FindAt(line.Start).Should().BeSameAs(step, "clicking on the leading indentation/keyword should resolve to the step");
        set.FindAt(step.Range.End).Should().BeSameAs(step, "clicking one past the last character of the step text should still resolve");
        set.FindAt(line.End).Should().BeSameAs(step, "clicking at end-of-line should resolve to the step");
    }

    [Fact]
    public void FindAt_does_not_bleed_across_lines()
    {
        // DefinedFeature line 1 (0-based) is "Scenario: S" — a different line than the step,
        // so it must not resolve even though it's adjacent to the step's line.
        var set  = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));
        var step = set.Steps[0];
        var stepLine = step.Range.Snapshot.GetLineFromLineNumber(step.Range.StartLinePosition.Line);

        set.FindAt(stepLine.Start - 1).Should().BeNull("offset one before the step's line belongs to the previous line");
    }

    [Fact]
    public void Defined_step_exposes_its_binding_source_location()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step", file: "Steps.cs", line: 5)));

        set.Steps[0].BindingLocations
            .Should().ContainSingle()
            .Which.SourceFile.Should().Be("Steps.cs");
    }

    [Fact]
    public void Empty_set_has_no_steps_and_FindAt_is_null()
    {
        FeatureBindingMatchSet.Empty.Steps.Should().BeEmpty();
        FeatureBindingMatchSet.Empty.FindAt(0).Should().BeNull();
    }

    [Fact]
    public void FromTags_owner_is_stored_on_the_key()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")), owner: OwnerA);

        set.Owner.Should().Be(OwnerA);
        set.Key.Owner.Should().Be(OwnerA);
        set.Key.DocumentId.Should().Be(Uri);
    }

    // ── FromTags / Scenarios (issue #373) ───────────────────────────────────────

    [Fact]
    public void FromTags_captures_a_scenario()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));

        set.Scenarios.Should().ContainSingle();
        set.Scenarios[0].Name.Should().Be("S");
        set.Scenarios[0].IsOutline.Should().BeFalse();
    }

    [Fact]
    public void FromTags_excludes_background()
    {
        const string feature = "Feature: F\nBackground:\n    Given setup\nScenario: S\n    Given my step\n";
        var set = BuildSet(feature, RegistryWith(GivenBinding("my step"), GivenBinding("setup")));

        set.Scenarios.Should().ContainSingle("Background shares ScenarioDefinitionBlock with real scenarios but isn't independently executed/countable");
        set.Scenarios[0].Name.Should().Be("S");
    }

    [Fact]
    public void FromTags_marks_scenario_outline_and_counts_it_once_regardless_of_examples_row_count()
    {
        const string feature = "Feature: F\nScenario Outline: SO\n    Given <x>\nExamples:\n    | x |\n    | 1 |\n    | 2 |\n    | 3 |\n";
        var set = BuildSet(feature, RegistryWith(GivenBinding("<x>")));

        set.Scenarios.Should().ContainSingle("a Scenario Outline counts as one scenario definition regardless of its Examples row count (#373 decided semantics)");
        set.Scenarios[0].IsOutline.Should().BeTrue();
        set.Scenarios[0].Name.Should().Be("SO");
    }

    [Fact]
    public void FromTags_scenario_context_includes_inherited_feature_tags()
    {
        // The scenario itself carries no tags of its own -- @foo is only declared on the
        // enclosing Feature. A hook scoped to @foo must still match this scenario, which relies
        // on IGherkinDocumentContext.GetTagNames() walking up to the Feature tag (see
        // FeatureScenarioInfo's doc comment) -- this test locks in that the ScenarioTag's parent
        // chain is actually wired for that to work, not just theoretically true of the interface.
        const string feature = "@foo\nFeature: F\nScenario: S\n    Given my step\n";
        var set = BuildSet(feature, RegistryWith(GivenBinding("my step")));

        var context = (IGherkinDocumentContext)set.Scenarios[0].ScenarioTag;
        context.GetTagNames().Should().Contain("@foo");
    }

    [Fact]
    public void FromTags_two_scenarios_in_one_feature_are_both_captured()
    {
        const string feature = "Feature: F\nScenario: S1\n    Given my step\nScenario: S2\n    Given my step\n";
        var set = BuildSet(feature, RegistryWith(GivenBinding("my step")));

        set.Scenarios.Should().HaveCount(2);
        set.Scenarios.Select(s => s.Name).Should().BeEquivalentTo(["S1", "S2"]);
    }

    // ── BindingMatchService cache (single-project, unknown owner) ──────────────

    [Fact]
    public void Store_then_TryGet_returns_the_set()
    {
        var sut = new BindingMatchService();
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));

        sut.Store(set);

        sut.TryGet(MatchSetKey.ForUnknownProject(Uri), out var found).Should().BeTrue();
        found.Should().BeSameAs(set);
    }

    [Fact]
    public void TryGet_unknown_document_returns_false_and_Empty()
    {
        var sut = new BindingMatchService();

        sut.TryGet(MatchSetKey.ForUnknownProject("file:///nope.feature"), out var found).Should().BeFalse();
        found.Should().BeSameAs(FeatureBindingMatchSet.Empty);
    }

    [Fact]
    public void Store_replaces_the_prior_set_for_the_same_key()
    {
        var sut    = new BindingMatchService();
        var first  = BuildSet(DefinedFeature,   RegistryWith(GivenBinding("my step")), version: 1);
        var second = BuildSet(UndefinedFeature, RegistryWith(GivenBinding("my step")), version: 2);

        sut.Store(first);
        sut.Store(second);

        sut.TryGet(MatchSetKey.ForUnknownProject(Uri), out var found).Should().BeTrue();
        found.Should().BeSameAs(second);
        found.DocumentVersion.Should().Be(2);
    }

    [Fact]
    public void InvalidateAllForDocument_drops_the_document_entry()
    {
        var sut = new BindingMatchService();
        sut.Store(BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step"))));

        sut.InvalidateAllForDocument(Uri);

        sut.TryGet(MatchSetKey.ForUnknownProject(Uri), out _).Should().BeFalse();
    }

    [Fact]
    public void InvalidateAll_clears_every_entry()
    {
        var sut = new BindingMatchService();
        sut.Store(BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step"))));

        sut.InvalidateAll();

        sut.TryGet(MatchSetKey.ForUnknownProject(Uri), out _).Should().BeFalse();
    }

    // ── Per-project keying (Q18 2B) ────────────────────────────────────────────

    [Fact]
    public void Store_with_known_owner_evicts_Unknown_placeholder_for_same_document()
    {
        var sut         = new BindingMatchService();
        var registry    = RegistryWith(GivenBinding("my step"));
        var placeholder = BuildSet(DefinedFeature, registry, version: 1); // owner = Unknown
        var projectSet  = BuildSet(DefinedFeature, registry, version: 1, owner: OwnerA);

        sut.Store(placeholder);
        sut.TryGet(MatchSetKey.ForUnknownProject(Uri), out _).Should().BeTrue("placeholder stored");

        sut.Store(projectSet);
        sut.TryGet(MatchSetKey.ForUnknownProject(Uri), out _)
           .Should().BeFalse("Unknown entry evicted by project-keyed store");
        sut.TryGet(new MatchSetKey(Uri, OwnerA), out _).Should().BeTrue("project entry present");
    }

    [Fact]
    public void Two_projects_can_store_independent_match_sets_for_the_same_document()
    {
        var sut      = new BindingMatchService();
        var regA     = RegistryWith(GivenBinding("my step",  file: "A.cs", line: 1));
        var regB     = RegistryWith(GivenBinding("my step",  file: "B.cs", line: 1));
        var setA     = BuildSet(DefinedFeature, regA, owner: OwnerA);
        var setB     = BuildSet(DefinedFeature, regB, owner: OwnerB);

        sut.Store(setA);
        sut.Store(setB);

        sut.TryGet(new MatchSetKey(Uri, OwnerA), out var foundA).Should().BeTrue();
        sut.TryGet(new MatchSetKey(Uri, OwnerB), out var foundB).Should().BeTrue();
        foundA.Should().BeSameAs(setA);
        foundB.Should().BeSameAs(setB);
    }

    [Fact]
    public void InvalidateAllForDocument_removes_all_owner_slots_for_that_uri()
    {
        var sut  = new BindingMatchService();
        var reg  = RegistryWith(GivenBinding("my step"));
        sut.Store(BuildSet(DefinedFeature, reg, owner: OwnerA));
        sut.Store(BuildSet(DefinedFeature, reg, owner: OwnerB));

        sut.InvalidateAllForDocument(Uri);

        sut.TryGet(new MatchSetKey(Uri, OwnerA), out _).Should().BeFalse();
        sut.TryGet(new MatchSetKey(Uri, OwnerB), out _).Should().BeFalse();
    }

    [Fact]
    public void InvalidateAllForDocument_does_not_remove_other_documents()
    {
        var sut = new BindingMatchService();
        var reg = RegistryWith(GivenBinding("my step"));
        sut.Store(BuildSet(DefinedFeature, reg, docUri: Uri,       owner: OwnerA));
        sut.Store(BuildSet(DefinedFeature, reg, docUri: SecondUri, owner: OwnerA));

        sut.InvalidateAllForDocument(Uri);

        sut.TryGet(new MatchSetKey(SecondUri, OwnerA), out _).Should().BeTrue();
    }

    [Fact]
    public void InvalidateAllForProject_removes_all_slots_for_that_project()
    {
        var sut = new BindingMatchService();
        var reg = RegistryWith(GivenBinding("my step"));
        sut.Store(BuildSet(DefinedFeature, reg, docUri: Uri,       owner: OwnerA));
        sut.Store(BuildSet(DefinedFeature, reg, docUri: SecondUri, owner: OwnerA));
        sut.Store(BuildSet(DefinedFeature, reg, docUri: Uri,       owner: OwnerB));

        sut.InvalidateAllForProject(OwnerA);

        sut.TryGet(new MatchSetKey(Uri,       OwnerA), out _).Should().BeFalse();
        sut.TryGet(new MatchSetKey(SecondUri, OwnerA), out _).Should().BeFalse();
        sut.TryGet(new MatchSetKey(Uri,       OwnerB), out _).Should().BeTrue("OwnerB unaffected");
    }

    // ── reverse index (FindUsages) ──────────────────────────────────────────────

    [Fact]
    public void FindUsages_returns_steps_bound_to_the_given_source_location()
    {
        var sut = new BindingMatchService();
        sut.Store(BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step", file: "Steps.cs", line: 5))));

        var usages = sut.FindUsages(new SourceLocation("Steps.cs", 5, 99));

        usages.Should().ContainSingle();
    }

    [Fact]
    public void FindUsages_returns_nothing_for_an_unrelated_location()
    {
        var sut = new BindingMatchService();
        sut.Store(BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step", file: "Steps.cs", line: 5))));

        sut.FindUsages(new SourceLocation("Other.cs", 1, 1)).Should().BeEmpty();
    }

    [Fact]
    public void FindUsages_null_location_returns_empty()
    {
        var sut = new BindingMatchService();
        sut.Store(BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step"))));

        sut.FindUsages(null!).Should().BeEmpty();
    }

    [Fact]
    public void FindUsages_each_result_carries_the_feature_document_id()
    {
        var sut = new BindingMatchService();
        sut.Store(BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step", file: "Steps.cs", line: 5))));

        var usage = sut.FindUsages(new SourceLocation("Steps.cs", 5, 1)).Single();

        usage.FeatureDocumentId.Should().Be(Uri);
    }

    [Fact]
    public void FindUsages_finds_matches_across_multiple_documents()
    {
        var sut      = new BindingMatchService();
        var registry = RegistryWith(GivenBinding("my step", file: "Steps.cs", line: 5));

        sut.Store(BuildSet(DefinedFeature, registry, docUri: Uri));
        sut.Store(BuildSet(DefinedFeature, registry, docUri: SecondUri));

        var usages = sut.FindUsages(new SourceLocation("Steps.cs", 5, 1));

        usages.Should().HaveCount(2);
        usages.Select(u => u.FeatureDocumentId).Should()
              .BeEquivalentTo([Uri, SecondUri]);
    }

    [Fact]
    public void GetCacheStats_returns_document_count_and_total_step_count_across_the_cache()
    {
        var sut      = new BindingMatchService();
        var registry = RegistryWith(GivenBinding("my step", file: "Steps.cs", line: 5));

        // Two documents, one step each — DefinedFeature has a single "Given my step" scenario.
        sut.Store(BuildSet(DefinedFeature, registry, docUri: Uri));
        sut.Store(BuildSet(DefinedFeature, registry, docUri: SecondUri));

        var (documentCount, totalStepCount) = sut.GetCacheStats();

        documentCount.Should().Be(2);
        totalStepCount.Should().Be(2);
    }

    [Fact]
    public void GetCacheStats_on_empty_cache_returns_zero()
    {
        var sut = new BindingMatchService();

        var (documentCount, totalStepCount) = sut.GetCacheStats();

        documentCount.Should().Be(0);
        totalStepCount.Should().Be(0);
    }

    [Fact]
    public void FindUsages_uses_case_insensitive_path_comparison_on_source_file()
    {
        var sut = new BindingMatchService();
        sut.Store(BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step", file: "Steps.cs", line: 5))));

        var usages = sut.FindUsages(new SourceLocation("STEPS.CS", 5, 1));

        usages.Should().ContainSingle();
    }

    [Fact]
    public void FindUsages_does_not_return_undefined_steps()
    {
        var sut = new BindingMatchService();
        sut.Store(BuildSet(UndefinedFeature, RegistryWith(GivenBinding("my step"))));

        sut.FindUsages(new SourceLocation("Steps.cs", 5, 1)).Should().BeEmpty();
    }

    [Fact]
    public void FindUsages_with_project_filter_restricts_to_matching_owner()
    {
        var sut     = new BindingMatchService();
        var binding = GivenBinding("my step", file: "Steps.cs", line: 5);
        sut.Store(BuildSet(DefinedFeature, RegistryWith(binding), docUri: Uri,       owner: OwnerA));
        sut.Store(BuildSet(DefinedFeature, RegistryWith(binding), docUri: SecondUri, owner: OwnerB));

        var usages = sut.FindUsages(new SourceLocation("Steps.cs", 5, 1), [OwnerA]);

        usages.Should().ContainSingle()
              .Which.FeatureDocumentId.Should().Be(Uri);
    }

    [Fact]
    public void FindUsages_with_project_filter_includes_Unknown_entries()
    {
        var sut     = new BindingMatchService();
        var binding = GivenBinding("my step", file: "Steps.cs", line: 5);
        // Unknown entry — pre-baseline placeholder
        sut.Store(BuildSet(DefinedFeature, RegistryWith(binding), docUri: Uri));

        // Filter for OwnerA (a known project), but only Unknown entries exist.
        var usages = sut.FindUsages(new SourceLocation("Steps.cs", 5, 1), [OwnerA]);

        // Unknown entries are always included regardless of filter (backward compat during startup).
        usages.Should().ContainSingle();
    }

    [Fact]
    public void FindUsages_with_null_filter_returns_all_projects()
    {
        var sut     = new BindingMatchService();
        var binding = GivenBinding("my step", file: "Steps.cs", line: 5);
        sut.Store(BuildSet(DefinedFeature, RegistryWith(binding), docUri: Uri,       owner: OwnerA));
        sut.Store(BuildSet(DefinedFeature, RegistryWith(binding), docUri: SecondUri, owner: OwnerB));

        var usages = sut.FindUsages(new SourceLocation("Steps.cs", 5, 1));

        usages.Should().HaveCount(2);
    }

    // ── BindingId (issue #471) ──────────────────────────────────────────────────

    [Fact]
    public void BindingId_is_stable_for_the_same_binding_identity()
    {
        var b1 = GivenBinding("my step", method: "Steps.MyStep", file: "Steps.cs", line: 5);
        var b2 = GivenBinding("my step", method: "Steps.MyStep", file: "Steps.cs", line: 99); // different line

        BindingId.For(b1).Should().Be(BindingId.For(b2), "identity is content-based, not location-based");
    }

    [Fact]
    public void BindingId_differs_when_the_method_differs()
    {
        var b1 = GivenBinding("my step", method: "Steps.MethodA");
        var b2 = GivenBinding("my step", method: "Steps.MethodB");

        BindingId.For(b1).Should().NotBe(BindingId.For(b2));
    }

    [Fact]
    public void BindingId_differs_when_the_step_block_differs()
    {
        var given = new ProjectStepDefinitionBinding(ScenarioBlock.Given, new Regex("^my step$"), null,
            new ProjectBindingImplementation("Steps.MyStep", null, new SourceLocation("Steps.cs", 5, 1)));
        var when = new ProjectStepDefinitionBinding(ScenarioBlock.When, new Regex("^my step$"), null,
            new ProjectBindingImplementation("Steps.MyStep", null, new SourceLocation("Steps.cs", 5, 1)));

        BindingId.For(given).Should().NotBe(BindingId.For(when));
    }

    [Fact]
    public void BindingId_differs_when_the_expression_differs()
    {
        var b1 = GivenBinding("my step");
        var b2 = GivenBinding("my other step");

        BindingId.For(b1).Should().NotBe(BindingId.For(b2));
    }

    [Fact]
    public void BindingId_ToString_round_trips_through_TryParse()
    {
        var id = BindingId.For(GivenBinding("my step"));

        BindingId.TryParse(id.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(id);
    }

    // ── FindUsages(BindingId) (issue #471) ──────────────────────────────────────

    [Fact]
    public void FindUsages_by_BindingId_returns_steps_bound_to_that_binding()
    {
        var sut     = new BindingMatchService();
        var binding = GivenBinding("my step", file: "Steps.cs", line: 5);
        sut.Store(BuildSet(DefinedFeature, RegistryWith(binding)));

        var usages = sut.FindUsages(BindingId.For(binding));

        usages.Should().ContainSingle();
    }

    [Fact]
    public void FindUsages_by_BindingId_returns_nothing_for_an_unrelated_binding()
    {
        var sut     = new BindingMatchService();
        var binding = GivenBinding("my step", file: "Steps.cs", line: 5);
        sut.Store(BuildSet(DefinedFeature, RegistryWith(binding)));

        var other = GivenBinding("some other step", method: "Other");
        sut.FindUsages(BindingId.For(other)).Should().BeEmpty();
    }

    [Fact]
    public void FindUsages_by_BindingId_respects_project_filter_and_includes_Unknown_entries()
    {
        var sut     = new BindingMatchService();
        var binding = GivenBinding("my step", file: "Steps.cs", line: 5);
        sut.Store(BuildSet(DefinedFeature, RegistryWith(binding), docUri: Uri,       owner: OwnerA));
        sut.Store(BuildSet(DefinedFeature, RegistryWith(binding), docUri: SecondUri, owner: OwnerB));

        var id = BindingId.For(binding);
        sut.FindUsages(id, [OwnerA]).Should().ContainSingle().Which.FeatureDocumentId.Should().Be(Uri);
        sut.FindUsages(id).Should().HaveCount(2, "null filter returns all projects");
    }

    [Fact]
    public void FindUsages_by_BindingId_agrees_with_FindUsages_by_SourceLocation()
    {
        var sut     = new BindingMatchService();
        var binding = GivenBinding("my step", file: "Steps.cs", line: 5);
        sut.Store(BuildSet(DefinedFeature, RegistryWith(binding)));

        var byId = sut.FindUsages(BindingId.For(binding));
        var byLocation = sut.FindUsages(new SourceLocation("Steps.cs", 5, 1));

        byId.Should().BeEquivalentTo(byLocation);
    }

    // ── shard-precise eviction (issue #471) ─────────────────────────────────────

    [Fact]
    public void InvalidateAllForDocument_removes_only_that_documents_bindings_from_the_reverse_index()
    {
        var sut = new BindingMatchService();
        var bindingA = GivenBinding("step a", method: "MethodA", file: "A.cs", line: 5);
        var bindingB = GivenBinding("step b", method: "MethodB", file: "B.cs", line: 5);
        sut.Store(BuildSet("Feature: F\nScenario: S\n    Given step a\n", RegistryWith(bindingA), docUri: Uri));
        sut.Store(BuildSet("Feature: F\nScenario: S\n    Given step b\n", RegistryWith(bindingB), docUri: SecondUri));

        sut.InvalidateAllForDocument(Uri);

        sut.FindUsages(BindingId.For(bindingA)).Should().BeEmpty("its only document was invalidated");
        sut.FindUsages(BindingId.For(bindingB)).Should().ContainSingle("SecondUri's shard is untouched");
    }

    [Fact]
    public void Store_replacing_a_document_removes_its_old_bindings_from_the_reverse_index()
    {
        var sut = new BindingMatchService();
        var oldBinding = GivenBinding("old step", method: "OldMethod", file: "Steps.cs", line: 5);
        var newBinding = GivenBinding("new step", method: "NewMethod", file: "Steps.cs", line: 20);

        sut.Store(BuildSet("Feature: F\nScenario: S\n    Given old step\n", RegistryWith(oldBinding), version: 1));
        sut.Store(BuildSet("Feature: F\nScenario: S\n    Given new step\n", RegistryWith(newBinding), version: 2));

        sut.FindUsages(BindingId.For(oldBinding)).Should().BeEmpty("the old binding is no longer referenced by this document");
        sut.FindUsages(BindingId.For(newBinding)).Should().ContainSingle();
    }

    // ── location-index leeway (issue #471) ──────────────────────────────────────

    [Fact]
    public void FindUsages_by_SourceLocation_resolves_a_click_up_to_two_lines_above_the_binding_start()
    {
        var sut     = new BindingMatchService();
        var binding = GivenBinding("my step", file: "Steps.cs", line: 10);
        sut.Store(BuildSet(DefinedFeature, RegistryWith(binding)));

        sut.FindUsages(new SourceLocation("Steps.cs", 8, 1)).Should().ContainSingle("2-line backward leeway for the attribute line");
        sut.FindUsages(new SourceLocation("Steps.cs", 7, 1)).Should().BeEmpty("3 lines back is outside the leeway window");
    }

    [Fact]
    public void FindUsages_by_SourceLocation_resolves_both_bindings_that_share_a_source_line()
    {
        // Two attributes on one method share the same SourceLocation -- the location index must
        // surface both BindingIds at a tied StartLine, not just the first one inserted.
        var impl  = new ProjectBindingImplementation("Steps.MultiAttribute", null, new SourceLocation("Steps.cs", 5, 1));
        var given = new ProjectStepDefinitionBinding(ScenarioBlock.Given, new Regex("^first$"),  null, impl, "first");
        var when  = new ProjectStepDefinitionBinding(ScenarioBlock.When,  new Regex("^second$"), null, impl, "second");
        const string feature = "Feature: F\nScenario: S\n    Given first\n    When second\n";

        var sut = new BindingMatchService();
        sut.Store(BuildSet(feature, RegistryWith(given, when)));

        sut.FindUsages(new SourceLocation("Steps.cs", 5, 1)).Should().HaveCount(2, "both attributes at the same location must resolve");
    }

    [Fact]
    public void FindUsages_by_SourceLocation_isolates_closely_adjacent_bindings()
    {
        // Two bindings 4 lines apart -- inside the 2-line leeway of neither neighbour's window
        // when queried from the far side, so a click near one must not accidentally pick up the
        // other via an off-by-one in the binary search boundary.
        var first  = GivenBinding("step one", method: "Steps.One", file: "Steps.cs", line: 10);
        var second = GivenBinding("step two", method: "Steps.Two", file: "Steps.cs", line: 14);
        const string feature = "Feature: F\nScenario: S\n    Given step one\n    Given step two\n";

        var sut = new BindingMatchService();
        sut.Store(BuildSet(feature, RegistryWith(first, second)));

        sut.FindUsages(new SourceLocation("Steps.cs", 10, 1)).Should().ContainSingle().Which
           .Result.Items.Single().MatchedStepDefinition!.Implementation.Method.Should().Be("Steps.One");
        sut.FindUsages(new SourceLocation("Steps.cs", 14, 1)).Should().ContainSingle().Which
           .Result.Items.Single().MatchedStepDefinition!.Implementation.Method.Should().Be("Steps.Two");
        // First binding's window (with leeway) is [8,10]; second's is [12,14] -- line 11 falls
        // between both windows and must resolve to neither.
        sut.FindUsages(new SourceLocation("Steps.cs", 11, 1)).Should().BeEmpty("between both windows, outside either one's leeway");
    }

    [Fact]
    public void FindUsages_by_SourceLocation_resolves_correctly_at_realistic_file_scale()
    {
        // Real repro scale (issue #471, Reqnroll.VeryLargeFeature): ~1,300 step-definition
        // bindings in a single .cs file. Locks in that the binary-search location lookup (added
        // after profiling showed a linear scan runs ~40x slower at this scale) still resolves
        // correctly across the full range, not just at sizes small enough that a bug would be
        // masked by scanning past it anyway.
        const int count = 1300;
        var bindings = Enumerable.Range(0, count)
            .Select(i => GivenBinding($"step number {i}", method: $"Steps.Step{i}", file: "Steps.cs", line: 10 + i * 6))
            .ToArray();
        var feature = "Feature: F\nScenario: S\n" +
            string.Concat(Enumerable.Range(0, count).Select(i => $"    Given step number {i}\n"));

        var sut = new BindingMatchService();
        sut.Store(BuildSet(feature, RegistryWith(bindings)));

        AssertResolvesTo(sut, 0);
        AssertResolvesTo(sut, 650);
        AssertResolvesTo(sut, count - 1);
        sut.FindUsages(new SourceLocation("Steps.cs", 10 + 3 * 6 + 3, 1))
           .Should().BeEmpty("halfway between two bindings, outside leeway of either");

        static void AssertResolvesTo(BindingMatchService sut, int index) =>
            sut.FindUsages(new SourceLocation("Steps.cs", 10 + index * 6, 1)).Should().ContainSingle().Which
               .Result.Items.Single().MatchedStepDefinition!.Implementation.Method.Should().Be($"Steps.Step{index}");
    }

    // ── concurrency ──────────────────────────────────────────────────────────────

    [Fact]
    public void Store_InvalidateAllForDocument_and_FindUsages_are_safe_under_concurrent_access()
    {
        // _cache is a ConcurrentDictionary shared across the LSP server's request-handling
        // threads: a store from one document-sync notification can race a FindUsages call (or an
        // invalidation) for a different document. Drives real concurrent Store/FindUsages/
        // InvalidateAllForDocument calls across many distinct documents to confirm no exception,
        // torn read, or lost update under contention -- FindUsages' own comment claims
        // "ConcurrentDictionary enumeration is safe under concurrent writes" but nothing exercised
        // that claim under real concurrency before this test.
        var sut = new BindingMatchService();
        const int documentCount = 30;
        var registry = RegistryWith(GivenBinding("my step", file: "Steps.cs", line: 5));
        var docUris = Enumerable.Range(0, documentCount).Select(i => $"file:///c:/proj/f{i}.feature").ToArray();

        Parallel.ForEach(docUris, docUri =>
        {
            for (var i = 0; i < 20; i++)
            {
                sut.Store(BuildSet(DefinedFeature, registry, docUri: docUri));
                sut.FindUsages(new SourceLocation("Steps.cs", 5, 1));
                sut.InvalidateAllForDocument(docUri);
                sut.Store(BuildSet(DefinedFeature, registry, docUri: docUri));
            }
        });

        // Every document was re-Stored last (after its own invalidation), so all of them should
        // still resolve a usage -- no entry should have been lost or left invalidated.
        var usages = sut.FindUsages(new SourceLocation("Steps.cs", 5, 1));
        usages.Should().HaveCount(documentCount);
    }
}
