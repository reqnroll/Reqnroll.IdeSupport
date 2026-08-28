using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Gherkin.Parsing;

public class DeveroomTagParserTests
{
    private readonly IIdeSupportLogger _logger;
    private readonly ITelemetryService _telemetryService;
    private readonly IDeveroomConfigurationProvider _configProvider;

    public DeveroomTagParserTests()
    {
        _logger = Substitute.For<IIdeSupportLogger>();
        _telemetryService = Substitute.For<ITelemetryService>();
        _configProvider = Substitute.For<IDeveroomConfigurationProvider>();
        _configProvider.GetConfiguration().Returns(new DeveroomConfiguration());
    }

    private DeveroomTagParser CreateSut() =>
        new(_logger, _telemetryService, _configProvider);

    private static IGherkinTextSnapshot Snap(string text) =>
        new StubGherkinTextSnapshot(text);

    private static ProjectBindingRegistry RegistryWith(params ProjectStepDefinitionBinding[] bindings) =>
        new(bindings, Array.Empty<ProjectHookBinding>(), 0);

    private static ProjectStepDefinitionBinding GivenBinding(string pattern, string method = "MyStep") =>
        new(ScenarioBlock.Given,
            new Regex("^" + Regex.Escape(pattern) + "$"),
            null,
            new ProjectBindingImplementation(method, null, new SourceLocation("Steps.cs", 5, 1)));

    // ── helpers ───────────────────────────────────────────────────────────────

    private IReadOnlyCollection<DeveroomTag> ParseTags(string text,
        ProjectBindingRegistry? registry = null)
    {
        var sut = CreateSut();
        return sut.Parse(Snap(text), registry ?? ProjectBindingRegistry.Invalid);
    }

    private static IEnumerable<DeveroomTag> OfType(IReadOnlyCollection<DeveroomTag> tags, string type) =>
        tags.Where(t => t.Type == type);

    private static DeveroomTag Single(IReadOnlyCollection<DeveroomTag> tags, string type) =>
        tags.Single(t => t.Type == type);

    // ScenarioOutlinePlaceholder tags come from two independent sources: TagRowCells tags every
    // Examples header cell with this type (Data is a Gherkin.Ast.TableCell), and AddPlaceholderTags
    // tags each <placeholder> found within a step's own text (Data is a
    // MatchedScenarioOutlinePlaceholder). Tests about step-text placeholder detection need to
    // filter to the latter, not just the tag type, or they'll also match unrelated header cells.
    private static IEnumerable<DeveroomTag> StepTextPlaceholderTags(IReadOnlyCollection<DeveroomTag> tags) =>
        OfType(tags, DeveroomTagTypes.ScenarioOutlinePlaceholder)
            .Where(t => t.Data is MatchedScenarioOutlinePlaceholder);

    // ── empty file ────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_file_produces_no_FeatureBlock_tag()
    {
        var tags = ParseTags(string.Empty);
        tags.Any(t => t.Type == DeveroomTagTypes.FeatureBlock).Should().BeFalse();
    }

    [Fact]
    public void Whitespace_only_file_produces_no_FeatureBlock_tag()
    {
        var tags = ParseTags("   \n  \n");
        tags.Any(t => t.Type == DeveroomTagTypes.FeatureBlock).Should().BeFalse();
    }

    // ── Feature block ─────────────────────────────────────────────────────────

    [Fact]
    public void Feature_produces_FeatureBlock_tag()
    {
        var text = "Feature: My Feature\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.FeatureBlock).Should().BeTrue();
    }

    [Fact]
    public void Feature_keyword_produces_DefinitionLineKeyword_tag()
    {
        var text = "Feature: My Feature\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.DefinitionLineKeyword).Should().BeTrue();
    }

    [Fact]
    public void Feature_with_description_produces_Description_tag()
    {
        var text = "Feature: My Feature\n  As a user\n  I want things\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.Description).Should().BeTrue();
    }

    // ── Scenario ──────────────────────────────────────────────────────────────

    [Fact]
    public void Scenario_produces_ScenarioDefinitionBlock_tag()
    {
        var text = "Feature: F\nScenario: S\n  Given a step\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.ScenarioDefinitionBlock).Should().BeTrue();
    }

    [Fact]
    public void Scenario_produces_StepBlock_tags_for_each_step()
    {
        var text = "Feature: F\nScenario: S\n  Given step1\n  When step2\n  Then step3\n";
        var tags = ParseTags(text);
        OfType(tags, DeveroomTagTypes.StepBlock).Should().HaveCount(3);
    }

    [Fact]
    public void Step_produces_StepKeyword_tag()
    {
        var text = "Feature: F\nScenario: S\n  Given a step\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.StepKeyword).Should().BeTrue();
    }

    // ── Undefined / Defined steps ─────────────────────────────────────────────

    [Fact]
    public void Unmatched_step_produces_UndefinedStep_tag()
    {
        // UndefinedStep is only emitted when the binding registry is NOT Invalid
        var registry = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(), Array.Empty<ProjectHookBinding>(), 0);
        var text = "Feature: F\nScenario: S\n  Given an unmatched step\n";
        var tags = ParseTags(text, registry);
        tags.Any(t => t.Type == DeveroomTagTypes.UndefinedStep).Should().BeTrue();
    }

    [Fact]
    public void Matched_step_produces_DefinedStep_tag()
    {
        var registry = RegistryWith(GivenBinding("a matched step"));
        var text = "Feature: F\nScenario: S\n  Given a matched step\n";
        var tags = ParseTags(text, registry);
        tags.Any(t => t.Type == DeveroomTagTypes.DefinedStep).Should().BeTrue();
    }

    [Fact]
    public void Matched_step_does_not_produce_UndefinedStep_tag()
    {
        var registry = RegistryWith(GivenBinding("a matched step"));
        var text = "Feature: F\nScenario: S\n  Given a matched step\n";
        var tags = ParseTags(text, registry);
        tags.Any(t => t.Type == DeveroomTagTypes.UndefinedStep).Should().BeFalse();
    }

    // ── Ambiguous step ────────────────────────────────────────────────────────

    [Fact]
    public void Ambiguous_step_produces_AmbiguousStep_tag()
    {
        var b1 = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex("^ambiguous step$"), null,
            new ProjectBindingImplementation("Method1", null, new SourceLocation("A.cs", 1, 1)));
        var b2 = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex("^ambiguous step$"), null,
            new ProjectBindingImplementation("Method2", null, new SourceLocation("B.cs", 1, 1)));
        var registry = RegistryWith(b1, b2);

        var text = "Feature: F\nScenario: S\n  Given ambiguous step\n";
        var tags = ParseTags(text, registry);
        tags.Any(t => t.Type == DeveroomTagTypes.AmbiguousStep).Should().BeTrue();
    }

    [Fact]
    public void Ambiguous_step_does_not_produce_BindingError_tag()
    {
        var b1 = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex("^ambiguous step$"), null,
            new ProjectBindingImplementation("Method1", null, new SourceLocation("A.cs", 1, 1)));
        var b2 = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex("^ambiguous step$"), null,
            new ProjectBindingImplementation("Method2", null, new SourceLocation("B.cs", 1, 1)));
        var registry = RegistryWith(b1, b2);

        var text = "Feature: F\nScenario: S\n  Given ambiguous step\n";
        var tags = ParseTags(text, registry);
        tags.Any(t => t.Type == DeveroomTagTypes.BindingError).Should().BeFalse();
    }

    // ── Step with parameter ───────────────────────────────────────────────────

    [Fact]
    public void Step_with_captured_group_produces_StepParameter_tag()
    {
        var binding = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex(@"^I have (\d+) items$"), null,
            new ProjectBindingImplementation("HaveItems", new[] { "System.Int32" },
                new SourceLocation("Steps.cs", 3, 1)));
        var registry = RegistryWith(binding);

        var text = "Feature: F\nScenario: S\n  Given I have 5 items\n";
        var tags = ParseTags(text, registry);
        tags.Any(t => t.Type == DeveroomTagTypes.StepParameter).Should().BeTrue();
    }

    // ── DataTable argument ────────────────────────────────────────────────────

    [Fact]
    public void DataTable_step_argument_produces_DataTable_tag()
    {
        var text = "Feature: F\nScenario: S\n  Given a table step\n    | col1 | col2 |\n    | a    | b    |\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.DataTable).Should().BeTrue();
    }

    [Fact]
    public void DataTable_header_row_produces_DataTableHeader_tag()
    {
        var text = "Feature: F\nScenario: S\n  Given a table step\n    | col1 | col2 |\n    | a    | b    |\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.DataTableHeader).Should().BeTrue();
    }

    // ── DocString argument ────────────────────────────────────────────────────

    [Fact]
    public void DocString_step_argument_produces_DocString_tag()
    {
        var text = "Feature: F\nScenario: S\n  Given a docstring step\n    \"\"\"\n    hello world\n    \"\"\"\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.DocString).Should().BeTrue();
    }

    // ── ScenarioOutline ───────────────────────────────────────────────────────

    [Fact]
    public void ScenarioOutline_produces_ScenarioDefinitionBlock_tag()
    {
        var text = "Feature: F\nScenario Outline: SO\n  Given <param>\n  Examples:\n    | param |\n    | v1    |\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.ScenarioDefinitionBlock).Should().BeTrue();
    }

    [Fact]
    public void ScenarioOutline_placeholder_produces_ScenarioOutlinePlaceholder_tag()
    {
        var text = "Feature: F\nScenario Outline: SO\n  Given I have <count> items\n  Examples:\n    | count |\n    | 5     |\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.ScenarioOutlinePlaceholder).Should().BeTrue();
    }

    [Fact]
    public void ScenarioOutline_examples_produces_ExamplesBlock_tag()
    {
        var text = "Feature: F\nScenario Outline: SO\n  Given <p>\n  Examples:\n    | p |\n    | x |\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.ExamplesBlock).Should().BeTrue();
    }

    [Fact]
    public void ScenarioOutline_multi_word_placeholder_produces_single_tag()
    {
        var text = "Feature: F\nScenario Outline: SO\n  Given a bay called <bay name>\n" +
                    "  Examples:\n    | bay name |\n    | Bay1     |\n";
        var tags = ParseTags(text);
        var placeholderTag = StepTextPlaceholderTags(tags).Should().ContainSingle().Which;
        ((MatchedScenarioOutlinePlaceholder)placeholderTag.Data).Name.Should().Be("bay name");
    }

    // Regression tests: "<"/">" used as comparison operators (not real placeholder syntax) used
    // to be misread as a placeholder because the matching regex greedily spanned from the first
    // "<" to the next ">" regardless of what was between them, and — worse — their mere presence
    // silently suppressed StepParameter tags for the whole step via a naive
    // step.Text.Contains("<") heuristic, even when there was no real placeholder to conflict with.
    [Fact]
    public void Comparison_operators_in_a_ScenarioOutline_step_do_not_produce_a_placeholder_tag()
    {
        var text = "Feature: F\nScenario Outline: SO\n  Given the count < 5 and total > 10 items\n" +
                    "  Examples:\n    | unused |\n    | x      |\n";
        var tags = ParseTags(text);
        StepTextPlaceholderTags(tags).Should().BeEmpty();
    }

    [Fact]
    public void Comparison_operators_in_a_ScenarioOutline_step_do_not_suppress_StepParameter_tags()
    {
        var binding = new ProjectStepDefinitionBinding(ScenarioBlock.Given,
            new Regex(@"^the count < (\d+) and total > (\d+) items$"), null,
            new ProjectBindingImplementation("CountCheck", new[] { "System.Int32", "System.Int32" },
                new SourceLocation("Steps.cs", 3, 1)));
        var registry = RegistryWith(binding);

        var text = "Feature: F\nScenario Outline: SO\n  Given the count < 5 and total > 10 items\n" +
                    "  Examples:\n    | unused |\n    | x      |\n";
        var tags = ParseTags(text, registry);
        tags.Any(t => t.Type == DeveroomTagTypes.StepParameter).Should().BeTrue();
    }

    // Compound cases: a real placeholder sharing a line with comparison-operator "<"/">" usage.
    // These matter more than the isolated case above because a fix that merely happens to work
    // on a single condition could still misbehave once there's a real placeholder nearby for the
    // greedy backtracking to latch onto.

    [Fact]
    public void Real_placeholder_is_found_while_comparison_operators_after_it_are_ignored()
    {
        var text = "Feature: F\nScenario Outline: SO\n  Given the <value> is < 5 and > 10\n" +
                    "  Examples:\n    | value |\n    | 7     |\n";
        var tags = ParseTags(text);
        var placeholder = StepTextPlaceholderTags(tags).Should().ContainSingle().Which;
        ((MatchedScenarioOutlinePlaceholder)placeholder.Data).Name.Should().Be("value");
    }

    [Fact]
    public void Real_placeholder_is_found_while_comparison_operators_earlier_in_the_line_are_ignored()
    {
        var text = "Feature: F\nScenario Outline: SO\n" +
                    "  Given the value is < 5 and > 10 and aligned with <placeholder>\n" +
                    "  Examples:\n    | placeholder |\n    | x           |\n";
        var tags = ParseTags(text);
        var placeholder = StepTextPlaceholderTags(tags).Should().ContainSingle().Which;
        ((MatchedScenarioOutlinePlaceholder)placeholder.Data).Name.Should().Be("placeholder");
    }

    [Fact]
    public void Two_real_placeholders_immediately_adjacent_to_comparison_operators_are_both_found()
    {
        var text = "Feature: F\nScenario Outline: SO\n" +
                    "  Given the value is < <highValue> and > <lowValue>\n" +
                    "  Examples:\n    | highValue | lowValue |\n    | 10        | 1        |\n";
        var tags = ParseTags(text);
        var names = StepTextPlaceholderTags(tags)
            .Select(t => ((MatchedScenarioOutlinePlaceholder)t.Data).Name);
        names.Should().BeEquivalentTo("highValue", "lowValue");
    }

    // Documents that the known spaceless-form limitation (see MatchedScenarioOutlinePlaceholder's
    // regex comment) persists even when a real placeholder is also present on the line — it isn't
    // masked or fixed by the presence of legitimate placeholder syntax elsewhere. If this ever
    // starts failing because the false match disappears, that's a welcome improvement; update the
    // assertion rather than treating it as a regression.
    [Fact]
    public void Known_limitation_spaceless_comparison_operators_still_false_match_alongside_a_real_placeholder()
    {
        var text = "Feature: F\nScenario Outline: SO\n  Given the <value> is <5 and b>10\n" +
                    "  Examples:\n    | value |\n    | 7     |\n";
        var tags = ParseTags(text);
        var names = StepTextPlaceholderTags(tags)
            .Select(t => ((MatchedScenarioOutlinePlaceholder)t.Data).Name);
        names.Should().BeEquivalentTo("value", "5 and b");
    }

    // ── Background ────────────────────────────────────────────────────────────

    [Fact]
    public void Background_produces_ScenarioDefinitionBlock_tag()
    {
        var text = "Feature: F\nBackground:\n  Given a background step\nScenario: S\n  Given another step\n";
        var tags = ParseTags(text);
        // Background counts as a ScenarioDefinitionBlock
        OfType(tags, DeveroomTagTypes.ScenarioDefinitionBlock).Should().NotBeEmpty();
    }

    // ── Gherkin Tags ──────────────────────────────────────────────────────────

    [Fact]
    public void Gherkin_tag_annotation_produces_Tag_tag()
    {
        var text = "@smoke\nFeature: F\n@fast\nScenario: S\n  Given a step\n";
        var tags = ParseTags(text);
        OfType(tags, DeveroomTagTypes.Tag).Should().NotBeEmpty();
    }

    // ── Scenario hook (BeforeScenario) ────────────────────────────────────────

    [Fact]
    public void BeforeScenario_hook_produces_ScenarioHookReference_for_tagged_scenario()
    {
        var hook = new ProjectHookBinding(
            new ProjectBindingImplementation("HookMethod", null, new SourceLocation("Hooks.cs", 10, 1)),
            null,
            HookType.BeforeScenario,
            null,
            null);
        var registry = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook }, 0);

        var text = "Feature: F\nScenario: S\n  Given a step\n";
        var tags = ParseTags(text, registry);
        tags.Any(t => t.Type == DeveroomTagTypes.ScenarioHookReference).Should().BeTrue();
    }

    // ── Parser error ──────────────────────────────────────────────────────────

    [Fact]
    public void Malformed_feature_produces_ParserError_tag()
    {
        var text = "not a feature file\nsome garbage\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.ParserError).Should().BeTrue();
    }

    [Fact]
    public void Incomplete_scenario_without_steps_produces_tag_or_parses_gracefully()
    {
        // An incomplete file (truncated mid-step-text) may or may not emit a ParserError
        // depending on Gherkin's recovery heuristics; we verify the parser does not throw
        // and returns a non-null collection.
        var text = "Feature: F\nScenario: S\n  Given";
        var tags = ParseTags(text);
        tags.Should().NotBeNull();
    }

    // ── Rule block ────────────────────────────────────────────────────────────

    [Fact]
    public void Rule_produces_RuleBlock_tag()
    {
        var text = "Feature: F\nRule: My Rule\nScenario: S\n  Given a step\n";
        var tags = ParseTags(text);
        tags.Any(t => t.Type == DeveroomTagTypes.RuleBlock).Should().BeTrue();
    }

    // ── Multi-step scenario (Given/When/Then) ─────────────────────────────────

    [Fact]
    public void GivenWhenThen_steps_all_get_StepBlock_tags()
    {
        var given = GivenBinding("first step", "Given1");
        var when = new ProjectStepDefinitionBinding(ScenarioBlock.When,
            new Regex("^second step$"), null,
            new ProjectBindingImplementation("When1", null, new SourceLocation("S.cs", 2, 1)));
        var then = new ProjectStepDefinitionBinding(ScenarioBlock.Then,
            new Regex("^third step$"), null,
            new ProjectBindingImplementation("Then1", null, new SourceLocation("S.cs", 3, 1)));
        var registry = RegistryWith(given, when, then);

        var text = "Feature: F\nScenario: S\n  Given first step\n  When second step\n  Then third step\n";
        var tags = ParseTags(text, registry);
        OfType(tags, DeveroomTagTypes.StepBlock).Should().HaveCount(3);
        OfType(tags, DeveroomTagTypes.DefinedStep).Should().HaveCount(3);
    }

    // ── Range properties ──────────────────────────────────────────────────────

    [Fact]
    public void All_tags_have_non_null_ranges()
    {
        var text = "Feature: F\nScenario: S\n  Given a step\n";
        var tags = ParseTags(text);
        tags.Should().NotBeEmpty();
        tags.Should().AllSatisfy(t => t.Range.Should().NotBeNull());
    }

    [Fact]
    public void FeatureBlock_range_spans_entire_document()
    {
        var text = "Feature: F\nScenario: S\n  Given a step\n";
        var snap = Snap(text);
        var sut = CreateSut();
        var tags = sut.Parse(snap, ProjectBindingRegistry.Invalid);
        var featureBlock = tags.First(t => t.Type == DeveroomTagTypes.FeatureBlock);
        featureBlock.Range.Start.Should().Be(0);
        featureBlock.Range.End.Should().Be(snap.Length);
    }

    // ── And/But keywords ─────────────────────────────────────────────────────

    [Fact]
    public void And_But_steps_are_treated_as_steps_and_get_StepBlock_tags()
    {
        var text = "Feature: F\nScenario: S\n  Given first step\n  And second step\n  But third step\n";
        var tags = ParseTags(text);
        OfType(tags, DeveroomTagTypes.StepBlock).Should().HaveCount(3);
    }

    // ── Unhandled exception guard ─────────────────────────────────────────────

    [Fact]
    public void Exception_in_configuration_returns_empty_collection()
    {
        _configProvider.GetConfiguration().Returns(_ => throw new InvalidOperationException("boom"));
        var tags = ParseTags("Feature: F\nScenario: S\n  Given a step\n");
        tags.Should().BeEmpty();
    }
}
