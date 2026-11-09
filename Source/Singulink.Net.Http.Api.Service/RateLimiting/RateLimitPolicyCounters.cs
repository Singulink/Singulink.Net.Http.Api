namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The counts of one per-client limit on this server, which the limiters of all its clients update, so that a client's limiter that is discarded while it
/// is idle loses none of them. The metrics and the health check of rate limiting read them.
/// </summary>
internal sealed class RateLimitPolicyCounters(string? policy, int permitLimit, TimeSpan window)
{
    private int _queuedRequests;
    private long _rejected;
    private long _notCounted;
    private long _lastNotCountedTicks;
    private int _hasCounted;

    /// <summary>
    /// Gets the name of the limit's policy, or <see langword="null"/> for the global limiter, which has none.
    /// </summary>
    public string? Policy { get; } = policy;

    public int PermitLimit { get; } = permitLimit;

    public TimeSpan Window { get; } = window;

    public int QueuedRequests => Volatile.Read(ref _queuedRequests);

    public long Rejected => Interlocked.Read(ref _rejected);

    public long NotCounted => Interlocked.Read(ref _notCounted);

    /// <summary>
    /// Gets when the store last couldn't count a request of the limit, or <see langword="null"/> if it never failed to.
    /// </summary>
    public DateTimeOffset? LastNotCountedUtc => Interlocked.Read(ref _lastNotCountedTicks) is var ticks and not 0 ? new(ticks, TimeSpan.Zero) : null;

    /// <summary>
    /// Gets a value indicating whether the limit has counted a request since the app started.
    /// </summary>
    public bool HasCounted => Volatile.Read(ref _hasCounted) is not 0;

    public void AddQueued(int count) => Interlocked.Add(ref _queuedRequests, count);

    public void MarkCounted()
    {
        if (Volatile.Read(ref _hasCounted) is 0)
            Volatile.Write(ref _hasCounted, 1);
    }

    public void AddRejected() => Interlocked.Increment(ref _rejected);

    public void AddNotCounted(DateTimeOffset utcNow)
    {
        Interlocked.Increment(ref _notCounted);
        Interlocked.Exchange(ref _lastNotCountedTicks, utcNow.UtcTicks);
    }
}
