namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// A timeout that runs while nothing happens: its token is cancelled once the timeout passes without <see cref="Reset"/> being called, measured with a time
/// provider.
/// </summary>
internal sealed class InactivityTimeout : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly ITimer? _timer;
    private volatile bool _hasExpired;

    /// <summary>
    /// Initializes a new instance of the <see cref="InactivityTimeout"/> class whose timeout starts now.
    /// </summary>
    /// <param name="timeout">The timeout. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> and values longer than <see cref="int.MaxValue"/>
    /// milliseconds mean no timeout.</param>
    /// <param name="timeProvider">The time provider that measures the timeout.</param>
    /// <param name="cancellationToken">A token whose cancellation cancels the timeout's token too.</param>
    public InactivityTimeout(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Timeout = timeout;

        if (timeout != System.Threading.Timeout.InfiniteTimeSpan && timeout.TotalMilliseconds <= int.MaxValue)
            _timer = timeProvider.CreateTimer(static state => ((InactivityTimeout)state!).Expire(), this, timeout, System.Threading.Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Timeout { get; }

    /// <summary>
    /// Gets the token that is cancelled when the timeout passes, or when the token that the timeout was created with is cancelled.
    /// </summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>
    /// Gets a value indicating whether the timeout passed.
    /// </summary>
    public bool HasExpired => _hasExpired;

    /// <summary>
    /// Starts the timeout again, after something happened. Does nothing once the timeout is disposed, such as for content that a handler writes on after
    /// its request has ended.
    /// </summary>
    public void Reset()
    {
        try
        {
            _timer?.Change(Timeout, System.Threading.Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // The request has ended.
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cancellation.Dispose();
    }

    private void Expire()
    {
        _hasExpired = true;

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request has ended.
        }
    }
}
