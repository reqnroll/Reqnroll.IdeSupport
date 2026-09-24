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

    // ── Command → mode mapping (issue #747) ──────────────────────────────
    //
    // The IDs are written out as literals rather than taken from VSConstants on purpose: the bug
    // was a filter matching the wrong numbers, so the test pins the numbers VS actually dispatches.
    // VSStd2K 136/137 (and legacy 98/99) = Edit.CommentSelection/UncommentSelection;
    // {160961B3-...}:48 = Edit.ToggleLineComment (from VS's editor CommandBindings, not VSConstants).

    private static readonly Guid VsStd2K = new("{1496A755-94DE-11D0-8C3F-00C04FC2AAE2}");
    private static readonly Guid EditorCommands = new("{160961B3-909D-4B28-9353-A1BEF587B4A6}");

    public static TheoryData<Guid, uint, CommentToggleMode> CommentCommands => new()
    {
        { VsStd2K, 136u, CommentToggleMode.Comment },        // COMMENT_BLOCK — Ctrl+K, Ctrl+C
        { VsStd2K, 98u,  CommentToggleMode.Comment },        // COMMENTBLOCK (legacy)
        { VsStd2K, 137u, CommentToggleMode.Uncomment },      // UNCOMMENT_BLOCK — Ctrl+K, Ctrl+U
        { VsStd2K, 99u,  CommentToggleMode.Uncomment },      // UNCOMMENTBLOCK (legacy)
        { EditorCommands, 48u, CommentToggleMode.Toggle },   // Edit.ToggleLineComment — Ctrl+/
    };

    [Theory]
    [MemberData(nameof(CommentCommands))]
    public void Maps_the_built_in_comment_commands_to_their_mode(Guid group, uint id, CommentToggleMode expected)
    {
        CommentToggleCommandFilter.TryGetCommentMode(group, id, out var mode).Should().BeTrue();
        mode.Should().Be(expected);
    }

    public static TheoryData<Guid, uint> OtherCommands => new()
    {
        { VsStd2K, 145u },          // FINAL — one of the IDs the filter wrongly claimed before #747
        { VsStd2K, 146u },          // ECMD_DECREASEFILTER — likewise
        { VsStd2K, 147u },
        { VsStd2K, 143u },          // FORMATDOCUMENT — owned by FormatDocumentCommandFilter
        { EditorCommands, 49u },    // Edit.ToggleBlockComment — Gherkin has no block comments
        { EditorCommands, 136u },   // right ID, wrong command set
        { Guid.Empty, 48u },
    };

    [Theory]
    [MemberData(nameof(OtherCommands))]
    public void Ignores_every_other_command(Guid group, uint id)
    {
        CommentToggleCommandFilter.TryGetCommentMode(group, id, out _).Should().BeFalse();
    }
}
