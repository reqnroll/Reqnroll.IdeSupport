using Microsoft.VisualStudio.Editor;

namespace Reqnroll.VisualStudio.Tests;

/// <summary>
/// Coverage for <see cref="FormatDocumentCommandFilter.GetTextBufferFileUri"/> — the one member of
/// this filter that never touches <c>ThreadHelper</c>/COM directly, so — unlike <c>Exec</c>/
/// <c>QueryStatus</c>, which require a real VS UI thread — it can be exercised here with plain
/// NSubstitute fakes. Mirrors <see cref="CommentToggleCommandFilterTests"/>, which covers the
/// identical helper on the sibling Comment/Uncomment filter.
/// </summary>
public class FormatDocumentCommandFilterTests
{
    private static FormatDocumentCommandFilter CreateSut() =>
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

    // ── IsSnapshotStale (issue #766) ──────────────────────────────────────
    //
    // Format Document applies its edits onto whatever the buffer's current snapshot happens to
    // be when the LSP round trip completes. If the user typed (or pressed Format again) while the
    // request was in flight, that snapshot has moved on from the one the edits were computed
    // against, and applying them would land on the wrong lines. IsSnapshotStale is the pure check
    // Exec uses to detect that and drop the edits instead.

    private static ITextSnapshot CreateSnapshot(int versionNumber)
    {
        var version = Substitute.For<ITextVersion>();
        version.VersionNumber.Returns(versionNumber);
        var snapshot = Substitute.For<ITextSnapshot>();
        snapshot.Version.Returns(version);
        return snapshot;
    }

    [Fact]
    public void Is_not_stale_when_the_current_snapshot_matches_the_request_snapshot()
    {
        var requestSnapshot = CreateSnapshot(1);

        FormatDocumentCommandFilter.IsSnapshotStale(requestSnapshot, requestSnapshot).Should().BeFalse();
    }

    [Fact]
    public void Is_stale_when_the_buffer_changed_during_the_round_trip()
    {
        var requestSnapshot = CreateSnapshot(1);
        var currentSnapshot = CreateSnapshot(2);

        FormatDocumentCommandFilter.IsSnapshotStale(requestSnapshot, currentSnapshot).Should().BeTrue();
    }
}
