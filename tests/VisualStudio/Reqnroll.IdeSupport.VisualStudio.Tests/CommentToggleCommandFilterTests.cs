using Microsoft.VisualStudio.Editor;

namespace Reqnroll.VisualStudio.Tests;

/// <summary>
/// Coverage for issue #565: <see cref="CommentToggleCommandFilter.GetTextBufferFileUri"/> is the
/// one member of this filter that never touches <c>ThreadHelper</c>/COM directly, so — unlike
/// <c>Exec</c>/<c>QueryStatus</c>, which require a real VS UI thread — it can be exercised here
/// with plain NSubstitute fakes. The constructor and this method were widened from private to
/// internal specifically to make this reachable (see the source file for the InternalsVisibleTo note).
/// </summary>
public class CommentToggleCommandFilterTests
{
    private static CommentToggleCommandFilter CreateSut() =>
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

    [Fact]
    public void Returns_empty_string_and_does_not_throw_when_the_file_path_is_not_a_valid_uri()
    {
        var document = Substitute.For<ITextDocument>();
        document.FilePath.Returns(string.Empty);
        var properties = new PropertyCollection();
        properties.AddProperty(typeof(ITextDocument), document);

        var uri = CreateSut().GetTextBufferFileUri(CreateWpfTextView(properties));

        uri.Should().Be(string.Empty);
    }
}
