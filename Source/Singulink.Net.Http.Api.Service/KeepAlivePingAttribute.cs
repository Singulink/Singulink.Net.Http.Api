namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Specifies that enumeration responses from the endpoint should automatically send a "ping" item (which requires no special client-side handling) whenever no
/// item has been produced for the specified interval, keeping the response connection alive.
/// </summary>
/// <remarks>
/// This is only supported on endpoints that return <see cref="IAsyncEnumerable{T}" /> results handled by the
/// <see cref="ResponseSideInfoEnumerationEndpointFilter" />, not <see cref="IEnumerable{T}" /> ones.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class KeepAlivePingAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KeepAlivePingAttribute"/> class.
    /// </summary>
    /// <param name="intervalSeconds">The minimum time (in seconds) to wait for the next item before a ping item is sent to the client.</param>
    public KeepAlivePingAttribute(double intervalSeconds)
    {
        if (!(intervalSeconds > 0) || !double.IsFinite(intervalSeconds))
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "Ping interval must be a positive finite number of seconds.");

        Interval = TimeSpan.FromSeconds(intervalSeconds);
    }

    /// <summary>
    /// Gets the minimum time to wait for the next item before a ping item is sent to the client.
    /// </summary>
    public TimeSpan Interval { get; }
}
