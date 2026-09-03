using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Documents;

/// <summary>
/// Issue #568: <see cref="DocumentBufferService"/> is the open-document buffer map every
/// <c>didChange</c>/<c>didClose</c>/read handler touches concurrently from the thread pool — the
/// single most concurrency-exposed class in the server, per the audit that raised this issue.
/// These tests exercise concurrent callers rather than the sequential-only coverage in
/// <see cref="DocumentBufferServiceTests"/>.
/// </summary>
public class DocumentBufferServiceConcurrencyTests
{
    private static DocumentUri MakeUri(int i) => DocumentUri.FromFileSystemPath($"/workspace/doc{i}.feature");

    [Fact]
    public async Task Concurrent_Update_calls_for_distinct_uris_never_lose_a_buffer()
    {
        var sut = new DocumentBufferService();
        const int threadCount = 32;

        using var gate = new Barrier(threadCount);
        var tasks = Enumerable.Range(0, threadCount).Select(i => Task.Run(() =>
        {
            gate.SignalAndWait();
            sut.Update(MakeUri(i), i, $"Feature: F{i}\n");
        })).ToArray();

        await Task.WhenAll(tasks);

        sut.All.Should().HaveCount(threadCount, "every concurrently-registered buffer must survive");
        for (var i = 0; i < threadCount; i++)
        {
            sut.TryGet(MakeUri(i), out var buffer).Should().BeTrue();
            buffer!.Version.Should().Be(i);
            buffer.Text.Should().Be($"Feature: F{i}\n");
        }
    }

    [Fact]
    public async Task Concurrent_UpdateTags_calls_for_the_same_uri_never_leave_a_torn_buffer()
    {
        var uri = MakeUri(0);
        var notFoundAt = -1;

        for (var round = 0; round < 500 && notFoundAt < 0; round++)
        {
            var sut = new DocumentBufferService();
            sut.Update(uri, 1, "Feature: X\n");

            // Two distinct (but otherwise interchangeable) collection instances — only their
            // object identity matters, so the assertion below can tell which write "won" without
            // needing a real GherkinRange to build a populated IdeSupportTag.
            var tagsA = new List<IdeSupportTag>();
            var tagsB = new List<IdeSupportTag>();

            using var gate = new Barrier(2);
            var t1 = Task.Run(() => { gate.SignalAndWait(); sut.UpdateTags(uri, tagsA); });
            var t2 = Task.Run(() => { gate.SignalAndWait(); sut.UpdateTags(uri, tagsB); });
            await Task.WhenAll(t1, t2);

            // Whichever write "won", TryGet must observe a complete, self-consistent buffer:
            // the original text/version untouched, and Tags set to exactly one of the two racing
            // writes — never null (a lost update) and never a mix of the two.
            if (!sut.TryGet(uri, out var buffer) ||
                buffer!.Text != "Feature: X\n" ||
                buffer.Version != 1 ||
                (buffer.Tags != tagsA && buffer.Tags != tagsB))
            {
                notFoundAt = round;
            }
        }

        notFoundAt.Should().Be(-1,
            $"a racing UpdateTags pair must leave a complete, consistent buffer, at round {notFoundAt}");
    }

    [Fact]
    public async Task Concurrent_Update_and_Remove_for_the_same_uri_never_throw()
    {
        var sut = new DocumentBufferService();
        var uri = MakeUri(0);
        const int threadCount = 8;
        const int roundsPerThread = 100;

        using var gate = new Barrier(threadCount);
        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            gate.SignalAndWait();
            for (var i = 0; i < roundsPerThread; i++)
            {
                if (t % 2 == 0)
                    sut.Update(uri, i, $"text-{t}-{i}");
                else
                    sut.Remove(uri);
            }
        })).ToArray();

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }
}
