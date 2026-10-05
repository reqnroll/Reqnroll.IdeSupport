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
