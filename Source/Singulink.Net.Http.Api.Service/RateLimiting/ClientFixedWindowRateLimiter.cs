using System.Globalization;
using System.Threading.RateLimiting;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// A fixed window rate limiter for one client that counts requests in the <see cref="IShortLivedStore"/>, so that all the servers of a service share the
/// count. Windows are aligned to the clock, so every server counts the same window. A request that the store has no room to count is refused, with the
/// store's <see cref="ServiceUnavailableApiException"/> in the lease's <see cref="ErrorMetadata"/>.
/// </summary>
internal sealed class ClientFixedWindowRateLimiter : RateLimiter
{
    /// <summary>
    /// The metadata of a lease that was refused because the request could not be counted, with the error to report instead of a rate limit.
    /// </summary>
    public static readonly MetadataName<ApiException> ErrorMetadata = new("Singulink.Net.Http.Api.Error");

    /// <summary>
    /// The metadata of a lease that was refused by the limit, with the limit's rejection message if it has one.
    /// </summary>
    public static readonly MetadataName<string> RejectionMessageMetadata = new("Singulink.Net.Http.Api.RejectionMessage");

    private static readonly RateLimitLease AcquiredLease = new Lease(true, null);

    // Tells the rate limiting middleware to acquire asynchronously instead.
    private static readonly RateLimitLease NotCountedLease = new Lease(false, null);

    private readonly IShortLivedStore _store;
    private readonly MemoryShortLivedStore? _memoryStore;
    private readonly TimeProvider _timeProvider;
    private readonly string _keyPrefix;
    private readonly int _permitLimit;
    private readonly long _windowTicks;
    private readonly int _queueLimit;
    private readonly string? _rejectionMessage;

    private int _queuedPermits;
    private long _lastUsedTimestamp;

    // countSynchronously: whether requests are counted when the rate limiting middleware first tries to acquire synchronously, which only the memory store
    // can do. A global limiter must not: when an endpoint's limiter refuses that try, the middleware acquires from the global limiter again, which would
    // count the request twice.
    public ClientFixedWindowRateLimiter(
        IShortLivedStore store,
        TimeProvider timeProvider,
        string keyPrefix,
        int permitLimit,
        TimeSpan window,
        int queueLimit,
        string? rejectionMessage,
        bool countSynchronously)
    {
        _store = store;
        _memoryStore = countSynchronously ? store as MemoryShortLivedStore : null;
        _timeProvider = timeProvider;
        _keyPrefix = keyPrefix;
        _permitLimit = permitLimit;
        _windowTicks = window.Ticks;
        _queueLimit = queueLimit;
        _rejectionMessage = rejectionMessage;
        _lastUsedTimestamp = timeProvider.GetTimestamp();
    }

    public override TimeSpan? IdleDuration =>
        Volatile.Read(ref _queuedPermits) > 0 ? null : _timeProvider.GetElapsedTime(Volatile.Read(ref _lastUsedTimestamp));

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        CheckPermitCount(permitCount);
        MarkUsed();

        // Only the memory store answers without waiting, which keeps the synchronous path the rate limiting middleware tries first. Otherwise nothing is
        // counted here: the middleware then acquires asynchronously, which counts the request once.
        if (_memoryStore is null)
            return NotCountedLease;

        var window = GetWindow();
        long count;

        try
        {
            count = permitCount is 0 ?
                ParseCount(_memoryStore.Get(window.Key)) :
                _memoryStore.Increment(window.Key, permitCount, window.KeyLifetime);
        }
        catch (ServiceUnavailableApiException ex)
        {
            return new Lease(false, ex.RetryAfter, ex);
        }

        return IsWithinLimit(count, permitCount) ? AcquiredLease : Rejected(window.Remaining);
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        CheckPermitCount(permitCount);
        bool queued = false;

        try
        {
            while (true)
            {
                MarkUsed();
                var window = GetWindow();
                long count;

                try
                {
                    count = permitCount is 0 ?
                        ParseCount(await _store.GetAsync(window.Key, cancellationToken)) :
                        await _store.IncrementAsync(window.Key, permitCount, window.KeyLifetime, cancellationToken);
                }
                catch (ServiceUnavailableApiException ex)
                {
                    return new Lease(false, ex.RetryAfter, ex);
                }

                if (IsWithinLimit(count, permitCount))
                    return AcquiredLease;

                // Requests over the limit wait for the next window while there is room in this server's queue, and are counted again when it starts.
                if (!queued)
                {
                    if (Interlocked.Add(ref _queuedPermits, permitCount) > _queueLimit)
                    {
                        Interlocked.Add(ref _queuedPermits, -permitCount);
                        return Rejected(window.Remaining);
                    }

                    queued = true;
                }

                await Task.Delay(window.Remaining, _timeProvider, cancellationToken);
            }
        }
        finally
        {
            if (queued)
                Interlocked.Add(ref _queuedPermits, -permitCount);

            MarkUsed();
        }
    }

    private Lease Rejected(TimeSpan retryAfter) => new(false, retryAfter, rejectionMessage: _rejectionMessage);

    private void CheckPermitCount(int permitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(permitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _permitLimit);
    }

    // A count of 0 only checks whether a permit is left.
    private bool IsWithinLimit(long count, int permitCount) => permitCount is 0 ? count < _permitLimit : count <= _permitLimit;

    private static long ParseCount(string? value)
    {
        return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long count) ? count : 0;
    }

    private void MarkUsed() => Volatile.Write(ref _lastUsedTimestamp, _timeProvider.GetTimestamp());

    private FixedWindow GetWindow() => FixedWindow.Get(_keyPrefix, _windowTicks, _timeProvider);

    private sealed class Lease(bool isAcquired, TimeSpan? retryAfter, ApiException? error = null, string? rejectionMessage = null) : RateLimitLease
    {
        public override bool IsAcquired => isAcquired;

        public override IEnumerable<string> MetadataNames {
            get {
                if (retryAfter is not null)
                    yield return MetadataName.RetryAfter.Name;

                if (error is not null)
                    yield return ErrorMetadata.Name;

                if (rejectionMessage is not null)
                    yield return RejectionMessageMetadata.Name;
            }
        }

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is { } value && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = value;
                return true;
            }

            if (error is not null && metadataName == ErrorMetadata.Name)
            {
                metadata = error;
                return true;
            }

            if (rejectionMessage is not null && metadataName == RejectionMessageMetadata.Name)
            {
                metadata = rejectionMessage;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
