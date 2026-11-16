using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Matching;

/// <summary>
/// The stamp write rule as <see cref="BindingMatchService.Store"/> enforces it: a rejected write is
/// dropped and the entry already stored stays in place.
/// </summary>
public class BindingMatchServiceStampOutcomeTests
{
    private const string Uri = "file:///c:/proj/feature1.feature";

    private static readonly ProjectOwner Owner = new("C:/proj/A.csproj", "net8.0");
    private static readonly ProjectOwner OtherOwner = new("C:/proj/B.csproj", "net8.0");

    private readonly DocumentGenerations _generations = new();
    private readonly IIdeSupportLogger   _logger      = Substitute.For<IIdeSupportLogger>();
    private readonly List<LogMessage> _logged = new();
    private readonly BindingMatchService _sut;

    public BindingMatchServiceStampOutcomeTests()
    {
        _logger.Level.Returns(TraceLevel.Verbose);
        _logger.When(l => l.Log(Arg.Any<LogMessage>())).Do(c => _logged.Add(c.Arg<LogMessage>()));
        _sut = new BindingMatchService(_generations, _logger);
    }

    private static FeatureBindingMatchSet SetWith(
        long content, long config, int registry, string uri = Uri, ProjectOwner? owner = null) =>
        FeatureBindingMatchSet.FromTags(
            uri, 1, new DerivationStamp(content, config, registry), Array.Empty<IdeSupportTag>(), owner ?? Owner);

    private bool IsStored(ProjectOwner owner) => _sut.TryGet(new MatchSetKey(Uri, owner), out _);

    private DerivationStamp StoredStamp()
    {
        _sut.TryGet(new MatchSetKey(Uri, Owner), out var stored).Should().BeTrue();
        return stored.Stamp;
    }

    private void BumpTo(long generation)
    {
        while (_generations.Current(Uri) < generation)
            _generations.Bump(Uri);
    }

    [Fact]
    public void First_write_for_the_current_generation_is_accepted()
    {
        BumpTo(3);

        _sut.Store(SetWith(3, 0, 10)).Should().Be(StoreOutcome.Accepted);

        _sut.GetStoreStats().Should().Be(new StoreStats(1, 0, 0));
    }

    [Fact]
    public void Write_built_from_text_the_document_has_since_left_is_superseded_and_dropped()
    {
        BumpTo(4);

        _sut.Store(SetWith(3, 0, 10)).Should().Be(StoreOutcome.RejectedSuperseded);

        IsStored(Owner).Should().BeFalse("a superseded write must not create an entry");
        _sut.GetStoreStats().Should().Be(new StoreStats(0, 1, 0));
    }

    [Fact]
    public void Superseded_write_leaves_the_entry_already_stored_in_place()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 0, 10)).Should().Be(StoreOutcome.Accepted);
        BumpTo(4);

        _sut.Store(SetWith(3, 0, 11)).Should().Be(StoreOutcome.RejectedSuperseded);

        StoredStamp().Should().Be(new DerivationStamp(3, 0, 10));
    }

    [Fact]
    public void Write_for_a_generation_the_document_has_not_reached_is_also_superseded()
    {
        BumpTo(2);

        _sut.Store(SetWith(3, 0, 10)).Should().Be(StoreOutcome.RejectedSuperseded);
    }

    [Fact]
    public void Older_registry_for_the_same_text_arriving_after_a_newer_one_is_stale_and_dropped()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 0, 12)).Should().Be(StoreOutcome.Accepted);

        _sut.Store(SetWith(3, 0, 11)).Should().Be(StoreOutcome.RejectedStale);

        StoredStamp().Should().Be(new DerivationStamp(3, 0, 12), "the newer derivation stays");
        _sut.GetStoreStats().Should().Be(new StoreStats(1, 0, 1));
    }

    [Fact]
    public void Older_configuration_for_the_same_text_and_registry_is_stale()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 2, 10));

        _sut.Store(SetWith(3, 1, 10)).Should().Be(StoreOutcome.RejectedStale);

        StoredStamp().Should().Be(new DerivationStamp(3, 2, 10));
    }

    [Fact]
    public void Registries_of_two_owners_of_one_document_advance_independently()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 0, 20, owner: Owner)).Should().Be(StoreOutcome.Accepted);

        // The other project's registry is at an older version than the first owner's: its stamp is
        // only compared with its own entry, so it is not stale.
        _sut.Store(SetWith(3, 0, 5, owner: OtherOwner)).Should().Be(StoreOutcome.Accepted);
        _sut.Store(SetWith(3, 0, 6, owner: OtherOwner)).Should().Be(StoreOutcome.Accepted);

        _sut.Store(SetWith(3, 0, 19, owner: Owner)).Should().Be(StoreOutcome.RejectedStale);
        _sut.Store(SetWith(3, 0, 5, owner: OtherOwner)).Should().Be(StoreOutcome.RejectedStale);

        _sut.TryGet(new MatchSetKey(Uri, Owner), out var first).Should().BeTrue();
        _sut.TryGet(new MatchSetKey(Uri, OtherOwner), out var second).Should().BeTrue();
        first.Stamp.Should().Be(new DerivationStamp(3, 0, 20));
        second.Stamp.Should().Be(new DerivationStamp(3, 0, 6));
    }

    [Fact]
    public void A_content_generation_bump_supersedes_every_owner_of_the_document()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 0, 10, owner: Owner));
        _sut.Store(SetWith(3, 0, 10, owner: OtherOwner));
        BumpTo(4);

        _sut.Store(SetWith(3, 0, 11, owner: Owner)).Should().Be(StoreOutcome.RejectedSuperseded);
        _sut.Store(SetWith(3, 0, 11, owner: OtherOwner)).Should().Be(StoreOutcome.RejectedSuperseded);
    }

    [Fact]
    public void Placeholder_for_an_unknown_owner_is_not_evicted_by_a_dropped_write()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 0, 10, owner: ProjectOwner.Unknown)).Should().Be(StoreOutcome.Accepted);
        BumpTo(4);

        _sut.Store(SetWith(3, 0, 10, owner: Owner)).Should().Be(StoreOutcome.RejectedSuperseded);

        IsStored(ProjectOwner.Unknown).Should().BeTrue("a write that was dropped must not have side effects");
        IsStored(Owner).Should().BeFalse();
    }

    [Fact]
    public void Equal_stamps_are_accepted()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 1, 10));

        _sut.Store(SetWith(3, 1, 10)).Should().Be(StoreOutcome.Accepted);
    }

    [Fact]
    public void Newer_registry_replaces_older_one_for_the_same_text()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 0, 10));

        _sut.Store(SetWith(3, 0, 11)).Should().Be(StoreOutcome.Accepted);
    }

    [Fact]
    public void Generations_are_tracked_per_document()
    {
        _generations.Bump("file:///c:/proj/other.feature");
        BumpTo(0);

        _sut.Store(SetWith(0, 0, 10)).Should().Be(StoreOutcome.Accepted,
            "another document's generation must not supersede this one");
    }

    [Fact]
    public void Rejected_write_is_logged_with_the_stamps_and_the_current_generation()
    {
        BumpTo(4);

        _sut.Store(SetWith(3, 0, 10));

        var line = _logged.Should().ContainSingle(m => m.Level == TraceLevel.Verbose).Subject.Message;
        line.Should().Contain("RejectedSuperseded").And.Contain(Uri).And.Contain("documentVersion=1")
            .And.Contain("currentGeneration=4").And.Contain("<none>").And.Contain("dropped");
    }

    [Fact]
    public void Accepted_write_is_not_logged()
    {
        BumpTo(3);

        _sut.Store(SetWith(3, 0, 10));

        _logged.Should().BeEmpty();
    }

    [Fact]
    public void Newer_text_derived_from_an_older_registry_is_warned_about()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 0, 12));
        BumpTo(4);

        _sut.Store(SetWith(4, 0, 11)).Should().Be(StoreOutcome.Accepted);

        _logged.Should().ContainSingle(m => m.Level == TraceLevel.Warning && m.Message.Contains("project-input regression"));
    }

    [Fact]
    public void Newer_text_derived_from_an_older_configuration_is_warned_about()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 2, 10));
        BumpTo(4);

        _sut.Store(SetWith(4, 1, 10));

        _logged.Should().ContainSingle(m => m.Level == TraceLevel.Warning && m.Message.Contains("project-input regression"));
    }

    [Fact]
    public void Newer_text_with_newer_inputs_is_not_warned_about()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 0, 10));
        BumpTo(4);

        _sut.Store(SetWith(4, 0, 11));

        _logged.Should().NotContain(m => m.Level == TraceLevel.Warning);
    }
}
