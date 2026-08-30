using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LineCodeLens;

/// <summary>
/// Unit tests for <see cref="WeakTaggerRegistry{TTagger}"/> — the per-file, weakly-held tagger
/// tracking shared by every Reqnroll classic CodeLens feature (issue #372/#262 follow-up
/// extraction). <c>TTagger</c> is constrained only to <see langword="class"/>, so a plain fake
/// stands in for the real <c>LineKeyedCodeLensTagger{TEntry}</c> — the registry itself has no
/// dependency on VS editor types.
/// </summary>
public class WeakTaggerRegistryTests
{
    private sealed class FakeTagger
    {
    }

    private static WeakTaggerRegistry<FakeTagger> CreateSut(out List<FakeTagger> refreshed)
    {
        var captured = new List<FakeTagger>();
        refreshed = captured;
        return new WeakTaggerRegistry<FakeTagger>(t => captured.Add(t));
    }

    // ── InvalidateFile ───────────────────────────────────────────────────────────

    [Fact]
    public void InvalidateFile_requests_a_refresh_for_a_registered_tagger()
    {
        var sut = CreateSut(out var refreshed);
        var tagger = new FakeTagger();
        sut.RegisterTagger(tagger, "file:///a.feature");

        sut.InvalidateFile("file:///a.feature");

        refreshed.Should().ContainSingle().Which.Should().BeSameAs(tagger);
    }

    [Fact]
    public void InvalidateFile_requests_a_refresh_for_every_tagger_registered_to_that_file()
    {
        var sut = CreateSut(out var refreshed);
        var first = new FakeTagger();
        var second = new FakeTagger();
        sut.RegisterTagger(first, "file:///a.feature");
        sut.RegisterTagger(second, "file:///a.feature");

        sut.InvalidateFile("file:///a.feature");

        refreshed.Should().BeEquivalentTo(new[] { first, second });
    }

    [Fact]
    public void InvalidateFile_does_not_affect_taggers_registered_to_a_different_file()
    {
        var sut = CreateSut(out var refreshed);
        var otherFileTagger = new FakeTagger();
        sut.RegisterTagger(otherFileTagger, "file:///b.feature");

        sut.InvalidateFile("file:///a.feature");

        refreshed.Should().BeEmpty();
    }

    [Fact]
    public void InvalidateFile_for_an_untracked_file_does_nothing_and_does_not_throw()
    {
        var sut = CreateSut(out var refreshed);

        var act = () => sut.InvalidateFile("file:///never-registered.feature");

        act.Should().NotThrow();
        refreshed.Should().BeEmpty();
    }

    [Fact]
    public void InvalidateFile_treats_the_file_key_as_case_insensitive()
    {
        // The registry's dictionary is built with StringComparer.OrdinalIgnoreCase — file URIs can
        // arrive with different casing (e.g. a drive letter) from different call sites.
        var sut = CreateSut(out var refreshed);
        var tagger = new FakeTagger();
        sut.RegisterTagger(tagger, "file:///C:/Project/a.feature");

        sut.InvalidateFile("file:///c:/project/a.feature");

        refreshed.Should().ContainSingle().Which.Should().BeSameAs(tagger);
    }

    // ── InvalidateAll ────────────────────────────────────────────────────────────

    [Fact]
    public void InvalidateAll_requests_a_refresh_for_every_tracked_tagger_across_every_file()
    {
        var sut = CreateSut(out var refreshed);
        var a = new FakeTagger();
        var b = new FakeTagger();
        sut.RegisterTagger(a, "file:///a.feature");
        sut.RegisterTagger(b, "file:///b.feature");

        sut.InvalidateAll();

        refreshed.Should().BeEquivalentTo(new[] { a, b });
    }

    [Fact]
    public void InvalidateAll_on_an_empty_registry_does_nothing_and_does_not_throw()
    {
        var sut = CreateSut(out var refreshed);

        var act = () => sut.InvalidateAll();

        act.Should().NotThrow();
        refreshed.Should().BeEmpty();
    }

    // ── UnregisterTagger ─────────────────────────────────────────────────────────

    [Fact]
    public void UnregisterTagger_stops_it_from_receiving_further_refresh_requests()
    {
        var sut = CreateSut(out var refreshed);
        var tagger = new FakeTagger();
        sut.RegisterTagger(tagger, "file:///a.feature");

        sut.UnregisterTagger(tagger, "file:///a.feature");
        sut.InvalidateFile("file:///a.feature");

        refreshed.Should().BeEmpty();
    }

    [Fact]
    public void UnregisterTagger_only_removes_the_specified_tagger_not_its_file_siblings()
    {
        var sut = CreateSut(out var refreshed);
        var kept = new FakeTagger();
        var removed = new FakeTagger();
        sut.RegisterTagger(kept, "file:///a.feature");
        sut.RegisterTagger(removed, "file:///a.feature");

        sut.UnregisterTagger(removed, "file:///a.feature");
        sut.InvalidateFile("file:///a.feature");

        refreshed.Should().ContainSingle().Which.Should().BeSameAs(kept);
    }

    [Fact]
    public void UnregisterTagger_for_an_untracked_file_does_nothing_and_does_not_throw()
    {
        var sut = CreateSut(out _);
        var tagger = new FakeTagger();

        var act = () => sut.UnregisterTagger(tagger, "file:///never-registered.feature");

        act.Should().NotThrow();
    }

    // ── Weak reference behavior ──────────────────────────────────────────────────

    [Fact]
    public void A_collected_tagger_is_dropped_and_no_longer_receives_refresh_requests()
    {
        var sut = CreateSut(out var refreshed);
        RegisterATaggerThatIsThenAbandoned(sut, "file:///a.feature");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        sut.InvalidateFile("file:///a.feature");

        refreshed.Should().BeEmpty();
    }

    /// <summary>
    /// Registers a tagger without letting the caller hold a strong reference to it, so it's
    /// actually eligible for collection once this method returns — a local variable in the calling
    /// test method could otherwise be kept alive by the JIT for the rest of the frame.
    /// </summary>
    private static void RegisterATaggerThatIsThenAbandoned(WeakTaggerRegistry<FakeTagger> registry, string fileUri) =>
        registry.RegisterTagger(new FakeTagger(), fileUri);
}
