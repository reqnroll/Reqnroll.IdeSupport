using System.Collections.Concurrent;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Concurrency;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Concurrency;

public class RefreshGateTests
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ConcurrentQueue<string> _events = new();

    private RefreshGate CreateSut(TimeSpan? timeout = null) => new(_logger, timeout);

    // A send that records when it starts and finishes, and finishes only when the test says so.
    private sealed class ControlledSend
    {
        private readonly ConcurrentQueue<string> _events;
        private readonly string _name;
        private readonly TaskCompletionSource _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledSend(ConcurrentQueue<string> events, string name)
        {
            _events = events;
            _name = name;
        }

        public async Task SendAsync(CancellationToken token)
        {
            _events.Enqueue($"{_name} sent");
            await _answered.Task.ConfigureAwait(false);
            _events.Enqueue($"{_name} answered");
        }

        public void Answer() => _answered.TrySetResult();
    }

    private Func<CancellationToken, Task> Instant(string name) => _ =>
    {
        _events.Enqueue($"{name} sent");
        return Task.CompletedTask;
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task A_request_with_nothing_outstanding_is_sent_at_once()
    {
        await CreateSut().RunAsync("k", Instant("a"));

        _events.Should().Equal("a sent");
    }

    [Fact]
    public async Task A_request_while_one_is_outstanding_is_not_sent_until_the_first_is_answered()
    {
        var sut = CreateSut();
        var first = new ControlledSend(_events, "first");
        var firstRun = sut.RunAsync("k", first.SendAsync);
        await WaitUntilAsync(() => _events.Contains("first sent"));

        await sut.RunAsync("k", Instant("second"));

        _events.Should().Equal("first sent");

        first.Answer();
        await firstRun;
    }

    [Fact]
    public async Task Requests_that_arrive_while_one_is_outstanding_are_folded_into_the_latest_one_which_follows_the_answer()
    {
        var sut = CreateSut();
        var first = new ControlledSend(_events, "first");
        var firstRun = sut.RunAsync("k", first.SendAsync);
        await WaitUntilAsync(() => _events.Contains("first sent"));

        await sut.RunAsync("k", Instant("second"));
        await sut.RunAsync("k", Instant("third"));
        await sut.RunAsync("k", Instant("fourth"));
        first.Answer();
        await firstRun;

        _events.Should().Equal("first sent", "first answered", "fourth sent");
    }

    [Fact]
    public async Task The_client_is_asked_again_after_it_has_answered_when_a_request_arrived_meanwhile()
    {
        var sut = CreateSut();
        var runs = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task> send = async _ =>
        {
            if (Interlocked.Increment(ref runs) == 1)
                await gate.Task.ConfigureAwait(false);
        };

        var run = sut.RunAsync("k", send);
        await WaitUntilAsync(() => Volatile.Read(ref runs) == 1);
        await sut.RunAsync("k", send);
        gate.SetResult();
        await run;

        runs.Should().Be(2);
    }

    [Fact]
    public async Task Nothing_follows_when_no_request_arrived_in_the_meantime()
    {
        var sut = CreateSut();
        var first = new ControlledSend(_events, "first");
        var run = sut.RunAsync("k", first.SendAsync);
        await WaitUntilAsync(() => _events.Contains("first sent"));

        first.Answer();
        await run;
        await Task.Delay(50);

        _events.Should().Equal("first sent", "first answered");
    }

    [Fact]
    public async Task After_the_gate_has_been_released_the_next_request_is_sent_at_once()
    {
        var sut = CreateSut();
        await sut.RunAsync("k", Instant("a"));

        await sut.RunAsync("k", Instant("b"));

        _events.Should().Equal("a sent", "b sent");
    }

    [Fact]
    public async Task Keys_are_gated_independently()
    {
        var sut = CreateSut();
        var first = new ControlledSend(_events, "one");
        var firstRun = sut.RunAsync("one", first.SendAsync);
        await WaitUntilAsync(() => _events.Contains("one sent"));

        await sut.RunAsync("two", Instant("two"));

        _events.Should().Equal("one sent", "two sent");

        first.Answer();
        await firstRun;
    }

    [Fact]
    public async Task A_request_that_fails_releases_the_gate()
    {
        var sut = CreateSut();
        await sut.RunAsync("k", _ => throw new InvalidOperationException("boom"));

        await sut.RunAsync("k", Instant("after"));

        _events.Should().Equal("after sent");
    }

    [Fact]
    public async Task A_request_that_ignores_cancellation_and_is_never_answered_still_releases_the_gate_after_the_timeout()
    {
        // Cancelling an OmniSharp request does not end the task the caller awaits; only the client's answer does.
        var sut = CreateSut(TimeSpan.FromMilliseconds(50));
        var neverAnswered = new TaskCompletionSource();

        await sut.RunAsync("k", _ => neverAnswered.Task);
        await sut.RunAsync("k", Instant("after"));

        _events.Should().Equal("after sent");
    }

    [Fact]
    public async Task A_request_that_is_never_answered_is_given_up_on_after_the_timeout()
    {
        var sut = CreateSut(TimeSpan.FromMilliseconds(50));
        var sendsBefore = 0;

        await sut.RunAsync("k", async token =>
        {
            Interlocked.Increment(ref sendsBefore);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        });

        await sut.RunAsync("k", Instant("after"));

        sendsBefore.Should().Be(1);
        _events.Should().Equal("after sent");
    }
}
