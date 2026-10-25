namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// A time provider whose timestamps only advance when a test advances them. With manual timers, its timers also only fire when a test advances the time
/// past their due time; otherwise they are system timers.
/// </summary>
public sealed class ManualTimeProvider(bool manualTimers = false) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>
    /// Gets the number of manual timers that are waiting to fire.
    /// </summary>
    public int PendingTimerCount
    {
        get {
            lock (_timers)
                return _timers.Count(t => t.DueTimestamp is not null);
        }
    }

    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (!manualTimers)
            return base.CreateTimer(callback, state, dueTime, period);

        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);

        return timer;
    }

    public void Advance(TimeSpan time)
    {
        long now = Interlocked.Add(ref _timestamp, time.Ticks);
        List<ManualTimer> due;

        lock (_timers)
        {
            due = [.. _timers.Where(t => t.DueTimestamp <= now)];

            foreach (var timer in due)
                timer.DueTimestamp = timer.Period is { } period ? now + period.Ticks : null;
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? DueTimestamp { get; set; }

        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._timers)
            {
                if (!owner._timers.Contains(this))
                    owner._timers.Add(this);

                DueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetTimestamp() + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._timers)
                owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
