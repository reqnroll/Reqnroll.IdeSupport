using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Parsing.CSharp;

/// <summary>
/// Issue #568: <see cref="CSharpSyntaxTreeCache"/>'s check-then-parse-then-store-then-evict
/// sequence in <c>Store</c>/<c>EvictIfNeeded</c> is not atomic across threads — <c>EvictIfNeeded</c>
/// snapshots <c>_entries.OrderBy(LastAccess)</c> and removes while another thread can be mid-
/// <c>Store</c>, the same non-atomic-cache shape that caused #554. These tests exercise concurrent
/// callers rather than the sequential-only coverage in <see cref="CSharpSyntaxTreeCacheTests"/>.
/// </summary>
public class CSharpSyntaxTreeCacheConcurrencyTests
{
    [Fact]
    public async Task Concurrent_GetOrParse_calls_for_the_same_key_and_text_never_throw_and_agree_on_content()
    {
        const string text = "public class Steps { public void M() { } }";

        for (var round = 0; round < 200; round++)
        {
            var sut = new CSharpSyntaxTreeCache();
            var path = $"/virtual/Concurrent{round}.cs";

            using var gate = new Barrier(4);
            var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                gate.SignalAndWait();
                return sut.GetOrParse(path, text);
            })).ToArray();

            var roots = await Task.WhenAll(tasks);

            // Whichever thread's parse "won" the race to be cached, every caller must observe a
            // root representing the identical source text — no torn/partial entry.
            roots.Should().OnlyContain(r => r.ToFullString() == text);
        }
    }

    [Fact]
    public async Task Concurrent_inserts_beyond_the_cap_never_lose_track_of_entries_or_grow_the_cache_unboundedly()
    {
        var sut = new CSharpSyntaxTreeCache();

        // Far more distinct keys than MaxEntries (64), inserted concurrently across many threads,
        // so several threads' EvictIfNeeded calls race against each other's snapshot-then-remove.
        const int threadCount = 8;
        const int perThread = 20;

        using var gate = new Barrier(threadCount);
        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            gate.SignalAndWait();
            for (var i = 0; i < perThread; i++)
                sut.GetOrParse($"/virtual/T{t}_F{i}.cs", $"public class C{t}_{i} {{ }}");
        })).ToArray();

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync("concurrent Store/EvictIfNeeded calls must not corrupt the underlying dictionary");

        // A cache that lost eviction races could, in the worst case, retain a small multiple of
        // MaxEntries — but must never simply keep growing to the full insert count (threadCount *
        // perThread = 160), which would mean eviction is not being applied at all under contention.
        var finalCount = GetEntryCount(sut);
        finalCount.Should().BeLessThan(threadCount * perThread,
            "the bounded-cache eviction must still take effect once the concurrent burst settles, even if racy evictions left it above the exact 64 cap");
    }

    private static int GetEntryCount(CSharpSyntaxTreeCache sut)
    {
        var field = typeof(CSharpSyntaxTreeCache).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (System.Collections.IDictionary)field.GetValue(sut)!;
        return dict.Count;
    }
}
