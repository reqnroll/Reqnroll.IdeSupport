using Microsoft.VisualStudio.Language.CodeLens;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LineCodeLens;

/// <summary>
/// Issue #1028: the Run and hook-count CodeLens tagger providers hand every consumer of a buffer the
/// same <see cref="LineKeyedCodeLensTagger{TEntry}"/> (a per-buffer singleton in
/// <see cref="ITextBuffer.Properties"/>). VS's tag aggregator (<c>TagAggregator&lt;T&gt;.UnregisterTagger</c>
/// in <c>Microsoft.VisualStudio.Platform.VSEditor.dll</c>) calls <see cref="IDisposable.Dispose"/> on every
/// <see cref="IDisposable"/> tagger it obtained from a provider whenever that aggregator is disposed
/// (its view closes) or the buffer's content type changes, so one consumer going away must not stop
/// the lenses of the others.
/// </summary>
/// <remarks>
/// Each test disposes the first consumer's tagger exactly the way the aggregator does
/// (<c>if (tagger is IDisposable d) d.Dispose()</c>) and checks that the remaining consumer still
/// refreshes when the feature's registry invalidates the file. Both providers read process-wide static
/// redirects, so the class runs in one collection and restores them afterwards.
/// </remarks>
[Collection("CodeLens redirect static state")]
public class SharedCodeLensTaggerLifetimeTests
{
    private static ITextSnapshot CreateSnapshot(int lineCount)
    {
        var snapshot = Substitute.For<ITextSnapshot>();
        snapshot.LineCount.Returns(lineCount);
        snapshot.Length.Returns(lineCount * 10 + 10);
        snapshot.GetLineFromLineNumber(Arg.Any<int>()).Returns(ci =>
        {
            var lineNumber = ci.Arg<int>();
            // Built before touching `line`: SnapshotPoint's constructor reads snapshot.Length, and a call on
            // another fake between `line.Start` and `.Returns(...)` would be taken as the call being configured.
            var start = new SnapshotPoint(snapshot, lineNumber * 10);
            var line = Substitute.For<ITextSnapshotLine>();
            line.LineNumber.Returns(lineNumber);
            line.Start.Returns(start);
            return line;
        });
        return snapshot;
    }

    private static ITextBuffer CreateFeatureBuffer(ITextSnapshot snapshot, string filePath)
    {
        var buffer = Substitute.For<ITextBuffer>();
        buffer.Properties.Returns(new PropertyCollection());
        buffer.CurrentSnapshot.Returns(snapshot);
        var document = Substitute.For<ITextDocument>();
        document.FilePath.Returns(filePath);
        buffer.Properties.AddProperty(typeof(ITextDocument), document);
        return buffer;
    }

    private static NormalizedSnapshotSpanCollection WholeDocument(ITextSnapshot snapshot) =>
        new(snapshot, new Span(0, snapshot.Length));

    /// <summary>
    /// Waits until <paramref name="tagger"/> reports a single tag on <paramref name="line"/>. Polled rather
    /// than checked once: <c>CodeLensRefreshInterceptorTests</c> (another collection, run in parallel)
    /// invalidates these same process-wide registries, and a refresh it starts can be in flight when this
    /// test invalidates the file, in which case this test's request is queued behind it, not run inline.
    /// </summary>
    private static async Task<bool> WaitForTagOnLineAsync(ITagger<ICodeLensTag> tagger, ITextSnapshot snapshot, int line)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var tags = tagger.GetTags(WholeDocument(snapshot)).ToList();
            if (tags.Count == 1 && tags[0].Span.Start.Position == line * 10) // CreateSnapshot lays lines out 10 apart
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(10);
        }
    }

    /// <summary>What <c>TagAggregator&lt;T&gt;.UnregisterTagger</c> does to a tagger when its aggregator goes away.</summary>
    private static void DisposeLikeTheTagAggregator(ITagger<ICodeLensTag> tagger) => (tagger as IDisposable)?.Dispose();

    [Fact]
    public async Task Run_lens_tagger_keeps_refreshing_for_a_remaining_consumer_after_another_consumer_disposes_it()
    {
        const string filePath = @"C:\repo\issue1028-run.feature";
        var fileUri = new Uri(filePath).AbsoluteUri;
        var line = 1; // read by the fetch, which may run on another thread
        var fetchCount = 0;
        RunTestCodeLensRedirect.GetTagLocationsAsync = (_, _) =>
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult<IReadOnlyList<RunTestLensLocation>>(new[] { new RunTestLensLocation(Volatile.Read(ref line), "Scenario|S") });
        };
        ITagger<ICodeLensTag>? first = null;
        try
        {
            var snapshot = CreateSnapshot(lineCount: 5);
            var buffer = CreateFeatureBuffer(snapshot, filePath);
            var provider = new RunTestCodeLensTaggerProvider();

            first = provider.CreateTagger<ICodeLensTag>(buffer)!;   // e.g. the main editor view
            var second = provider.CreateTagger<ICodeLensTag>(buffer)!; // e.g. a split or peek view
            var tagsChanged = 0;
            second.TagsChanged += (_, _) => Interlocked.Increment(ref tagsChanged);

            DisposeLikeTheTagAggregator(first); // the first view closes; the buffer stays open in the second

            var fetchesBefore = Volatile.Read(ref fetchCount);
            Volatile.Write(ref line, 3);
            RunTestCodeLensRedirect.TaggerRegistry.InvalidateFile(fileUri);

            (await WaitForTagOnLineAsync(second, snapshot, line: 3)).Should().BeTrue("the remaining consumer's tagger must still refresh when the registry invalidates its file");
            fetchCount.Should().BeGreaterThan(fetchesBefore);
            Volatile.Read(ref tagsChanged).Should().BeGreaterThan(0, "the remaining consumer must be told its tags changed");
        }
        finally
        {
            RunTestCodeLensRedirect.GetTagLocationsAsync = null;
            // The registry is process-wide: leave no tagger behind to answer another test's invalidation.
            if (first is LineKeyedCodeLensTagger<RunTestLensLocation> tagger)
                RunTestCodeLensRedirect.TaggerRegistry.UnregisterTagger(tagger, fileUri);
        }
    }

    [Fact]
    public async Task Hook_lens_tagger_keeps_refreshing_for_a_remaining_consumer_after_another_consumer_disposes_it()
    {
        const string filePath = @"C:\repo\issue1028-hook.feature";
        var fileUri = new Uri(filePath).AbsoluteUri;
        var line = 1; // read by the fetch, which may run on another thread
        var fetchCount = 0;
        HookCodeLensRedirect.GetLensesAsync = (_, _) =>
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult<IReadOnlyList<HookFeatureLensEntry>>(new[]
            {
                new HookFeatureLensEntry(Volatile.Read(ref line), "1 hook", 0, 0, OwnLevelOnly: true, AlwaysShowPicker: false),
            });
        };
        ITagger<ICodeLensTag>? first = null;
        try
        {
            var snapshot = CreateSnapshot(lineCount: 5);
            var buffer = CreateFeatureBuffer(snapshot, filePath);
            var provider = new HookCodeLensTaggerProvider();

            first = provider.CreateTagger<ICodeLensTag>(buffer)!;
            var second = provider.CreateTagger<ICodeLensTag>(buffer)!;
            var tagsChanged = 0;
            second.TagsChanged += (_, _) => Interlocked.Increment(ref tagsChanged);

            DisposeLikeTheTagAggregator(first);

            var fetchesBefore = Volatile.Read(ref fetchCount);
            Volatile.Write(ref line, 3);
            HookCodeLensRedirect.InvalidateFile(fileUri);

            (await WaitForTagOnLineAsync(second, snapshot, line: 3)).Should().BeTrue("the remaining consumer's tagger must still refresh when the registry invalidates its file");
            fetchCount.Should().BeGreaterThan(fetchesBefore);
            Volatile.Read(ref tagsChanged).Should().BeGreaterThan(0, "the remaining consumer must be told its tags changed");
        }
        finally
        {
            HookCodeLensRedirect.GetLensesAsync = null;
            if (first is LineKeyedCodeLensTagger<HookFeatureLensEntry> tagger)
                HookCodeLensRedirect.TaggerRegistry.UnregisterTagger(tagger, fileUri);
        }
    }
}
