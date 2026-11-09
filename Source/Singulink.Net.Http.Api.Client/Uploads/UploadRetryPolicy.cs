using System.Net;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Decides whether a failed request of an upload is made again and how long to wait before it, and waits. Failures are retried until no request of the
/// upload has succeeded for the retry timeout.
/// </summary>
internal sealed class UploadRetryPolicy
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    // Task.Delay and timers take at most this many milliseconds.
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(int.MaxValue);

#if !NET
    private static readonly Random SharedRandom = new();
#endif

    private readonly TimeSpan _retryTimeout;
    private readonly TimeProvider _timeProvider;
    private long _lastSuccessTimestamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadRetryPolicy"/> class whose retry timeout counts from now until a request succeeds.
    /// </summary>
    public UploadRetryPolicy(TimeSpan retryTimeout, TimeProvider timeProvider)
    {
        _retryTimeout = retryTimeout;
        _timeProvider = timeProvider;
        _lastSuccessTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Gets the time provider that measures the retry timeout and the waits.
    /// </summary>
    public TimeProvider TimeProvider => _timeProvider;

    /// <summary>
    /// Gets a value indicating whether a request that failed with the exception can be made again: a failure that <see cref="ApiFailures.IsTransient"/>
    /// counts as transient, a 408 (Request Timeout), which the service sends for a request body that arrives too slowly, and a 429 (Too Many Requests)
    /// that says how long to wait. An upload whose handler was interrupted (<see cref="UploadErrorCodes.Interrupted"/>) has failed for good.
    /// </summary>
    public static bool IsRetryable(Exception exception) => exception switch {
        ApiException { ErrorCode: UploadErrorCodes.Interrupted } => false,
        ApiException { StatusCode: HttpStatusCode.RequestTimeout } => true,
        TooManyRequestsApiException { RetryAfter: not null } => true,
        _ => ApiFailures.IsTransient(exception),
    };

    /// <summary>
    /// Gets how long to wait before the attempt that follows the specified number of failed attempts of a request: 1 second after the first failure,
    /// doubling up to 30 seconds, each reduced by up to half at random so that clients that failed together don't retry together.
    /// </summary>
    /// <param name="failedAttempts">The number of failed attempts, from 1.</param>
    /// <param name="random">A random value from 0 (inclusive) to 1 (exclusive).</param>
    public static TimeSpan GetBackoff(int failedAttempts, double random)
    {
        long backoffTicks = Math.Min(MinBackoff.Ticks << Math.Min(failedAttempts - 1, 30), MaxBackoff.Ticks);
        return TimeSpan.FromTicks((long)(backoffTicks * (1 - (random / 2))));
    }

    /// <summary>
    /// Gets the backoff after the specified number of failed attempts, reduced at random (see <see cref="GetBackoff(int, double)"/>).
    /// </summary>
    public TimeSpan NextBackoff(int failedAttempts) => GetBackoff(failedAttempts, NextRandom());

    /// <summary>
    /// Records that a request of the upload succeeded, which starts the retry timeout again.
    /// </summary>
    public void OnSuccess() => Interlocked.Exchange(ref _lastSuccessTimestamp, _timeProvider.GetTimestamp());

    /// <summary>
    /// Gets how long to wait before making a request again that failed with the exception, or <see langword="null"/> if it must not be made again: the
    /// failure is not retryable (see <see cref="IsRetryable(Exception)"/>), or the wait would end after the retry timeout. The wait is the backoff (see
    /// <see cref="GetBackoff(int, double)"/>), or the failure's <see cref="ApiException.RetryAfter"/> if it has one that is not zero, raised at random (see
    /// <see cref="GetRetryAfterWait(TimeSpan, double)"/>). A retry time that would end after the retry timeout is not waited for; one that fits is waited for,
    /// raised at random but never past the timeout.
    /// </summary>
    /// <param name="exception">The exception that the request failed with.</param>
    /// <param name="failedAttempts">The number of failed attempts of the request, from 1.</param>
    public TimeSpan? GetRetryDelay(Exception exception, int failedAttempts)
    {
        if (!IsRetryable(exception))
            return null;

        if (exception is not ApiException { RetryAfter: { } retryAfter } || retryAfter <= TimeSpan.Zero)
            return WithinRetryTimeout(NextBackoff(failedAttempts));

        if (WithinRetryTimeout(retryAfter) is not { } delay)
            return null;

        var wait = GetRetryAfterWait(delay, NextRandom());
        var timeLeft = GetTimeLeft();

        return wait < timeLeft ? wait : timeLeft;
    }

    /// <summary>
    /// Gets how long to wait for a retry time that a failure has: the retry time raised by up to half at random, so that clients that a service told to
    /// come back together don't come back together.
    /// </summary>
    /// <param name="retryAfter">The retry time.</param>
    /// <param name="random">A random value from 0 (inclusive) to 1 (exclusive).</param>
    public static TimeSpan GetRetryAfterWait(TimeSpan retryAfter, double random) => TimeSpan.FromTicks((long)(retryAfter.Ticks * (1 + (random / 2))));

    /// <summary>
    /// Gets how long to wait before making a request again after the specified number of failed attempts, whatever it failed with: the backoff (see
    /// <see cref="GetBackoff(int, double)"/>), or <see langword="null"/> if the wait would end after the retry timeout. Its only use is a direct upload that
    /// failed without a response, which is made again once whatever it failed with.
    /// </summary>
    /// <param name="failedAttempts">The number of failed attempts of the request, from 1.</param>
    public TimeSpan? GetRetryDelay(int failedAttempts) => WithinRetryTimeout(NextBackoff(failedAttempts));

    /// <summary>
    /// Waits for the specified time, measured with the time provider.
    /// </summary>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
#if NET
        return Task.Delay(delay, _timeProvider, cancellationToken);
#else
        return _timeProvider.Delay(delay, cancellationToken);
#endif
    }

    private static double NextRandom()
    {
#if NET
        return Random.Shared.NextDouble();
#else
        lock (SharedRandom)
            return SharedRandom.NextDouble();
#endif
    }

    /// <summary>
    /// Gets the time until the retry timeout ends, at most the longest wait of a timer.
    /// </summary>
    private TimeSpan GetTimeLeft()
    {
        if (_retryTimeout == Timeout.InfiniteTimeSpan)
            return MaxWait;

        var timeLeft = _retryTimeout - _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastSuccessTimestamp));
        return timeLeft < MaxWait ? timeLeft : MaxWait;
    }

    /// <summary>
    /// Gets a wait, at most the longest wait of a timer, or <see langword="null"/> if it would end after the retry timeout.
    /// </summary>
    private TimeSpan? WithinRetryTimeout(TimeSpan delay)
    {
        if (delay > MaxWait)
            delay = MaxWait;

        if (_retryTimeout != Timeout.InfiniteTimeSpan && _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastSuccessTimestamp)) + delay > _retryTimeout)
            return null;

        return delay;
    }
}
