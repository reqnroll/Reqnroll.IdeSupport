using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Matching;

/// <summary>
/// Issue #554 probe: <see cref="BindingMatchService.Store"/> updates <c>_cache</c> and the reverse
/// index as three separate operations (read previous → remove previous's index entries → write
/// cache → add new index entries) with no mutual exclusion, while the server calls it for the same
/// (document, owner) key from several unsynchronised pipeline entry points
/// (<c>TextDocumentSyncHandler</c>, <c>DocumentActivatedHandler</c>,
/// <c>BindingRegistryChangedHandler</c>, <c>ReqnrollConfigChangedHandler</c> all reach
/// <c>GherkinDocumentTaggerService.ParseAsync</c>, and only one of them goes through
/// <c>ParseCoordinator</c>'s per-URI serialisation).
///
/// These tests demonstrate that two concurrent stores for the same key can leave the losing set's
/// entries orphaned in the reverse index — a permanent +1 on every usage count for that document,
/// for the rest of the process's lifetime, which is exactly the shape reported in #554.
/// </summary>
public class BindingMatchServiceConcurrencyTests
{
    private const string Uri = "file:///c:/proj/feature1.feature";

    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ITelemetryService _telemetryService = Substitute.For<ITelemetryService>();
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();

    private static readonly ProjectOwner OwnerA = new("C:/proj/A.csproj", "net8.0");
    private const string DefinedFeature = "Feature: F\nScenario: S\n    Given my step\n";

    public BindingMatchServiceConcurrencyTests()
    {
        _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());
    }

    private static ProjectStepDefinitionBinding GivenBinding(
        string pattern, string method = "MyStep", string file = "Steps.cs", int line = 5) =>
        new(ScenarioBlock.Given,
            new Regex("^" + Regex.Escape(pattern) + "$"),
            null,
            new ProjectBindingImplementation(method, null, new SourceLocation(file, line, 1)));

    private static ProjectBindingRegistry RegistryWith(params ProjectStepDefinitionBinding[] bindings) =>
        new(bindings, Array.Empty<ProjectHookBinding>(), 0);

    private FeatureBindingMatchSet BuildSet(ProjectBindingRegistry registry, int version)
    {
        var parser = new IdeSupportTagParser(_logger, _telemetryService, _configProvider);
        var tags = parser.Parse(new StubGherkinTextSnapshot(DefinedFeature), registry);
        return FeatureBindingMatchSet.FromTags(Uri, version, registry.Version, tags, OwnerA);
    }

    [Fact]
    public async Task Concurrent_Store_calls_for_the_same_key_must_not_inflate_the_usage_count()
    {
        var registry = RegistryWith(GivenBinding("my step"));
        var location = new SourceLocation("Steps.cs", 5, 1);

        var inflatedAt = -1;
        var observedCount = 0;
        IReadOnlyList<string> audit = Array.Empty<string>();

        // Each round: two threads store a *different* match set object under the identical key,
        // released together. Whatever the winner is, exactly one set may remain indexed.
        for (var round = 0; round < 2000 && inflatedAt < 0; round++)
        {
            var sut = new BindingMatchService();
            sut.Store(BuildSet(registry, version: 0));   // the "previous" entry the race replaces

            var first = BuildSet(registry, version: round * 2 + 1);
            var second = BuildSet(registry, version: round * 2 + 2);

            using var gate = new Barrier(2);
            var t1 = Task.Run(() => { gate.SignalAndWait(); sut.Store(first); });
            var t2 = Task.Run(() => { gate.SignalAndWait(); sut.Store(second); });
            await Task.WhenAll(t1, t2);

            var count = sut.FindUsages(location).Count;
            if (count != 1)
            {
                inflatedAt = round;
                observedCount = count;
                audit = sut.AuditIndexConsistency();
            }
        }

        inflatedAt.Should().Be(-1,
            $"two concurrent Store calls for the same key left {observedCount} usage(s) indexed " +
            $"for a single step after round {inflatedAt} (issue #554). Audit said: " +
            $"{string.Join(" | ", audit)}");
    }

    [Fact]
    public async Task Concurrent_Store_and_Invalidate_must_not_leave_orphaned_index_entries()
    {
        var registry = RegistryWith(GivenBinding("my step"));
        var location = new SourceLocation("Steps.cs", 5, 1);

        var leakedAt = -1;
        var observedCount = 0;

        for (var round = 0; round < 2000 && leakedAt < 0; round++)
        {
            var sut = new BindingMatchService();
            sut.Store(BuildSet(registry, version: 0));

            var next = BuildSet(registry, version: round + 1);

            using var gate = new Barrier(2);
            var t1 = Task.Run(() => { gate.SignalAndWait(); sut.Store(next); });
            var t2 = Task.Run(() => { gate.SignalAndWait(); sut.InvalidateAllForDocument(Uri); });
            await Task.WhenAll(t1, t2);

            // After an invalidate racing a store, the cache and the reverse index must agree:
            // either the store won (1 cached set, 1 usage) or the invalidate won (0 and 0).
            var cached = sut.TryGet(new MatchSetKey(Uri, OwnerA), out _) ? 1 : 0;
            var count = sut.FindUsages(location).Count;
            if (count != cached)
            {
                leakedAt = round;
                observedCount = count;
            }
        }

        leakedAt.Should().Be(-1,
            $"a Store racing InvalidateAllForDocument left the reverse index reporting " +
            $"{observedCount} usage(s) that the cache no longer agrees with, after round {leakedAt} (issue #554)");
    }

    [Fact]
    public void AuditIndexConsistency_is_silent_when_the_index_matches_the_cache()
    {
        var sut = new BindingMatchService();
        sut.Store(BuildSet(RegistryWith(GivenBinding("my step")), version: 1));
        sut.Store(BuildSet(RegistryWith(GivenBinding("my step")), version: 2));

        sut.AuditIndexConsistency().Should().BeEmpty();
    }
}
