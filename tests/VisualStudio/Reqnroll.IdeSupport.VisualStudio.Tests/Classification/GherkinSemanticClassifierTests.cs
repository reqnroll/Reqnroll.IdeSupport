using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.VisualStudio.Extension.Classification;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.Classification;

/// <summary>
/// Lifetime coverage for <see cref="GherkinSemanticClassifier"/> (issue #1018): the classifier subscribes to the
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
