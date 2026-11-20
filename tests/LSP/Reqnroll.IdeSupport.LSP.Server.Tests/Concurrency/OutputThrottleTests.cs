using System.Collections.Concurrent;
using System.Diagnostics;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Concurrency;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Concurrency;

public class OutputThrottleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly ManualTimeProvider _time = new();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ConcurrentQueue<string> _ran = new();
    private readonly OutputThrottle _sut;

    public OutputThrottleTests() => _sut = new OutputThrottle(_logger, _time);

    private Func<Task> Record(string name) => () =>
    {
        _ran.Enqueue(name);
        return Task.CompletedTask;
    };

    private Task Run(string key, string name) => _sut.RunThrottledAsync(key, Interval, Record(name));

    private string[] Ran => _ran.ToArray();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task The_first_call_runs_immediately()
    {
        await Run("k", "a");

        Ran.Should().Equal("a");
    }

    [Fact]
    public async Task A_call_that_waits_for_its_turn_returns_without_running_the_action_yet()
    {
        await Run("k", "a");

        await Run("k", "b");

        Ran.Should().Equal("a");
        _time.ActiveTimerCount.Should().Be(1);
    }

    [Fact]
    public async Task A_call_inside_the_interval_runs_once_the_interval_since_the_last_run_has_elapsed()
    {
        await Run("k", "a");
        _time.Advance(TimeSpan.FromMilliseconds(100));
        await Run("k", "b");

        _time.Advance(TimeSpan.FromMilliseconds(149));
        Ran.Should().Equal("a");

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Ran.Should().Equal("a", "b");
    }

    [Fact]
    public async Task A_burst_runs_the_first_action_at_once_and_then_only_the_last_one()
    {
        await Run("k", "a");
        await Run("k", "b");
        await Run("k", "c");
        await Run("k", "d");

        _time.Advance(Interval);

        Ran.Should().Equal("a", "d");
    }

    [Fact]
    public async Task A_sustained_burst_runs_at_most_once_per_interval_and_ends_with_the_last_action()
    {
        var starts = new List<DateTimeOffset>();
        Func<string, Func<Task>> record = name => () =>
        {
            starts.Add(_time.GetUtcNow());
            _ran.Enqueue(name);
            return Task.CompletedTask;
        };

        // An edit every 100 ms for 2 seconds.
        for (var i = 0; i < 20; i++)
        {
            await _sut.RunThrottledAsync("k", Interval, record($"edit {i}"));
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        _time.Advance(Interval);

        Ran.Last().Should().Be("edit 19", "the last change must always be delivered");
        Ran.Length.Should().BeLessThan(20).And.BeGreaterThan(2);
        for (var i = 1; i < starts.Count; i++)
            (starts[i] - starts[i - 1]).Should().BeGreaterThanOrEqualTo(Interval, "runs are at most one per interval");
    }

    [Fact]
    public async Task After_a_quiet_period_the_next_call_runs_immediately_again()
    {
        await Run("k", "a");
        _time.Advance(TimeSpan.FromSeconds(1));

        await Run("k", "b");

        Ran.Should().Equal("a", "b");
        _time.ActiveTimerCount.Should().Be(0);
    }

    [Fact]
    public async Task A_deferred_run_starts_the_interval_again_for_the_next_call()
    {
        await Run("k", "a");
        await Run("k", "b");
        _time.Advance(Interval); // b runs at +250 ms

        await Run("k", "c");
        _time.Advance(TimeSpan.FromMilliseconds(249));
        Ran.Should().Equal("a", "b");

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Ran.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Keys_are_throttled_independently()
    {
        await Run("one", "a");
        await Run("two", "b");

        Ran.Should().Equal("a", "b");
    }

    [Fact]
    public async Task Keys_are_compared_without_regard_to_case()
    {
        await Run("Diagnostics:file:///C:/w/A.feature", "a");
        await Run("diagnostics:file:///c:/w/a.feature", "b");

        Ran.Should().Equal("a");
    }

    [Fact]
    public async Task Cancel_drops_the_waiting_action_and_forgets_when_the_key_last_ran()
    {
        await Run("k", "a");
        await Run("k", "b");

        _sut.Cancel("k");
        _time.Advance(TimeSpan.FromSeconds(1));
        Ran.Should().Equal("a");

        await Run("k", "c");
        Ran.Should().Equal("a", "c");
    }

    [Fact]
    public void Cancel_of_a_key_that_never_ran_is_harmless()
    {
        var cancel = () => _sut.Cancel("never used");

        cancel.Should().NotThrow();
    }

    [Fact]
    public async Task A_deferred_run_never_overlaps_the_run_before_it()
    {
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _sut.RunThrottledAsync("k", Interval, async () =>
        {
            _ran.Enqueue("first started");
            await firstGate.Task;
            _ran.Enqueue("first finished");
        });
        _time.Advance(TimeSpan.FromMilliseconds(400)); // the interval has long passed, but the first run is still going

        await Run("k", "second");
        _time.Advance(TimeSpan.Zero);

        Ran.Should().Equal("first started");

        firstGate.SetResult();
        await first;
        await WaitUntilAsync(() => Ran.Contains("second"));

        Ran.Should().Equal("first started", "first finished", "second");
    }

    [Fact]
    public async Task A_failure_of_an_immediate_action_reaches_the_caller_and_does_not_block_later_runs()
    {
        var failing = () => _sut.RunThrottledAsync("k", Interval, () => throw new InvalidOperationException("boom"));
        await failing.Should().ThrowAsync<InvalidOperationException>();

        _time.Advance(Interval);
        await Run("k", "after");

        Ran.Should().Equal("after");
    }

    [Fact]
    public async Task A_failure_of_a_deferred_action_does_not_reach_the_caller_or_block_later_runs()
    {
        await Run("k", "a");
        await _sut.RunThrottledAsync("k", Interval, () => throw new InvalidOperationException("boom"));

        var advance = () => _time.Advance(Interval);
        advance.Should().NotThrow();

        await Run("k", "c");
        _time.Advance(Interval);

        Ran.Should().Equal("a", "c");
    }

    [Fact]
    public async Task Dispose_stops_the_waiting_timers()
    {
        await Run("k", "a");
        await Run("k", "b");

        _sut.Dispose();
        _time.Advance(TimeSpan.FromSeconds(1));

        Ran.Should().Equal("a");
        _time.ActiveTimerCount.Should().Be(0);
    }
}
