using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text.Operations;
using Reqnroll.IdeSupport.VisualStudio.GoToDefinition;

namespace Reqnroll.VisualStudio.Tests;

/// <summary>
/// Coverage for <see cref="GoToDefinitionNavigableSymbolProvider"/> (issue #761): Ctrl+Click routing
/// through <see cref="GoToDefinitionRedirect"/>. Mirrors <see cref="GoToDefinitionCommandFilterTests"/>
/// — <c>GetTextBufferFileUri</c> and the "no redirect set" fallback need no real VS UI thread, so they
/// are exercised here with plain NSubstitute fakes.
/// </summary>
public class GoToDefinitionNavigableSymbolProviderTests : IDisposable
{
    // GoToDefinitionRedirect.GoToDefinitionAsync is a process-wide static: reset it on both sides of
    // every test so a value one test sets can't leak into another (this class is the only one that
    // touches it today, but xUnit may run test classes in parallel, so don't rely on ordering).
    public GoToDefinitionNavigableSymbolProviderTests() => GoToDefinitionRedirect.GoToDefinitionAsync = null;

    public void Dispose() => GoToDefinitionRedirect.GoToDefinitionAsync = null;

    private static GoToDefinitionNavigableSymbolProvider.NavigableSymbolSource CreateSut(ITextView? textView = null) =>
        new(textView ?? Substitute.For<ITextView>(),
            Substitute.For<ITextStructureNavigatorSelectorService>(),
            Substitute.For<IIdeSupportLogger>());

    private static ITextView CreateTextView(PropertyCollection properties)
    {
        var textBuffer = Substitute.For<ITextBuffer>();
        textBuffer.Properties.Returns(properties);
        var textView = Substitute.For<ITextView>();
        textView.TextBuffer.Returns(textBuffer);
        return textView;
    }

    // ── GetTextBufferFileUri ─────────────────────────────────────────────

    [Fact]
    public void Returns_the_absolute_file_uri_when_an_ITextDocument_is_present()
    {
        var document = Substitute.For<ITextDocument>();
        document.FilePath.Returns(@"C:\repo\Feature1.feature");
        var properties = new PropertyCollection();
        properties.AddProperty(typeof(ITextDocument), document);

        var uri = CreateSut().GetTextBufferFileUri(CreateTextView(properties).TextBuffer);

        uri.Should().Be(new Uri(@"C:\repo\Feature1.feature").AbsoluteUri);
    }

    [Fact]
    public void Returns_empty_string_when_no_ITextDocument_property_is_present()
    {
        var uri = CreateSut().GetTextBufferFileUri(CreateTextView(new PropertyCollection()).TextBuffer);

        uri.Should().Be(string.Empty);
    }

    // ── GetNavigableSymbolAsync ───────────────────────────────────────────

    [Fact]
    public async Task Returns_null_when_no_redirect_is_set_so_VS_falls_through_to_its_own_provider()
    {
        var properties = new PropertyCollection();
        properties.AddProperty(typeof(ITextDocument), Substitute.For<ITextDocument>());
        var sut = CreateSut(CreateTextView(properties));

        // The redirect-null check happens before triggerSpan is touched, so a default span is fine.
        var symbol = await sut.GetNavigableSymbolAsync(default, CancellationToken.None);

        symbol.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_the_text_buffer_has_no_file_uri_even_with_a_redirect_set()
    {
        GoToDefinitionRedirect.GoToDefinitionAsync = (_, _, _, _, _) => Task.CompletedTask;

        var sut = CreateSut(CreateTextView(new PropertyCollection()));

        var symbol = await sut.GetNavigableSymbolAsync(default, CancellationToken.None);

        symbol.Should().BeNull();
    }
}
