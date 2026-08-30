using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Matching;

/// <summary>
/// Direct construction/lookup coverage for <see cref="FeatureBindingMatchSet"/> itself
/// (its constructor, <see cref="FeatureBindingMatchSet.FromTags"/>, and
/// <see cref="FeatureBindingMatchSet.FindAt"/>), isolated from the services that only
/// exercise it indirectly (<c>BindingMatchService</c>, the diagnostics aggregator, the inlay
/// hint service, and the LSP handlers).
/// </summary>
public class FeatureBindingMatchSetTests
{
    private const string Uri = "file:///c:/proj/feature1.feature";

    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ITelemetryService _telemetryService = Substitute.For<ITelemetryService>();
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();

    public FeatureBindingMatchSetTests()
    {
        _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());
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

    private IReadOnlyCollection<IdeSupportTag> ParseTags(string text, ProjectBindingRegistry registry)
    {
        var parser = new IdeSupportTagParser(_logger, _telemetryService, _configProvider);
        return parser.Parse(new StubGherkinTextSnapshot(text), registry);
    }

    private FeatureBindingMatchSet BuildSet(
        string text, ProjectBindingRegistry registry,
        int? version = 1, int registryVersion = 0, string docUri = Uri, ProjectOwner owner = default)
    {
        var tags = ParseTags(text, registry);
        return FeatureBindingMatchSet.FromTags(docUri, version, registryVersion, tags, owner);
    }

    private static readonly ProjectOwner OwnerA = new("C:/proj/A.csproj", "net8.0");

    private const string DefinedFeature = "Feature: F\nScenario: S\n    Given my step\n";

    // ── constructor ──────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_null_documentId_throws()
    {
        var act = () => new FeatureBindingMatchSet(null!, ProjectOwner.Unknown, 1, 0, Array.Empty<StepBindingMatch>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("documentId");
    }

    [Fact]
    public void Constructor_null_steps_throws()
    {
        var act = () => new FeatureBindingMatchSet(Uri, ProjectOwner.Unknown, 1, 0, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("steps");
    }

    [Fact]
    public void Constructor_without_scenarios_defaults_to_empty()
    {
        var set = new FeatureBindingMatchSet(Uri, ProjectOwner.Unknown, 1, 0, Array.Empty<StepBindingMatch>());

        set.Scenarios.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_stores_document_and_registry_version()
    {
        var set = new FeatureBindingMatchSet(Uri, ProjectOwner.Unknown, 7, 42, Array.Empty<StepBindingMatch>());

        set.DocumentVersion.Should().Be(7);
        set.RegistryVersion.Should().Be(42);
        set.DocumentId.Should().Be(Uri);
    }

    [Fact]
    public void Constructor_unknown_owner_normalizes_to_ProjectOwner_Unknown()
    {
        // owner.IsKnown is false for default(ProjectOwner), so the Key stores ProjectOwner.Unknown
        // rather than the passed-through empty struct — this is what keeps MatchSetKey.ForUnknownProject
        // lookups working regardless of how the caller constructed the (empty) owner value.
        var set = new FeatureBindingMatchSet(Uri, default, 1, 0, Array.Empty<StepBindingMatch>());

        set.Owner.Should().Be(ProjectOwner.Unknown);
    }

    [Fact]
    public void Empty_singleton_has_no_steps_no_scenarios_and_unknown_owner()
    {
        FeatureBindingMatchSet.Empty.Steps.Should().BeEmpty();
        FeatureBindingMatchSet.Empty.Scenarios.Should().BeEmpty();
        FeatureBindingMatchSet.Empty.Owner.Should().Be(ProjectOwner.Unknown);
        FeatureBindingMatchSet.Empty.DocumentId.Should().Be(string.Empty);
    }

    // ── FromTags ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FromTags_captures_a_defined_step_match()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));

        set.Steps.Should().ContainSingle();
        set.Steps[0].IsDefined.Should().BeTrue();
    }

    [Fact]
    public void FromTags_collapses_duplicate_tags_sharing_the_same_span_into_one_step()
    {
        // A single step can emit both a DefinedStep and an UndefinedStep tag at the same span
        // (e.g. a scenario outline whose example rows partly match) — FromTags must collapse
        // these into a single StepBindingMatch entry rather than one per tag.
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));

        set.Steps.Should().HaveCount(1, "duplicate tags at the same span must collapse to one entry");
    }

    [Fact]
    public void FromTags_orders_steps_by_range_start()
    {
        const string feature = "Feature: F\nScenario: S\n    Given first step\n    Given second step\n";
        var set = BuildSet(feature, RegistryWith(GivenBinding("first step"), GivenBinding("second step")));

        set.Steps.Should().HaveCount(2);
        set.Steps[0].Range.Start.Should().BeLessThan(set.Steps[1].Range.Start);
    }

    [Fact]
    public void FromTags_derives_short_project_name_from_owner_project_file()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")), owner: OwnerA);

        set.Steps[0].ProjectName.Should().Be("A");
    }

    [Fact]
    public void FromTags_leaves_project_name_null_for_unknown_owner()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));

        set.Steps[0].ProjectName.Should().BeNull();
    }

    [Fact]
    public void FromTags_captures_keyword_for_each_step()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));

        set.Steps[0].Keyword.Should().Be("Given");
    }

    [Fact]
    public void FromTags_stores_owner_and_versions_on_the_key()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")), version: 3, registryVersion: 9, owner: OwnerA);

        set.Key.DocumentId.Should().Be(Uri);
        set.Key.Owner.Should().Be(OwnerA);
        set.DocumentVersion.Should().Be(3);
        set.RegistryVersion.Should().Be(9);
    }

    [Fact]
    public void FromTags_captures_one_scenario_per_definition_excluding_background()
    {
        const string feature = "Feature: F\nBackground:\n    Given setup\nScenario: S\n    Given my step\n";
        var set = BuildSet(feature, RegistryWith(GivenBinding("my step"), GivenBinding("setup")));

        set.Scenarios.Should().ContainSingle();
        set.Scenarios[0].Name.Should().Be("S");
    }

    [Fact]
    public void FromTags_empty_tag_collection_produces_empty_steps_and_scenarios()
    {
        var set = FeatureBindingMatchSet.FromTags(Uri, 1, 0, Array.Empty<IdeSupportTag>());

        set.Steps.Should().BeEmpty();
        set.Scenarios.Should().BeEmpty();
    }

    // ── FindAt ───────────────────────────────────────────────────────────────────

    [Fact]
    public void FindAt_returns_the_step_whose_span_contains_the_offset()
    {
        var set  = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));
        var step = set.Steps[0];

        set.FindAt(step.Range.Start).Should().BeSameAs(step);
        set.FindAt(step.Range.End - 1).Should().BeSameAs(step);
    }

    [Fact]
    public void FindAt_returns_null_when_no_step_contains_the_offset()
    {
        var set = BuildSet(DefinedFeature, RegistryWith(GivenBinding("my step")));

        set.FindAt(0).Should().BeNull();
    }

    [Fact]
    public void FindAt_picks_the_first_step_whose_line_contains_the_offset_when_multiple_steps_exist()
    {
        const string feature = "Feature: F\nScenario: S\n    Given first step\n    Given second step\n";
        var set = BuildSet(feature, RegistryWith(GivenBinding("first step"), GivenBinding("second step")));

        set.FindAt(set.Steps[1].Range.Start).Should().BeSameAs(set.Steps[1]);
    }

    [Fact]
    public void FindAt_on_empty_set_always_returns_null()
    {
        FeatureBindingMatchSet.Empty.FindAt(0).Should().BeNull();
        FeatureBindingMatchSet.Empty.FindAt(100).Should().BeNull();
    }
}
