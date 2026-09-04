namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Specifies that the streaming response from the endpoint should automatically send a ping record (which requires no special client-side handling) whenever
/// no item has been produced for the specified interval, keeping the response connection alive.
/// </summary>
/// <remarks>
/// This is only supported on endpoints that return <see cref="IAsyncEnumerable{T}"/> results, which are converted into streaming responses by
/// <see cref="WebApplicationExtensions.UseApiResponseHandling{TBuilder}(TBuilder)"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class KeepAlivePingAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KeepAlivePingAttribute"/> class.
    /// </summary>
    /// <param name="intervalSeconds">The minimum time (in seconds) to wait for the next item before a ping record is sent to the client.</param>
    public KeepAlivePingAttribute(double intervalSeconds)
    {
        if (!(intervalSeconds > 0) || !double.IsFinite(intervalSeconds))
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "Ping interval must be a positive finite number of seconds.");

        Interval = TimeSpan.FromSeconds(intervalSeconds);
    }

    /// <summary>
    /// Gets the minimum time to wait for the next item before a ping record is sent to the client.
    /// </summary>
    public TimeSpan Interval { get; }
}
