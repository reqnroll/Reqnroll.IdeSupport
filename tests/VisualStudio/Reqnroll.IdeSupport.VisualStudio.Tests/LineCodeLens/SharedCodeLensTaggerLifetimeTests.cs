using Microsoft.VisualStudio.Language.CodeLens;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LineCodeLens;

/// <summary>
/// The .feature CodeLens tagger provider hands every consumer of a buffer the
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
            var provider = new FeatureCodeLensTaggerProvider();

            first = provider.CreateTagger<ICodeLensTag>(buffer)!;   // e.g. the main editor view
            var second = provider.CreateTagger<ICodeLensTag>(buffer)!; // e.g. a split or peek view
            var tagsChanged = 0;
            second.TagsChanged += (_, _) => Interlocked.Increment(ref tagsChanged);

            DisposeLikeTheTagAggregator(first); // the first view closes; the buffer stays open in the second

            var fetchesBefore = Volatile.Read(ref fetchCount);
            Volatile.Write(ref line, 3);
            FeatureCodeLensTaggerProvider.TaggerRegistry.InvalidateFile(fileUri);

            (await WaitForTagOnLineAsync(second, snapshot, line: 3)).Should().BeTrue("the remaining consumer's tagger must still refresh when the registry invalidates its file");
            fetchCount.Should().BeGreaterThan(fetchesBefore);
            Volatile.Read(ref tagsChanged).Should().BeGreaterThan(0, "the remaining consumer must be told its tags changed");
        }
        finally
        {
            RunTestCodeLensRedirect.GetTagLocationsAsync = null;
            // The registry is process-wide: leave no tagger behind to answer another test's invalidation.
            if (first is LineKeyedCodeLensTagger<FeatureLineLensEntry> tagger)
                FeatureCodeLensTaggerProvider.TaggerRegistry.UnregisterTagger(tagger, fileUri);
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
            var provider = new FeatureCodeLensTaggerProvider();

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
            if (first is LineKeyedCodeLensTagger<FeatureLineLensEntry> tagger)
                FeatureCodeLensTaggerProvider.TaggerRegistry.UnregisterTagger(tagger, fileUri);
        }
    }

    [Fact]
    public async Task Fetch_returns_null_only_when_neither_source_is_wired()
    {
        RunTestCodeLensRedirect.GetTagLocationsAsync = null;
        HookCodeLensRedirect.GetLensesAsync = null;

        (await FeatureCodeLensTaggerProvider.FetchAsync("file:///C:/repo/a.feature", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Fetch_uses_the_wired_source_when_the_other_is_not_wired_yet()
    {
        HookCodeLensRedirect.GetLensesAsync = (_, _) =>
            Task.FromResult<IReadOnlyList<HookFeatureLensEntry>>(new[]
            {
                new HookFeatureLensEntry(2, "1 hook", 2, 0, OwnLevelOnly: true, AlwaysShowPicker: false),
            });
        try
        {
            RunTestCodeLensRedirect.GetTagLocationsAsync = null;

            var entries = await FeatureCodeLensTaggerProvider.FetchAsync("file:///C:/repo/a.feature", CancellationToken.None);

            entries.Should().ContainSingle().Which.Should().Match<FeatureLineLensEntry>(e => e.Line == 2 && e.Kind == LineLensKinds.Hooks);
        }
        finally
        {
            HookCodeLensRedirect.GetLensesAsync = null;
        }
    }

    [Fact]
    public async Task Run_and_hook_lenses_on_the_same_line_share_one_tag_listing_every_kind()
    {
        // VS keeps one CodeLens tag per line and offers it to every data-point provider, so the
        // tag must describe every lens kind on that line, whichever source it came from.
        const string filePath = @"C:\repo\shared-line.feature";
        var fileUri = new Uri(filePath).AbsoluteUri;
        RunTestCodeLensRedirect.GetTagLocationsAsync = (_, _) =>
            Task.FromResult<IReadOnlyList<RunTestLensLocation>>(new[] { new RunTestLensLocation(1, "Scenario|S") });
        HookCodeLensRedirect.GetLensesAsync = (_, _) =>
            Task.FromResult<IReadOnlyList<HookFeatureLensEntry>>(new[]
            {
                new HookFeatureLensEntry(1, "1 hook", 1, 0, OwnLevelOnly: true, AlwaysShowPicker: false),
                new HookFeatureLensEntry(1, "2 step hooks", 2, 4, OwnLevelOnly: false, AlwaysShowPicker: true),
            });
        ITagger<ICodeLensTag>? created = null;
        try
        {
            var snapshot = CreateSnapshot(lineCount: 5);
            var buffer = CreateFeatureBuffer(snapshot, filePath);
            created = new FeatureCodeLensTaggerProvider().CreateTagger<ICodeLensTag>(buffer)!;

            (await WaitForTagOnLineAsync(created, snapshot, line: 1)).Should().BeTrue();
            var description = created.GetTags(WholeDocument(snapshot)).Single().Tag.Descriptor.ElementDescription;

            LineElementDescription.HasLensKind(description, LineLensKinds.Run).Should().BeTrue();
            LineElementDescription.HasLensKind(description, LineLensKinds.Hooks).Should().BeTrue();
            LineElementDescription.HasLensKind(description, LineLensKinds.StepHooks).Should().BeTrue();
        }
        finally
        {
            RunTestCodeLensRedirect.GetTagLocationsAsync = null;
            HookCodeLensRedirect.GetLensesAsync = null;
            if (created is LineKeyedCodeLensTagger<FeatureLineLensEntry> tagger)
                FeatureCodeLensTaggerProvider.TaggerRegistry.UnregisterTagger(tagger, fileUri);
        }
    }
}
