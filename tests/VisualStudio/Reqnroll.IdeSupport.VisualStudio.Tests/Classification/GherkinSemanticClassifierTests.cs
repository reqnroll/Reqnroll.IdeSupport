using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.VisualStudio.Extension.Classification;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.Classification;

/// <summary>
/// Lifetime coverage for <see cref="GherkinSemanticClassifier"/> — the classifier subscribes to the
/// process-wide <see cref="SemanticTokenClassificationStore"/>, and that subscription must not keep the classifier
/// (and through it the editor's <see cref="ITextBuffer"/>) alive after the editor has released it.
/// Each test uses its own store instance, so no shared static state is touched.
/// </summary>
public class GherkinSemanticClassifierTests
{
    private const string FilePath = @"c:\w\A.feature";

    private static string FileKey => SemanticTokenClassificationStore.NormalizeKey(FilePath)!;

    [Fact]
    public void A_classifier_the_editor_has_released_is_collectable_while_the_token_store_is_still_alive()
    {
        var store = new SemanticTokenClassificationStore();

        var classifierRef = CreateAndReleaseClassifier(store);
        ForceFullGc();

        classifierRef.IsAlive.Should().BeFalse(
            "the long-lived token store must not root a classifier (and its text buffer) the editor has released");
        GC.KeepAlive(store);
    }

    [Fact]
    public void A_collected_classifier_subscription_is_pruned_the_next_time_tokens_change()
    {
        var store = new SemanticTokenClassificationStore();

        var classifierRef = CreateAndReleaseClassifier(store);
        ForceFullGc();
        classifierRef.IsAlive.Should().BeFalse();

        var setTokens = () => store.SetTokens(FileKey, new List<ClassifiedToken> { new(0, 0, 7, "reqnroll.keyword") });

        setTokens.Should().NotThrow("raising the event for a collected classifier must be a harmless no-op");
        TokensChangedSubscriberCount(store).Should().Be(0,
            "the subscription of a collected classifier should be removed so the store does not accumulate dead entries");
    }

    [Fact]
    public void A_live_classifier_still_raises_ClassificationChanged_when_tokens_for_its_file_change()
    {
        var store = new SemanticTokenClassificationStore();
        var classifier = CreateClassifier(store);
        var raised = 0;
        classifier.ClassificationChanged += (_, _) => raised++;

        store.SetTokens(FileKey, new List<ClassifiedToken> { new(0, 0, 7, "reqnroll.keyword") });
        store.SetTokens(SemanticTokenClassificationStore.NormalizeKey(@"c:\w\Other.feature")!, new List<ClassifiedToken>());

        raised.Should().Be(1, "only a token change for the classifier's own file should trigger a recolour");
        TokensChangedSubscriberCount(store).Should().Be(1, "a live classifier must stay subscribed");
        GC.KeepAlive(classifier);
    }

    // ── ChangedSpan ──────────────────────────────────────────────────────────

    private static ITextSnapshot SnapshotWithLines(int length, params (int Start, int End)[] lines)
    {
        var snapshot = Substitute.For<ITextSnapshot>();
        snapshot.Length.Returns(length);
        snapshot.LineCount.Returns(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            // Built before configuring the substitutes: constructing a SnapshotPoint reads the snapshot's
            // Length, which must not happen inside a Returns(...) argument.
            var start = new SnapshotPoint(snapshot, lines[i].Start);
            var end = new SnapshotPoint(snapshot, lines[i].End);
            var line = Substitute.For<ITextSnapshotLine>();
            line.Start.Returns(start);
            line.End.Returns(end);
            snapshot.GetLineFromLineNumber(i).Returns(line);
        }

        return snapshot;
    }

    [Fact]
    public void ChangedSpan_covers_exactly_the_changed_lines()
    {
        var snapshot = SnapshotWithLines(100, (0, 9), (10, 19), (20, 39), (40, 55), (56, 99));

        var span = GherkinSemanticClassifier.ChangedSpan(snapshot, startLine: 1, endLine: 3);

        span.Start.Position.Should().Be(10);
        span.End.Position.Should().Be(55);
    }

    [Fact]
    public void ChangedSpan_for_a_single_line_covers_just_that_line()
    {
        var snapshot = SnapshotWithLines(100, (0, 9), (10, 19), (20, 39));

        var span = GherkinSemanticClassifier.ChangedSpan(snapshot, startLine: 2, endLine: 2);

        span.Start.Position.Should().Be(20);
        span.End.Position.Should().Be(39);
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(-1, 3)]
    [InlineData(3, 1)]
    public void ChangedSpan_covers_the_whole_snapshot_when_no_usable_line_range_is_given(int startLine, int endLine)
    {
        var snapshot = SnapshotWithLines(100, (0, 9), (10, 19), (20, 39), (40, 55));

        var span = GherkinSemanticClassifier.ChangedSpan(snapshot, startLine, endLine);

        span.Start.Position.Should().Be(0);
        span.Length.Should().Be(100);
    }

    [Fact]
    public void ChangedSpan_clamps_lines_beyond_the_end_of_the_snapshot_to_its_last_line()
    {
        // The snapshot can be shorter than the line numbers in a push when the user has deleted lines since.
        var snapshot = SnapshotWithLines(40, (0, 9), (10, 39));

        var span = GherkinSemanticClassifier.ChangedSpan(snapshot, startLine: 1, endLine: 9);

        span.Start.Position.Should().Be(10);
        span.End.Position.Should().Be(39);
    }

    [Fact]
    public void ChangedSpan_of_an_empty_snapshot_is_empty()
    {
        var snapshot = SnapshotWithLines(0);

        var span = GherkinSemanticClassifier.ChangedSpan(snapshot, startLine: 0, endLine: 2);

        span.Length.Should().Be(0);
    }

    // ── Moving tokens through edits ──────────────────────────────────────────

    // Lines given as (start, end) positions; also answers the position-to-line lookups the conversion needs.
    private static ITextSnapshot SnapshotWithLinesByPosition(int length, params (int Start, int End)[] lines)
    {
        var snapshot = SnapshotWithLines(length, lines);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = snapshot.GetLineFromLineNumber(i);
            line.LineNumber.Returns(i);
            for (var position = lines[i].Start; position <= Math.Min(length, lines[i].End + 1); position++)
                snapshot.GetLineFromPosition(position).Returns(line);
        }

        return snapshot;
    }

    private static ITextChange TextChange(int oldPosition, int oldEnd, int newEnd)
    {
        var change = Substitute.For<ITextChange>();
        change.OldPosition.Returns(oldPosition);
        change.OldEnd.Returns(oldEnd);
        change.NewEnd.Returns(newEnd);
        return change;
    }

    [Fact]
    public void ToTokenTextChange_describes_an_edit_in_line_and_character_coordinates()
    {
        // Before: "abcde\nfghij\nklmno" (lines at 0-5, 6-11, 12-17); after a line break was typed at position 8.
        var before = SnapshotWithLinesByPosition(17, (0, 5), (6, 11), (12, 17));
        var after = SnapshotWithLinesByPosition(19, (0, 5), (6, 8), (9, 12), (13, 18));
        var change = TextChange(oldPosition: 8, oldEnd: 8, newEnd: 10);

        var result = GherkinSemanticClassifier.ToTokenTextChange(change, before, after);

        result.OldStartLine.Should().Be(1);
        result.OldStartChar.Should().Be(2);
        result.OldEndLine.Should().Be(1);
        result.OldEndChar.Should().Be(2);
        result.NewEndLine.Should().Be(2);
        result.NewEndChar.Should().Be(1);
        result.LineCountDelta.Should().Be(1);
    }

    [Fact]
    public void An_edit_of_the_buffer_moves_the_cached_tokens_and_recolours_from_the_edited_line_on()
    {
        var store = new SemanticTokenClassificationStore();
        var snapshot = SnapshotWithLinesByPosition(19, (0, 5), (6, 8), (9, 12), (13, 18));
        var before = SnapshotWithLinesByPosition(17, (0, 5), (6, 11), (12, 17));
        var buffer = Substitute.For<ITextBuffer>();
        buffer.Properties.Returns(new PropertyCollection());
        buffer.CurrentSnapshot.Returns(snapshot);
        var document = Substitute.For<ITextDocument>();
        document.FilePath.Returns(FilePath);
        var classifier = new GherkinSemanticClassifier(
            buffer, Substitute.For<IClassificationTypeRegistryService>(), new FakeTextDocumentFactory(document), store);
        store.SetTokens(FileKey, new List<ClassifiedToken> { new(0, 0, 5, "reqnroll.keyword"), new(2, 0, 5, "reqnroll.keyword") });
        ClassificationChangedEventArgs? raised = null;
        classifier.ClassificationChanged += (_, args) => raised = args;

        buffer.ChangedHighPriority += Raise.EventWith(buffer, ChangedArgs(before, snapshot, TextChange(8, 8, 10)));

        store.TryGetTokens(FileKey, out var tokens).Should().BeTrue();
        tokens.Select(t => (t.Line, t.StartChar)).Should().Equal((0, 0), (3, 0));
        raised.Should().NotBeNull("the lines below an added line were recoloured");
        raised!.ChangeSpan.Start.Position.Should().Be(6, "from the line the edit started on");
        GC.KeepAlive(classifier);
    }

    // TextContentChangedEventArgs reads its changes from the version of the snapshot before the edit.
    private static TextContentChangedEventArgs ChangedArgs(ITextSnapshot before, ITextSnapshot after, params ITextChange[] changes)
    {
        var version = Substitute.For<ITextVersion>();
        version.Changes.Returns(new FakeChanges(changes));
        before.Version.Returns(version);
        return new TextContentChangedEventArgs(before, after, EditOptions.None, null);
    }

    private sealed class FakeChanges : List<ITextChange>, INormalizedTextChangeCollection
    {
        public FakeChanges(IEnumerable<ITextChange> changes) : base(changes)
        {
        }

        public bool IncludesLineChanges => true;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Not inlined so that no local in the calling test frame can keep the classifier reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndReleaseClassifier(SemanticTokenClassificationStore store) =>
        new WeakReference(CreateClassifier(store));

    private static GherkinSemanticClassifier CreateClassifier(SemanticTokenClassificationStore store)
    {
        var snapshot = Substitute.For<ITextSnapshot>();
        snapshot.Length.Returns(0);
        var buffer = Substitute.For<ITextBuffer>();
        buffer.Properties.Returns(new PropertyCollection());
        buffer.CurrentSnapshot.Returns(snapshot);
        var document = Substitute.For<ITextDocument>();
        document.FilePath.Returns(FilePath);

        return new GherkinSemanticClassifier(
            buffer, Substitute.For<IClassificationTypeRegistryService>(), new FakeTextDocumentFactory(document), store);
    }

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static int TokensChangedSubscriberCount(SemanticTokenClassificationStore store)
    {
        var field = typeof(SemanticTokenClassificationStore).GetField(
            nameof(SemanticTokenClassificationStore.TokensChanged), BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (Delegate?)field!.GetValue(store);
        return handler?.GetInvocationList().Length ?? 0;
    }

    /// <summary>
    /// A hand-written fake rather than a substitute: a substitute records the buffer argument of every call, and
    /// the test must be sure nothing but the code under test can keep the buffer or classifier reachable.
    /// </summary>
    private sealed class FakeTextDocumentFactory : ITextDocumentFactoryService
    {
        private readonly ITextDocument _document;

        public FakeTextDocumentFactory(ITextDocument document) => _document = document;

        public event EventHandler<Microsoft.VisualStudio.Text.TextDocumentEventArgs>? TextDocumentCreated;
        public event EventHandler<Microsoft.VisualStudio.Text.TextDocumentEventArgs>? TextDocumentDisposed;

        public bool TryGetTextDocument(ITextBuffer textBuffer, out ITextDocument textDocument)
        {
            textDocument = _document;
            return true;
        }

        public ITextDocument CreateAndLoadTextDocument(string filePath, IContentType contentType) => throw new NotSupportedException();
        public ITextDocument CreateAndLoadTextDocument(string filePath, IContentType contentType, System.Text.Encoding encoding, out bool characterSubstitutionsOccurred) => throw new NotSupportedException();
        public ITextDocument CreateAndLoadTextDocument(string filePath, IContentType contentType, bool attemptUtf8Detection, out bool characterSubstitutionsOccurred) => throw new NotSupportedException();
        public ITextDocument CreateTextDocument(ITextBuffer textBuffer, string filePath) => throw new NotSupportedException();
    }
}
