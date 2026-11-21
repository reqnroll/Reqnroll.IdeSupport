namespace Reqnroll.IdeSupport.LSP.Server.Tests.Concurrency;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test calls <see cref="Advance"/>, firing the timers that
/// fall due on the way, so throttling behaviour can be tested without waiting in real time.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, _now + dueTime);
        lock (_timers)
            _timers.Add(timer);
        return timer;
    }

    /// <summary>Number of timers that have been created and neither fired nor been disposed.</summary>
    public int ActiveTimerCount
    {
        get { lock (_timers) return _timers.Count(t => !t.Done); }
    }

    /// <summary>Moves the clock forward by <paramref name="by"/>, firing every timer that falls due, earliest first.</summary>
    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_timers)
                next = _timers.Where(t => !t.Done && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();

            if (next is null)
                break;

            if (next.Due > _now)
                _now = next.Due;
            next.Fire();
        }

        _now = target;
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(TimerCallback callback, object? state, DateTimeOffset due)
        {
            _callback = callback;
            _state = state;
            Due = due;
        }

        public DateTimeOffset Due { get; }

        public bool Done { get; private set; }

        public void Fire()
        {
            Done = true;
            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose() => Done = true;

        public ValueTask DisposeAsync()
        {
            Done = true;
            return ValueTask.CompletedTask;
        }
    }
}
