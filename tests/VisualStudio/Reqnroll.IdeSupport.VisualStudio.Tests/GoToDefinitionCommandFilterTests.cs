using Microsoft.VisualStudio.Editor;

namespace Reqnroll.VisualStudio.Tests;

/// <summary>
/// Coverage for <see cref="GoToDefinitionCommandFilter.GetTextBufferFileUri"/> — the one member of
/// this filter that never touches <c>ThreadHelper</c>/COM directly, so — unlike <c>Exec</c>/
/// <c>QueryStatus</c>, which require a real VS UI thread — it can be exercised here with plain
/// NSubstitute fakes. Mirrors <see cref="FormatDocumentCommandFilterTests"/>.
/// </summary>
public class GoToDefinitionCommandFilterTests
{
    private static GoToDefinitionCommandFilter CreateSut() =>
        new(Substitute.For<IVsTextView>(), Substitute.For<IVsEditorAdaptersFactoryService>(),
            Substitute.For<IIdeSupportLogger>());

    private static IWpfTextView CreateWpfTextView(PropertyCollection properties)
    {
        var textBuffer = Substitute.For<ITextBuffer>();
        textBuffer.Properties.Returns(properties);
        var wpfTextView = Substitute.For<IWpfTextView>();
        wpfTextView.TextBuffer.Returns(textBuffer);
        return wpfTextView;
    }

    [Fact]
    public void Returns_the_absolute_file_uri_when_an_ITextDocument_is_present()
    {
        var document = Substitute.For<ITextDocument>();
        document.FilePath.Returns(@"C:\repo\Feature1.feature");
        var properties = new PropertyCollection();
        properties.AddProperty(typeof(ITextDocument), document);

        var uri = CreateSut().GetTextBufferFileUri(CreateWpfTextView(properties));

        uri.Should().Be(new Uri(@"C:\repo\Feature1.feature").AbsoluteUri);
    }

    [Fact]
    public void Returns_empty_string_when_no_ITextDocument_property_is_present()
    {
        var uri = CreateSut().GetTextBufferFileUri(CreateWpfTextView(new PropertyCollection()));

        uri.Should().Be(string.Empty);
    }
}
