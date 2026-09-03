#nullable enable

using System.Threading.Tasks;
using Reqnroll.IdeSupport.LSP.Core.Rename;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Rename;

/// <summary>
/// Issue #568: <see cref="RenameSessionManager"/> backs its sessions with a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>, and <c>Cleanup()</c> enumerates it while
/// <c>SetSession</c>/<c>TryConsume</c> from other threads can be adding or removing entries at the
/// same instant (the rename picker flow and a same-file rename retry are not otherwise
/// synchronised). These tests exercise concurrent callers rather than the sequential-only coverage
/// in <see cref="RenameSessionManagerTests"/>.
/// </summary>
public class RenameSessionManagerConcurrencyTests
{
    [Fact]
    public async Task Concurrent_TryConsume_calls_for_the_same_session_let_exactly_one_caller_succeed()
    {
        var wrongCountAt = -1;

        for (var round = 0; round < 500 && wrongCountAt < 0; round++)
        {
            var sut = new RenameSessionManager();
            sut.SetSession("test.cs", round, 7);

            using var gate = new Barrier(4);
            var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                gate.SignalAndWait();
                return sut.TryConsume("test.cs", round, out var index) ? index : (int?)null;
            })).ToArray();

            var results = await Task.WhenAll(tasks);
            var successes = results.Where(r => r.HasValue).ToArray();

            if (successes.Length != 1 || successes[0] != 7)
                wrongCountAt = round;
        }

        wrongCountAt.Should().Be(-1,
            $"exactly one of several concurrent TryConsume calls for the identical session should succeed, at round {wrongCountAt}");
    }

    [Fact]
    public async Task Concurrent_SetSession_calls_for_distinct_keys_never_lose_a_session()
    {
        var sut = new RenameSessionManager();
        const int threadCount = 16;

        using var gate = new Barrier(threadCount);
        var tasks = Enumerable.Range(0, threadCount).Select(i => Task.Run(() =>
        {
            gate.SignalAndWait();
            sut.SetSession($"file-{i}.cs", 1, i);
        })).ToArray();

        await Task.WhenAll(tasks);

        for (var i = 0; i < threadCount; i++)
        {
            sut.TryConsume($"file-{i}.cs", 1, out var index).Should().BeTrue(
                $"session for file-{i}.cs must survive concurrent SetSession calls (and the Cleanup sweep each one triggers) for unrelated keys");
            index.Should().Be(i);
        }
    }

    [Fact]
    public async Task Concurrent_SetSession_and_TryConsume_across_many_keys_never_throw()
    {
        var sut = new RenameSessionManager();
        const int threadCount = 8;
        const int roundsPerThread = 50;

        using var gate = new Barrier(threadCount);
        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            gate.SignalAndWait();
            for (var i = 0; i < roundsPerThread; i++)
            {
                sut.SetSession($"file-{t}.cs", i, i);
                sut.TryConsume($"file-{t}.cs", i, out _);
            }
        })).ToArray();

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }
}
