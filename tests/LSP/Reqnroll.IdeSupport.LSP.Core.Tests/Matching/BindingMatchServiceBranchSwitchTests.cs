using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Matching;

/// <summary>
/// Issue #928 -- scenarios D1, D2 and E2: after a branch switch changes which bindings exist, a
/// feature file that is re-matched against the new registry must replace (not add to) its previous
/// match set, and the usage index must end up exactly as if the document had been matched once
/// against the final registry.
/// </summary>
public class BindingMatchServiceBranchSwitchTests
{
    private const string Uri = "file:///c:/proj/closed.feature";
    private const string OtherUri = "file:///c:/proj/other.feature";
    private const string Feature = "Feature: F\nScenario: S\n    Given my step\n";

    private static readonly ProjectOwner Owner = new("C:/proj/A.csproj", "net8.0");
    private static readonly ProjectOwner OtherOwner = new("C:/proj/B.csproj", "net8.0");

    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ITelemetryService _telemetryService = Substitute.For<ITelemetryService>();
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();

    public BindingMatchServiceBranchSwitchTests()
        => _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());

    private static ProjectStepDefinitionBinding GivenBinding(string pattern, string method, string file, int line = 5) =>
        new(ScenarioBlock.Given,
            new Regex("^" + Regex.Escape(pattern) + "$"),
            null,
            new ProjectBindingImplementation(method, null, new SourceLocation(file, line, 1)));

    private static ProjectBindingRegistry RegistryWith(params ProjectStepDefinitionBinding[] bindings) =>
        new(bindings, Array.Empty<ProjectHookBinding>(), 0);

    private FeatureBindingMatchSet Match(
        string uri, string text, ProjectBindingRegistry registry, int version, ProjectOwner? owner = null)
    {
        var parser = new IdeSupportTagParser(_logger, _telemetryService, _configProvider);
        var tags = parser.Parse(new StubGherkinTextSnapshot(text), registry);
        return FeatureBindingMatchSet.FromTags(uri, version, registry.Version, tags, owner ?? Owner);
    }

    private static SourceLocation At(string file, int line = 5) => new(file, line, 1);

    // ── D1: the other branch removed a step definition a closed feature uses ──

    [Fact]
    public void D1_rematching_against_a_registry_without_the_binding_makes_the_step_undefined_and_drops_its_usage()
    {
        var sut = new BindingMatchService();
        var before = RegistryWith(GivenBinding("my step", "MyStep", "Steps.cs"));
        sut.Store(Match(Uri, Feature, before, version: 1));
        sut.FindUsages(At("Steps.cs")).Should().ContainSingle("precondition: the step used the binding");

        var after = RegistryWith();
        sut.Store(Match(Uri, Feature, after, version: 1));

        sut.FindUsages(At("Steps.cs")).Should().BeEmpty("the binding is gone, so nothing may still count as using it");
        sut.TryGet(new MatchSetKey(Uri, Owner), out var set).Should().BeTrue();
        set.Undefined.Should().ContainSingle();
        set.Defined.Should().BeEmpty();
        sut.AuditIndexConsistency().Should().BeEmpty();
    }

    [Fact]
    public void D1_a_document_that_is_not_rematched_keeps_its_old_usage_until_it_is()
    {
        // Documents the dependency the fix has to respect: usage counts only change when the
        // document is re-matched, so a registry change must trigger a re-match of closed features.
        var sut = new BindingMatchService();
        var before = RegistryWith(GivenBinding("my step", "MyStep", "Steps.cs"));
        sut.Store(Match(Uri, Feature, before, version: 1));
        sut.Store(Match(OtherUri, Feature, before, version: 1));

        sut.Store(Match(Uri, Feature, RegistryWith(), version: 1)); // only one document re-matched

        sut.FindUsages(At("Steps.cs")).Should().ContainSingle()
            .Which.FeatureDocumentId.Should().Be(OtherUri);
    }

    [Fact]
    public void D1_rematching_every_document_clears_the_usage_for_all_of_them()
    {
        var sut = new BindingMatchService();
        var before = RegistryWith(GivenBinding("my step", "MyStep", "Steps.cs"));
        sut.Store(Match(Uri, Feature, before, version: 1));
        sut.Store(Match(OtherUri, Feature, before, version: 1));

        var after = RegistryWith();
        sut.Store(Match(Uri, Feature, after, version: 1));
        sut.Store(Match(OtherUri, Feature, after, version: 1));

        sut.FindUsages(At("Steps.cs")).Should().BeEmpty();
    }

    // ── D2: the other branch added a second binding that matches the same step ──

    [Fact]
    public void D2_a_second_matching_binding_turns_a_defined_step_ambiguous()
    {
        var sut = new BindingMatchService();
        var first = GivenBinding("my step", "First", "A.cs");
        sut.Store(Match(Uri, Feature, RegistryWith(first), version: 1));

        var second = GivenBinding("my step", "Second", "B.cs");
        sut.Store(Match(Uri, Feature, RegistryWith(first, second), version: 1));

        sut.TryGet(new MatchSetKey(Uri, Owner), out var set).Should().BeTrue();
        set.Ambiguous.Should().ContainSingle();
        set.Defined.Should().BeEmpty();
    }

    [Fact]
    public void D2_removing_the_second_binding_again_restores_the_defined_state()
    {
        var sut = new BindingMatchService();
        var first = GivenBinding("my step", "First", "A.cs");
        var second = GivenBinding("my step", "Second", "B.cs");
        sut.Store(Match(Uri, Feature, RegistryWith(first, second), version: 1));

        sut.Store(Match(Uri, Feature, RegistryWith(first), version: 1));

        sut.TryGet(new MatchSetKey(Uri, Owner), out var set).Should().BeTrue();
        set.Defined.Should().ContainSingle();
        set.Ambiguous.Should().BeEmpty();
        sut.FindUsages(At("B.cs")).Should().BeEmpty("the second binding no longer exists");
    }

    // ── E2: incremental history must equal a clean match against the final registry ──

    [Fact]
    public void E2_switching_A_to_B_to_A_leaves_the_same_usage_index_as_matching_A_once()
    {
        var stepsOnA = RegistryWith(
            GivenBinding("my step", "MyStep", "Steps.cs"),
            GivenBinding("another step", "Another", "Steps.cs", line: 9));
        var stepsOnB = RegistryWith(GivenBinding("my step", "MyStep", "Moved.cs"));
        const string twoSteps = "Feature: F\nScenario: S\n    Given my step\n    And another step\n";

        var switched = new BindingMatchService();
        switched.Store(Match(Uri, twoSteps, stepsOnA, version: 1));
        switched.Store(Match(Uri, twoSteps, stepsOnB, version: 1));
        switched.Store(Match(Uri, twoSteps, stepsOnA, version: 1));

        var fresh = new BindingMatchService();
        fresh.Store(Match(Uri, twoSteps, stepsOnA, version: 1));

        switched.FindUsages(At("Steps.cs")).Count.Should().Be(fresh.FindUsages(At("Steps.cs")).Count);
        switched.FindUsages(At("Steps.cs", 9)).Count.Should().Be(fresh.FindUsages(At("Steps.cs", 9)).Count);
        switched.GetCacheStats().Should().Be(fresh.GetCacheStats());
        switched.AuditIndexConsistency().Should().BeEmpty();
    }

    // ── A4 (match-service side): a binding that moved files on the other branch ──

    [Fact]
    public void A4_binding_that_moved_files_leaves_no_usage_at_its_old_location()
    {
        // Same method identity, different file on each branch. The location index is upsert-only
        // (see BindingMatchService._locationIndex), and an entry is only replaced when the same
        // BindingId is stored under the *same file*; a move leaves the old file's entry behind, and
        // because the id still has usages (at the new location) it does not resolve to zero.
        var onA = RegistryWith(GivenBinding("my step", "MyStep", "Steps.cs"));
        var onB = RegistryWith(GivenBinding("my step", "MyStep", "Moved.cs"));

        var sut = new BindingMatchService();
        sut.Store(Match(Uri, Feature, onB, version: 1));
        sut.Store(Match(Uri, Feature, onA, version: 1)); // switched back: Moved.cs no longer exists

        sut.FindUsages(At("Steps.cs")).Should().ContainSingle();
        sut.FindUsages(At("Moved.cs")).Should().BeEmpty(
            "the binding no longer lives in Moved.cs, so a lookup there must not return its usages");
    }

    [Fact]
    public void A4_one_binding_recorded_at_two_paths_by_two_projects_is_found_at_both()
    {
        // BindingId is location-independent, so two projects' registries can legitimately record the
        // same binding at different paths at the same time (a referenced project discovered by its
        // own registry and, transitively, by the referencing project's). Fixing the stale-location
        // case by allowing only one file per id would break one of these lookups.
        var bindingInA = GivenBinding("my step", "MyStep", "A/Steps.cs");
        var inA = RegistryWith(bindingInA);
        var inB = RegistryWith(GivenBinding("my step", "MyStep", "B/Steps.cs"));

        var sut = new BindingMatchService();
        sut.Store(Match(Uri, Feature, inA, version: 1, owner: Owner));
        sut.Store(Match(OtherUri, Feature, inB, version: 1, owner: OtherOwner));

        sut.FindUsages(At("A/Steps.cs")).Should().ContainSingle()
            .Which.FeatureDocumentId.Should().Be(Uri);
        sut.FindUsages(At("B/Steps.cs")).Should().ContainSingle()
            .Which.FeatureDocumentId.Should().Be(OtherUri);
        sut.FindUsages(BindingId.For(bindingInA)).Should().HaveCount(2);
    }

    [Fact]
    public void A4_lookup_by_file_is_case_insensitive()
    {
        var sut = new BindingMatchService();
        sut.Store(Match(Uri, Feature, RegistryWith(GivenBinding("my step", "MyStep", "C:/Proj/Steps.cs")), version: 1));

        sut.FindUsages(At("c:/proj/steps.cs")).Should().ContainSingle();
    }
}
