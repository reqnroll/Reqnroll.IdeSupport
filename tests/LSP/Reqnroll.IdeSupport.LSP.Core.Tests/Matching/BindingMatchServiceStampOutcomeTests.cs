using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Matching;

/// <summary>
/// The stamp write rule as <see cref="BindingMatchService.Store"/> reports it. The guard only
/// observes for now, so every case also asserts that the write still lands.
/// </summary>
public class BindingMatchServiceStampOutcomeTests
{
    private const string Uri = "file:///c:/proj/feature1.feature";

    private static readonly ProjectOwner Owner = new("C:/proj/A.csproj", "net8.0");

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

    private static FeatureBindingMatchSet SetWith(long content, long config, int registry, string uri = Uri) =>
        FeatureBindingMatchSet.FromTags(
            uri, 1, new DerivationStamp(content, config, registry), Array.Empty<IdeSupportTag>(), Owner);

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
    public void Write_built_from_text_the_document_has_since_left_is_superseded_but_still_stored()
    {
        BumpTo(4);

        _sut.Store(SetWith(3, 0, 10)).Should().Be(StoreOutcome.RejectedSuperseded);

        StoredStamp().Should().Be(new DerivationStamp(3, 0, 10), "the guard is log-only");
        _sut.GetStoreStats().Should().Be(new StoreStats(0, 1, 0));
    }

    [Fact]
    public void Write_for_a_generation_the_document_has_not_reached_is_also_superseded()
    {
        BumpTo(2);

        _sut.Store(SetWith(3, 0, 10)).Should().Be(StoreOutcome.RejectedSuperseded);
    }

    [Fact]
    public void Older_registry_for_the_same_text_arriving_after_a_newer_one_is_stale_but_still_stored()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 0, 12)).Should().Be(StoreOutcome.Accepted);

        _sut.Store(SetWith(3, 0, 11)).Should().Be(StoreOutcome.RejectedStale);

        StoredStamp().Should().Be(new DerivationStamp(3, 0, 11), "the guard is log-only");
        _sut.GetStoreStats().Should().Be(new StoreStats(1, 0, 1));
    }

    [Fact]
    public void Older_configuration_for_the_same_text_and_registry_is_stale()
    {
        BumpTo(3);
        _sut.Store(SetWith(3, 2, 10));

        _sut.Store(SetWith(3, 1, 10)).Should().Be(StoreOutcome.RejectedStale);
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
            .And.Contain("currentGeneration=4").And.Contain("<none>");
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
