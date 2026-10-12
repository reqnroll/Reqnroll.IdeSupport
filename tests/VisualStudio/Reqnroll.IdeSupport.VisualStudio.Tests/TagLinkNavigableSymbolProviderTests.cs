using System.Reflection;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.VisualStudio.DocumentLinks;
using Reqnroll.IdeSupport.VisualStudio.GoToDefinition;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.Tests;

/// <summary>
/// Coverage for <see cref="TagLinkNavigableSymbolProvider"/> (issue #755). The editor takes the first non-null
/// symbol in MEF order, so the ordering metadata is what makes a tag win over Go To Definition; it is pinned
/// here because nothing else would notice it drifting. The source-level tests need no real VS UI thread, so
/// they use plain NSubstitute fakes, like <see cref="GoToDefinitionNavigableSymbolProviderTests"/>.
/// </summary>
[Collection("TagLinkRedirect static state")]
public class TagLinkNavigableSymbolProviderTests : IDisposable
{
    // TagLinkRedirect.GetLinksAsync is a process-wide static: reset it on both sides of every test.
    public TagLinkNavigableSymbolProviderTests() => TagLinkRedirect.GetLinksAsync = null;

    public void Dispose() => TagLinkRedirect.GetLinksAsync = null;

    private static TagLinkNavigableSymbolProvider.NavigableSymbolSource CreateSut(ITextView? textView = null) =>
        new(textView ?? Substitute.For<ITextView>(), Substitute.For<IIdeSupportLogger>());

    private static ITextView CreateTextViewWithDocument()
    {
        var document = Substitute.For<ITextDocument>();
        document.FilePath.Returns(@"C:\repo\Feature1.feature");
        var properties = new PropertyCollection();
        properties.AddProperty(typeof(ITextDocument), document);

        var textBuffer = Substitute.For<ITextBuffer>();
        textBuffer.Properties.Returns(properties);
        var textView = Substitute.For<ITextView>();
        textView.TextBuffer.Returns(textBuffer);
        return textView;
    }

    // The test text is three lines: "Feature: F" (0), "@issue:1234 @smoke" (11), "Scenario: S" (30).
    private static readonly int[] LineStarts = { 0, 11, 30 };

    private static ITextSnapshot CreateSnapshot()
    {
        var snapshot = Substitute.For<ITextSnapshot>();
        snapshot.Length.Returns(42);
        snapshot.LineCount.Returns(LineStarts.Length);
        // Built up front: SnapshotPoint's constructor reads snapshot.Length, and calling the snapshot
        // between `line.Start` and `.Returns(...)` would be taken as the call being configured.
        var lines = LineStarts.Select((start, number) =>
        {
            var startPoint = new SnapshotPoint(snapshot, start);
            var line = Substitute.For<ITextSnapshotLine>();
            line.LineNumber.Returns(number);
            line.Start.Returns(startPoint);
            return line;
        }).ToArray();
        snapshot.GetLineFromLineNumber(Arg.Any<int>()).Returns(ci => lines[ci.Arg<int>()]);
        // GetContainingLine() resolves the line through GetLineFromPosition.
        snapshot.GetLineFromPosition(Arg.Any<int>()).Returns(ci =>
        {
            var position = ci.Arg<int>();
            var index = 0;
            for (var i = 0; i < LineStarts.Length; i++)
            {
                if (LineStarts[i] <= position)
                    index = i;
            }
            return lines[index];
        });
        return snapshot;
    }

    // A zero-width trigger span on the tag at line 1 (character 11), used to drive the link lookup.
    private static SnapshotSpan TriggerOnLine1(ITextSnapshot snapshot) =>
        new(new SnapshotPoint(snapshot, 11), new SnapshotPoint(snapshot, 11));

    // ── MEF metadata ─────────────────────────────────────────────────────

    [Fact]
    public void Is_ordered_before_the_Go_To_Definition_provider_so_a_tag_link_wins()
    {
        var goToDefinitionName = typeof(GoToDefinitionNavigableSymbolProvider).GetCustomAttribute<NameAttribute>()!.Name;
        var order = typeof(TagLinkNavigableSymbolProvider).GetCustomAttribute<OrderAttribute>()!;

        order.Before.Should().Be(goToDefinitionName);
    }

    [Fact]
    public void Is_exported_for_the_Gherkin_content_type_under_its_own_name()
    {
        var type = typeof(TagLinkNavigableSymbolProvider);

        type.GetCustomAttribute<ContentTypeAttribute>()!.ContentTypes.Should().Be(VsWellKnownIds.GherkinContentType);
        type.GetCustomAttribute<NameAttribute>()!.Name.Should()
            .NotBe(typeof(GoToDefinitionNavigableSymbolProvider).GetCustomAttribute<NameAttribute>()!.Name);
    }

    // ── GetNavigableSymbolAsync ──────────────────────────────────────────

    [Fact]
    public async Task Returns_null_when_no_link_bridge_is_set_so_Go_To_Definition_takes_over()
    {
        var symbol = await CreateSut(CreateTextViewWithDocument()).GetNavigableSymbolAsync(default, CancellationToken.None);

        symbol.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_without_asking_for_links_when_the_text_buffer_has_no_file_uri()
    {
        var asked = false;
        TagLinkRedirect.GetLinksAsync = (_, _) =>
        {
            asked = true;
            return Task.FromResult<IReadOnlyList<TagLinkEntry>>(Array.Empty<TagLinkEntry>());
        };
        var textView = Substitute.For<ITextView>();
        var textBuffer = Substitute.For<ITextBuffer>();
        textBuffer.Properties.Returns(new PropertyCollection());
        textView.TextBuffer.Returns(textBuffer);

        var symbol = await CreateSut(textView).GetNavigableSymbolAsync(default, CancellationToken.None);

        symbol.Should().BeNull();
        asked.Should().BeFalse();
    }

    [Fact]
    public async Task Asks_for_links_without_depending_on_the_Go_To_Definition_bridge()
    {
        string? askedFor = null;
        TagLinkRedirect.GetLinksAsync = (uri, _) =>
        {
            askedFor = uri;
            throw new InvalidOperationException("server gone");   // a failure degrades to "no link"
        };

        var symbol = await CreateSut(CreateTextViewWithDocument()).GetNavigableSymbolAsync(default, CancellationToken.None);

        symbol.Should().BeNull();
        askedFor.Should().Be(new Uri(@"C:\repo\Feature1.feature").AbsoluteUri);
    }

    [Fact]
    public async Task Returns_null_for_a_non_openable_target_so_the_ctrl_hover_symbol_is_never_offered_issue_1036()
    {
        // The server emits target.AbsoluteUri for any configured pattern, so a misconfigured non-http(s)
        // target reaches the provider; offering a symbol for it would underline the tag and win over
        // Go To Definition, only to have Navigate refuse to open it.
        var snapshot = CreateSnapshot();
        TagLinkRedirect.GetLinksAsync = (_, _) => Task.FromResult<IReadOnlyList<TagLinkEntry>>(
            new[] { new TagLinkEntry(1, 0, 1, 11, "file:///C:/Windows/System32/calc.exe") });

        var symbol = await CreateSut(CreateTextViewWithDocument())
            .GetNavigableSymbolAsync(TriggerOnLine1(snapshot), CancellationToken.None);

        symbol.Should().BeNull();
        TagLinkRedirect.IsOpenableUrl("file:///C:/Windows/System32/calc.exe").Should().BeFalse();
    }

    [Fact]
    public async Task Returns_a_symbol_for_an_http_target_issue_1036()
    {
        var snapshot = CreateSnapshot();
        TagLinkRedirect.GetLinksAsync = (_, _) => Task.FromResult<IReadOnlyList<TagLinkEntry>>(
            new[] { new TagLinkEntry(1, 0, 1, 11, "https://example.com/issues/1036") });

        var symbol = await CreateSut(CreateTextViewWithDocument())
            .GetNavigableSymbolAsync(TriggerOnLine1(snapshot), CancellationToken.None);

        symbol.Should().NotBeNull();
        symbol!.SymbolSpan.Start.Position.Should().Be(11);
        symbol.SymbolSpan.Length.Should().Be(11);
    }

    // ── FindLink ─────────────────────────────────────────────────────────

    [Fact]
    public void FindLink_returns_the_link_covering_the_position_and_null_elsewhere()
    {
        var links = new[]
        {
            new TagLinkEntry(1, 0, 1, 11, "https://example.com/1"),
            new TagLinkEntry(4, 2, 4, 9, "https://example.com/2"),
        };

        TagLinkNavigableSymbolProvider.NavigableSymbolSource.FindLink(links, 4, 5)!.Value.Target
            .Should().Be("https://example.com/2");
        TagLinkNavigableSymbolProvider.NavigableSymbolSource.FindLink(links, 4, 9).Should().BeNull();
        TagLinkNavigableSymbolProvider.NavigableSymbolSource.FindLink(links, 2, 0).Should().BeNull();
    }
}
