using System.Reflection;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.VisualStudio.DocumentLinks;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.DocumentLinks;

/// <summary>
/// Coverage for the permanent link styling of clickable tags (issue #921): the pure position mapping of
/// <see cref="TagLinkTracker"/>, and the MEF metadata that makes the classifier and the hover source apply to
/// Gherkin buffers only. The live behaviour (underline in the editor, the hover tooltip) needs a running Visual Studio.
/// </summary>
public class TagLinkTrackerTests
{
    // "Feature: F\n@issue:1234 @smoke\nScenario: S\n" - lines start at 0, 11 and 30; 42 characters in all.
    private static readonly int[] LineStarts = { 0, 11, 30 };
    private const int Length = 42;

    private static ITextSnapshot CreateSnapshot()
    {
        var snapshot = Substitute.For<ITextSnapshot>();
        snapshot.Length.Returns(Length);
        snapshot.LineCount.Returns(LineStarts.Length);
        // Built up front: configuring one fake from inside another fake's callback is not allowed.
        var lines = LineStarts.Select(start =>
        {
            // The point is built first: its constructor reads snapshot.Length, and a call on one fake between
            // `line.Start` and `.Returns(...)` would be taken as the call being configured.
            var startPoint = new SnapshotPoint(snapshot, start);
            var line = Substitute.For<ITextSnapshotLine>();
            line.Start.Returns(startPoint);
            return line;
        }).ToArray();
        snapshot.GetLineFromLineNumber(Arg.Any<int>()).Returns(call => lines[call.Arg<int>()]);
        return snapshot;
    }

    private static SnapshotSpan? Map(TagLinkEntry link) => TagLinkTracker.TryGetSpan(CreateSnapshot(), link);

    // ── TryGetSpan ───────────────────────────────────────────────────────────

    [Fact]
    public void TryGetSpan_maps_a_line_and_character_range_to_a_snapshot_span()
    {
        var span = Map(new TagLinkEntry(1, 0, 1, 11, "https://example.com/1234"));

        span.Should().NotBeNull();
        span!.Value.Start.Position.Should().Be(11);
        span.Value.Length.Should().Be(11);
    }

    [Fact]
    public void TryGetSpan_maps_a_range_that_does_not_start_at_the_line_start()
    {
        var span = Map(new TagLinkEntry(1, 12, 1, 18, "https://example.com/smoke"));

        span!.Value.Start.Position.Should().Be(23);
        span.Value.End.Position.Should().Be(29);
    }

    [Theory]
    [InlineData(9, 0, 9, 3)]    // a line the snapshot does not have (answer for older text)
    [InlineData(1, 5, 1, 5)]    // empty
    [InlineData(1, 8, 1, 3)]    // end before start
    [InlineData(2, 0, 2, 99)]   // past the end of the snapshot
    [InlineData(1, -1, 1, 4)]   // negative start
    [InlineData(2, 0, 1, 4)]    // end line before start line
    public void TryGetSpan_drops_a_link_that_does_not_fit_the_snapshot(int startLine, int startChar, int endLine, int endChar)
    {
        Map(new TagLinkEntry(startLine, startChar, endLine, endChar, "https://example.com/x")).Should().BeNull();
    }

    // ── MEF metadata ─────────────────────────────────────────────────────────

    [Fact]
    public void Classifier_provider_is_exported_for_the_Gherkin_content_type_only()
    {
        var type = typeof(TagLinkClassifierProvider);

        type.GetCustomAttribute<ContentTypeAttribute>()!.ContentTypes.Should().Be(VsWellKnownIds.GherkinContentType);
        type.GetInterfaces().Should().Contain(typeof(IClassifierProvider));
    }

    [Fact]
    public void Classifier_uses_the_editors_url_classification_type()
    {
        TagLinkClassifierProvider.UrlClassificationTypeName.Should().Be("url");
    }

    [Fact]
    public void Quick_info_provider_is_exported_for_the_Gherkin_content_type_only()
    {
        var type = typeof(TagLinkQuickInfoSourceProvider);

        type.GetCustomAttribute<ContentTypeAttribute>()!.ContentTypes.Should().Be(VsWellKnownIds.GherkinContentType);
        type.GetInterfaces().Should().Contain(typeof(IAsyncQuickInfoSourceProvider));
    }

    [Fact]
    public void The_providers_have_names_of_their_own()
    {
        var names = new[]
        {
            typeof(TagLinkClassifierProvider).GetCustomAttribute<NameAttribute>()!.Name,
            typeof(TagLinkQuickInfoSourceProvider).GetCustomAttribute<NameAttribute>()!.Name,
            typeof(TagLinkNavigableSymbolProvider).GetCustomAttribute<NameAttribute>()!.Name,
        };

        names.Should().OnlyHaveUniqueItems();
    }
}
