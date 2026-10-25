namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// A clock whose time and timers only move when a test advances it. Advancing fires each timer that became due once, on the calling thread. Unlike
/// <c>TestClock</c>, delays and timers created from it never fire on their own. With manual timestamps, elapsed times measured with its timestamps only
/// move when it is advanced too.
/// </summary>
public sealed class ManualClock(bool manualTimestamps = false) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

    public DateTime UtcNow => GetUtcNow().UtcDateTime;

    public int TimerCount
    {
        get {
            lock (_timers)
                return _timers.Count;
        }
    }

    public override long TimestampFrequency => manualTimestamps ? TimeSpan.TicksPerSecond : base.TimestampFrequency;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_timers)
            return _utcNow;
    }

    public override long GetTimestamp() => manualTimestamps ? GetUtcNow().UtcTicks : base.GetTimestamp();

    /// <summary>
    /// Gets a value indicating whether a timer is waiting to fire the specified time from now.
    /// </summary>
    public bool HasTimerDueIn(TimeSpan dueTime)
    {
        lock (_timers)
            return _timers.Any(t => t.DueUtc - _utcNow == dueTime);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan time)
    {
        List<ManualTimer> due;

        lock (_timers)
        {
            _utcNow += time;
            due = _timers.Where(t => t.DueUtc <= _utcNow).ToList();

            foreach (var timer in due)
                timer.Reschedule(_utcNow);
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period;

        public DateTimeOffset DueUtc { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timers)
            {
                clock._timers.Remove(this);

                if (dueTime == Timeout.InfiniteTimeSpan)
                    return true;

                DueUtc = clock._utcNow + dueTime;
                _period = period;
                clock._timers.Add(this);
            }

            return true;
        }

        public void Reschedule(DateTimeOffset utcNow)
        {
            if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
            {
                clock._timers.Remove(this);
                return;
            }

            while (DueUtc <= utcNow)
                DueUtc += _period;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._timers)
                clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
