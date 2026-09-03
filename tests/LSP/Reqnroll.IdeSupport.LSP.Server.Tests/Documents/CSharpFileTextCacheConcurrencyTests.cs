using Reqnroll.IdeSupport.LSP.Server.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Documents;

/// <summary>
/// Issue #568: <see cref="CSharpFileTextCache"/> tracks the live text of every open <c>.cs</c>
/// file — <c>TextDocumentSyncHandler</c> writes to it from <c>didOpen</c>/<c>didChange</c>/
/// <c>didClose</c> on whatever thread the LSP dispatch lane hands the notification to, so the
/// same class of concurrent-access gap flagged for <see cref="DocumentBufferService"/> applies
/// here too. These tests exercise concurrent callers rather than the sequential-only coverage in
/// <see cref="CSharpFileTextCacheTests"/>.
/// </summary>
public class CSharpFileTextCacheConcurrencyTests
{
    private static DocumentUri MakeUri(int i) => DocumentUri.FromFileSystemPath($"/workspace/Steps{i}.cs");

    [Fact]
    public async Task Concurrent_Update_calls_for_distinct_uris_never_lose_an_entry()
    {
        var cache = new CSharpFileTextCache();
        const int threadCount = 32;

        using var gate = new Barrier(threadCount);
        var tasks = Enumerable.Range(0, threadCount).Select(i => Task.Run(() =>
        {
            gate.SignalAndWait();
            cache.Update(MakeUri(i), $"content-{i}");
        })).ToArray();

        await Task.WhenAll(tasks);

        cache.All.Should().HaveCount(threadCount);
        for (var i = 0; i < threadCount; i++)
        {
            cache.TryGet(MakeUri(i), out var text).Should().BeTrue();
            text.Should().Be($"content-{i}");
        }
    }

    [Fact]
    public async Task Concurrent_Updates_for_the_same_uri_leave_one_of_the_written_values_not_a_torn_string()
    {
        var uri = MakeUri(0);
        var inconsistentAt = -1;

        for (var round = 0; round < 500 && inconsistentAt < 0; round++)
        {
            var cache = new CSharpFileTextCache();
            var textA = new string('a', 500);
            var textB = new string('b', 500);

            using var gate = new Barrier(2);
            var t1 = Task.Run(() => { gate.SignalAndWait(); cache.Update(uri, textA); });
            var t2 = Task.Run(() => { gate.SignalAndWait(); cache.Update(uri, textB); });
            await Task.WhenAll(t1, t2);

            cache.TryGet(uri, out var text).Should().BeTrue();
            if (text != textA && text != textB)
                inconsistentAt = round;
        }

        inconsistentAt.Should().Be(-1,
            $"a racing Update pair must leave exactly one full write visible, not a mix of both, at round {inconsistentAt}");
    }

    [Fact]
    public async Task Concurrent_Update_and_Remove_for_the_same_uri_never_throw()
    {
        var cache = new CSharpFileTextCache();
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
                    cache.Update(uri, $"text-{t}-{i}");
                else
                    cache.Remove(uri);
            }
        })).ToArray();

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }
}
