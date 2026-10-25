using System.Threading.Channels;

namespace Singulink.Net.Http.Api.Service.Uploads;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// A run of an upload's handler on this server, which the completion requests for the upload on this server attach to: the items it has produced, the
/// token that stops it, and its end once it has one.
/// </summary>
internal sealed class UploadRun : IDisposable
{
    // How many events a completion request that falls behind keeps.
    private const int MaxQueuedEvents = 16;

    // The longest delay of a timer.
    private static readonly TimeSpan MaxLimit = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly Lock _lock = new();
    private readonly List<Channel<RunEvent>> _subscribers = [];
    private readonly CancellationToken _stopping;
    private readonly CancellationTokenSource _limit;
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationTokenSource _renewal = new();
    private RunCancelReason _cancelReason;
    private byte[]? _latestItem;
    private RunEnd? _end;
    private bool _isDisposed;
    private long _renewedTimestamp;

    public UploadRun(IUploadRunClaim claim, CancellationToken stopping, TimeSpan limit, TimeProvider timeProvider)
    {
        Claim = claim;
        _renewedTimestamp = timeProvider.GetTimestamp();
        _stopping = stopping;
        _limit = new CancellationTokenSource(limit < MaxLimit ? limit : MaxLimit, timeProvider);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping, _limit.Token);
    }

    public IUploadRunClaim Claim { get; }

    /// <summary>
    /// Gets or sets the timestamp of the time provider from after the claim was last made or renewed, so that its lease is never taken to run out before
    /// it does.
    /// </summary>
    public long RenewedTimestamp
    {
        get => Interlocked.Read(ref _renewedTimestamp);
        set => Interlocked.Exchange(ref _renewedTimestamp, value);
    }

    /// <summary>
    /// Gets the token of the handler, which is cancelled when the upload is aborted, when the claim is lost, when the run reaches its time limit or when the
    /// application stops.
    /// </summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>
    /// Gets the token that stops the renewals of the claim.
    /// </summary>
    public CancellationToken RenewalToken => _renewal.Token;

    /// <summary>
    /// Gets why <see cref="Token"/> was cancelled, or <see cref="RunCancelReason.None"/> if it was not.
    /// </summary>
    public RunCancelReason CancelReason
    {
        get {
            lock (_lock)
            {
                if (_cancelReason is not RunCancelReason.None || !_cancellation.IsCancellationRequested)
                    return _cancelReason;

                return _stopping.IsCancellationRequested ? RunCancelReason.Stopping : RunCancelReason.TimeLimit;
            }
        }
    }

    /// <summary>
    /// Gets the JSON of the latest item, or <see langword="null"/> if there is none yet.
    /// </summary>
    public byte[]? LatestItem
    {
        get {
            lock (_lock)
                return _latestItem;
        }
    }

    /// <summary>
    /// Cancels the handler's token for the specified reason, unless it was cancelled already or the run has ended and was disposed. Exceptions of the
    /// token's callbacks are thrown.
    /// </summary>
    public void Cancel(RunCancelReason reason)
    {
        lock (_lock)
        {
            if (_isDisposed || _cancellation.IsCancellationRequested)
                return;

            _cancelReason = reason;
        }

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended and was disposed in the meantime.
        }
    }

    public void StopRenewing() => _renewal.Cancel();

    /// <summary>
    /// Publishes an item to the completion requests that are attached.
    /// </summary>
    public void Publish(byte[] itemJson)
    {
        lock (_lock)
        {
            _latestItem = itemJson;

            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(new RunEvent(itemJson, null));
        }
    }

    /// <summary>
    /// Ends the run, which ends the completion requests that are attached.
    /// </summary>
    public void End(RunEnd end)
    {
        lock (_lock)
        {
            _end = end;

            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(new RunEvent(null, end));
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    /// <summary>
    /// Attaches a completion request to the run: it gets the latest item, and then either the run's end, if it has ended, or the items that follow and the
    /// end.
    /// </summary>
    public RunSubscription Subscribe()
    {
        lock (_lock)
        {
            if (_end is not null)
                return new RunSubscription(this, _latestItem, _end, null);

            // A request whose client reads slower than the handler produces items skips the oldest ones it has not sent, as a request that attaches to the
            // run starts with the latest item. The end can push out an item too, but never the last one, which comes right before it.
            var channel = Channel.CreateBounded<RunEvent>(new BoundedChannelOptions(MaxQueuedEvents) {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

            _subscribers.Add(channel);

            return new RunSubscription(this, _latestItem, null, channel);
        }
    }

    public void Dispose()
    {
        lock (_lock)
            _isDisposed = true;

        _cancellation.Dispose();
        _limit.Dispose();
        _renewal.Dispose();
    }

    internal void Unsubscribe(Channel<RunEvent> channel)
    {
        lock (_lock)
            _subscribers.Remove(channel);
    }
}

/// <summary>
/// Specifies why the token of a run was cancelled.
/// </summary>
internal enum RunCancelReason
{
    None,
    Aborted,
    ClaimLost,
    TimeLimit,
    Stopping,
}

/// <summary>
/// An item or the end of a run.
/// </summary>
internal sealed record RunEvent(byte[]? Item, RunEnd? End);

/// <summary>
/// A completion request's attachment to a run.
/// </summary>
internal sealed class RunSubscription(UploadRun run, byte[]? latestItem, RunEnd? end, Channel<RunEvent>? channel) : IDisposable
{
    public byte[]? LatestItem { get; } = latestItem;

    /// <summary>
    /// Gets the end of the run, if it had ended when the request attached to it.
    /// </summary>
    public RunEnd? End { get; } = end;

    public ChannelReader<RunEvent>? Events => channel?.Reader;

    public void Dispose()
    {
        if (channel is not null)
            run.Unsubscribe(channel);
    }
}

/// <summary>
/// How a run ended.
/// </summary>
internal sealed class RunEnd
{
    /// <summary>
    /// Gets the upload's final outcome, if the run ended with one.
    /// </summary>
    public UploadOutcome? Outcome { get; init; }

    /// <summary>
    /// Gets the error of a run that ended without an outcome, after which the upload has all its chunks again and the handler runs at the next completion
    /// request: a handler that reported that it cannot run at the moment (a <c>503</c>), or a run whose handler could not be started.
    /// </summary>
    public StreamErrorInfo? RetryError { get; init; }

    /// <summary>
    /// Gets a value indicating whether the run ended before the handler was called, because the completion request's parameters could not be bound, after
    /// which the upload has all its chunks again. Other completion requests attached to the run start again.
    /// </summary>
    public bool IsNotStarted { get; init; }

    /// <summary>
    /// Gets a value indicating whether the upload was aborted while the handler ran.
    /// </summary>
    public bool IsAborted { get; init; }

    /// <summary>
    /// Gets the error of the handler or of its parameters that the request that ran the handler reports, as it was mapped (see
    /// <see cref="IApiExceptionHandler"/>), if the run ended with one.
    /// </summary>
    public ApiException? RunnerError { get; init; }
}
