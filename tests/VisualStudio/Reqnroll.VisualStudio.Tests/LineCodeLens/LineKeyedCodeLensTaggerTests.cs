using AwesomeAssertions;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Text;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LineCodeLens;

/// <summary>
/// Unit tests for <see cref="LineKeyedCodeLensTagger{TEntry}"/> — the generic classic-CodeLens
/// tagger shared by every Reqnroll classic CodeLens feature (issue #372/#262 follow-up
/// extraction). Exercises it against a fake <see cref="ITextBuffer"/>/<see cref="ITextSnapshot"/>
/// rather than a real VS editor host: every method this class calls on those interfaces resolves to
/// plain value math (line/position bookkeeping), with no live editor session needed.
/// </summary>
/// <remarks>
/// The <c>fetch</c> delegates below always return an already-completed <c>Task</c>
/// (<see cref="Task.FromResult{TResult}"/>). Because <see cref="LineKeyedCodeLensTagger{TEntry}.RequestRefresh"/>
/// awaits that task with <c>ConfigureAwait(false)</c>, an already-completed task lets the whole
/// refresh run synchronously to completion within the triggering call — no thread hand-off, no
/// polling needed to observe the result immediately after construction or after a manual
/// <c>RequestRefresh()</c> call.
/// </remarks>
public class LineKeyedCodeLensTaggerTests
{
    private sealed record TestEntry(int Line, string Value);

    private static ITextSnapshot CreateSnapshot(int lineCount)
    {
        var snapshot = Substitute.For<ITextSnapshot>();
        snapshot.LineCount.Returns(lineCount);
        snapshot.Length.Returns(lineCount * 10 + 10);
        snapshot.GetLineFromLineNumber(Arg.Any<int>()).Returns(ci =>
        {
            var lineNumber = ci.Arg<int>();
            // Compute the SnapshotPoint (which itself calls snapshot.Length) *before* touching
            // `line`, so it isn't evaluated as part of a `line.Start.Returns(...)` argument list —
            // doing that inline confuses NSubstitute's "last configured call" tracking, since
            // evaluating the argument makes an unrelated call to the same `snapshot` substitute
            // between `line.Start` and `.Returns(...)`.
            var start = new SnapshotPoint(snapshot, lineNumber * 10);
            var line = Substitute.For<ITextSnapshotLine>();
            line.LineNumber.Returns(lineNumber);
            line.Start.Returns(start);
            return line;
        });
        return snapshot;
    }

    private static ITextBuffer CreateBuffer(ITextSnapshot snapshot)
    {
        var buffer = Substitute.For<ITextBuffer>();
        buffer.CurrentSnapshot.Returns(snapshot);
        return buffer;
    }

    private static NormalizedSnapshotSpanCollection WholeDocument(ITextSnapshot snapshot) =>
        new(snapshot, new Span(0, snapshot.Length));

    private static WeakTaggerRegistry<LineKeyedCodeLensTagger<TestEntry>> CreateRegistry() =>
        new(t => t.RequestRefresh());

    private static string Encode(int line, IEnumerable<TestEntry> entries) =>
        LineElementDescription.Encode(line, entries.Select(e => e.Value));

    private static LineKeyedCodeLensTagger<TestEntry> CreateSut(
        ITextBuffer buffer,
        Func<string, CancellationToken, Task<IReadOnlyList<TestEntry>?>> fetch,
        WeakTaggerRegistry<LineKeyedCodeLensTagger<TestEntry>>? registry = null,
        string fileUri = "file:///a.feature") =>
        new(buffer, @"C:\a.feature", fileUri, fetch, e => e.Line, Encode, registry ?? CreateRegistry());

    // ── GetTags: basic content ────────────────────────────────────────────────────

    [Fact]
    public void GetTags_returns_no_tags_before_the_data_source_is_wired_up()
    {
        // fetch returning null (not an empty list) means "not wired up yet" — the initial empty
        // tag set must be left in place rather than treated as "zero entries".
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        var sut = CreateSut(buffer, (_, _) => Task.FromResult<IReadOnlyList<TestEntry>?>(null));

        var tags = sut.GetTags(WholeDocument(snapshot)).ToList();

        tags.Should().BeEmpty();
    }

    [Fact]
    public void GetTags_returns_one_tag_per_grouped_line()
    {
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        IReadOnlyList<TestEntry> entries = new[]
        {
            new TestEntry(1, "own-level"),
            new TestEntry(1, "step-hooks"),
            new TestEntry(3, "own-level"),
        };
        var sut = CreateSut(buffer, (_, _) => Task.FromResult<IReadOnlyList<TestEntry>?>(entries));

        var tags = sut.GetTags(WholeDocument(snapshot)).ToList();

        // Our fake snapshot lays lines out 10 apart (see CreateSnapshot), so dividing a tag's
        // start position by that stride recovers the line number without needing a
        // GetLineFromPosition stub.
        tags.Select(t => t.Span.Start.Position / 10)
            .Should().BeEquivalentTo(new[] { 1, 3 });
    }

    [Fact]
    public void GetTags_encodes_every_entry_on_a_line_into_that_lines_ElementDescription()
    {
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        IReadOnlyList<TestEntry> entries = new[]
        {
            new TestEntry(1, "own-level"),
            new TestEntry(1, "step-hooks"),
        };
        var sut = CreateSut(buffer, (_, _) => Task.FromResult<IReadOnlyList<TestEntry>?>(entries));

        var tag = sut.GetTags(WholeDocument(snapshot)).Should().ContainSingle().Subject.Tag;

        LineElementDescription.TryDecode(tag.Descriptor.ElementDescription, out var decodedLine).Should().BeTrue();
        decodedLine.Should().Be(1);
    }

    [Fact]
    public void GetTags_ignores_a_line_that_no_longer_exists_in_the_current_snapshot()
    {
        // The fetched entry set can race ahead of/behind the live snapshot (e.g. the buffer shrank
        // since the fetch started); a stale out-of-range line must not be rendered.
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        IReadOnlyList<TestEntry> entries = new[] { new TestEntry(1, "a"), new TestEntry(99, "b") };
        var sut = CreateSut(buffer, (_, _) => Task.FromResult<IReadOnlyList<TestEntry>?>(entries));

        var tags = sut.GetTags(WholeDocument(snapshot)).ToList();

        tags.Should().ContainSingle();
    }

    [Fact]
    public void GetTags_returns_nothing_for_an_empty_span_collection()
    {
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        IReadOnlyList<TestEntry> entries = new[] { new TestEntry(1, "a") };
        var sut = CreateSut(buffer, (_, _) => Task.FromResult<IReadOnlyList<TestEntry>?>(entries));

        var tags = sut.GetTags(new NormalizedSnapshotSpanCollection()).ToList();

        tags.Should().BeEmpty();
    }

    [Fact]
    public void GetTags_excludes_tags_outside_the_requested_spans()
    {
        var snapshot = CreateSnapshot(lineCount: 5); // line starts: 0, 10, 20, 30, 40
        var buffer = CreateBuffer(snapshot);
        IReadOnlyList<TestEntry> entries = new[] { new TestEntry(1, "a"), new TestEntry(3, "b") };
        var sut = CreateSut(buffer, (_, _) => Task.FromResult<IReadOnlyList<TestEntry>?>(entries));

        var requestedSpans = new NormalizedSnapshotSpanCollection(snapshot, new Span(0, 15)); // covers lines 0-1 only

        var tags = sut.GetTags(requestedSpans).ToList();

        tags.Should().ContainSingle();
        (tags[0].Span.Start.Position / 10).Should().Be(1);
    }

    // ── Tag identity / reuse across refreshes ─────────────────────────────────────

    [Fact]
    public void The_same_tag_instance_is_reused_across_refreshes_when_a_lines_content_is_unchanged()
    {
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        IReadOnlyList<TestEntry> entries = new[] { new TestEntry(1, "a") };
        var sut = CreateSut(buffer, (_, _) => Task.FromResult<IReadOnlyList<TestEntry>?>(entries));

        var first = sut.GetTags(WholeDocument(snapshot)).Should().ContainSingle().Subject.Tag;
        sut.RequestRefresh();
        var second = sut.GetTags(WholeDocument(snapshot)).Should().ContainSingle().Subject.Tag;

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void A_new_tag_instance_is_created_when_a_lines_content_changes()
    {
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        var value = "a";
        var sut = CreateSut(buffer,
            (_, _) => Task.FromResult<IReadOnlyList<TestEntry>?>(new[] { new TestEntry(1, value) }));

        var first = sut.GetTags(WholeDocument(snapshot)).Should().ContainSingle().Subject.Tag;
        value = "b";
        sut.RequestRefresh();
        var second = sut.GetTags(WholeDocument(snapshot)).Should().ContainSingle().Subject.Tag;

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void A_tag_for_a_line_that_no_longer_has_entries_raises_Disconnected()
    {
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        IReadOnlyList<TestEntry> entries = new[] { new TestEntry(1, "a"), new TestEntry(2, "b") };
        var sut = CreateSut(buffer, (_, _) => Task.FromResult<IReadOnlyList<TestEntry>?>(entries));

        var tagForLine2 = sut.GetTags(WholeDocument(snapshot))
            .Single(t => t.Span.Start.Position / 10 == 2).Tag;
        var disconnected = false;
        tagForLine2.Disconnected += (_, _) => disconnected = true;

        entries = new[] { new TestEntry(1, "a") }; // line 2's entry disappears
        sut.RequestRefresh();

        disconnected.Should().BeTrue();
    }

    [Fact]
    public void TagsChanged_fires_after_a_refresh_completes()
    {
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        IReadOnlyList<TestEntry> entries = new[] { new TestEntry(1, "a") };
        var sut = CreateSut(buffer, (_, _) => Task.FromResult<IReadOnlyList<TestEntry>?>(entries));
        var raised = false;
        sut.TagsChanged += (_, _) => raised = true;

        sut.RequestRefresh();

        raised.Should().BeTrue();
    }

    // ── Failure handling ─────────────────────────────────────────────────────────

    [Fact]
    public void A_fetch_that_throws_leaves_the_previous_tag_set_in_place_and_does_not_propagate()
    {
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        var shouldThrow = true;
        IReadOnlyList<TestEntry> entries = new[] { new TestEntry(1, "a") };

        var construct = () => CreateSut(buffer, (_, _) =>
            shouldThrow ? throw new InvalidOperationException("server unavailable") : Task.FromResult<IReadOnlyList<TestEntry>?>(entries));

        var sut = construct.Should().NotThrow().Subject;
        sut.GetTags(WholeDocument(snapshot)).Should().BeEmpty();

        shouldThrow = false;
        sut.RequestRefresh();

        sut.GetTags(WholeDocument(snapshot)).Should().ContainSingle();
    }

    // ── Registry integration ─────────────────────────────────────────────────────

    [Fact]
    public void Construction_registers_the_tagger_with_the_registry_so_InvalidateFile_reaches_it()
    {
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        var fetchCount = 0;
        var registry = CreateRegistry();
        var sut = CreateSut(buffer, (_, _) =>
        {
            fetchCount++;
            return Task.FromResult<IReadOnlyList<TestEntry>?>(Array.Empty<TestEntry>());
        }, registry, fileUri: "file:///a.feature");
        fetchCount.Should().Be(1); // the constructor's own initial refresh

        registry.InvalidateFile("file:///a.feature");

        fetchCount.Should().Be(2);
    }

    [Fact]
    public void Dispose_unregisters_the_tagger_so_InvalidateFile_no_longer_reaches_it()
    {
        var snapshot = CreateSnapshot(lineCount: 5);
        var buffer = CreateBuffer(snapshot);
        var fetchCount = 0;
        var registry = CreateRegistry();
        var sut = CreateSut(buffer, (_, _) =>
        {
            fetchCount++;
            return Task.FromResult<IReadOnlyList<TestEntry>?>(Array.Empty<TestEntry>());
        }, registry, fileUri: "file:///a.feature");
        fetchCount.Should().Be(1);

        sut.Dispose();
        registry.InvalidateFile("file:///a.feature");

        fetchCount.Should().Be(1);
    }
}
