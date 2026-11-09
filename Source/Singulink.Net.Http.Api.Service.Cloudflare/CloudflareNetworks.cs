using System.Diagnostics.Metrics;
using System.Net;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The set of Cloudflare networks whose connections are trusted to forward the client address. Replaced as a whole on refresh, so requests always see a
/// complete set. It also keeps the state of the refreshes and the requests refused without networks, which the metrics and the health check report.
/// </summary>
internal sealed class CloudflareNetworks
{
    private IPNetwork[]? _networks;
    private long _rangesDateTicks;
    private long _lastRefreshTicks;
    private Exception? _lastRefreshError;
    private long _refusedRequests;

    private readonly Counter<long>? _refreshFailures;
    private readonly Counter<long>? _refusedRequestsCounter;

    public CloudflareNetworks(IServiceProvider services)
    {
        // The toolkit's meter, whose instruments the Service package creates.
        if (HttpApiMetrics.Get(services)?.Meter is not { } meter)
            return;

        meter.CreateObservableGauge("httpapi.cloudflare.range.count", () => (long)Count, "{range}", "Cloudflare's IP ranges in use.");
        meter.CreateObservableGauge(
            "httpapi.cloudflare.range.age",
            () => RangesDate is { } date ? [new Measurement<double>((DateTimeOffset.UtcNow - date).TotalSeconds)] : Array.Empty<Measurement<double>>(),
            "s",
            "Age of Cloudflare's IP ranges in use: since they were downloaded, or since the saved copy in use was written.");

        _refreshFailures = meter.CreateCounter<long>("httpapi.cloudflare.refresh.failures", "{refresh}", "Refreshes of Cloudflare's IP ranges that failed.");
        _refusedRequestsCounter = meter.CreateCounter<long>(
            "httpapi.cloudflare.refused_requests", "{request}", "Requests refused because Cloudflare's IP ranges were not loaded yet.");
    }

    /// <summary>
    /// Gets a value indicating whether the networks are available. They never become unavailable again once they are.
    /// </summary>
    public bool IsAvailable => Volatile.Read(ref _networks) is not null;

    /// <summary>
    /// Gets the number of networks in use.
    /// </summary>
    public int Count => Volatile.Read(ref _networks)?.Length ?? 0;

    /// <summary>
    /// Gets when the networks in use were downloaded, or when the saved copy in use was written, or <see langword="null"/> while there are none.
    /// </summary>
    public DateTimeOffset? RangesDate => Interlocked.Read(ref _rangesDateTicks) is var ticks and not 0 ? new(ticks, TimeSpan.Zero) : null;

    /// <summary>
    /// Gets when the last refresh ended, or <see langword="null"/> before the first one.
    /// </summary>
    public DateTimeOffset? LastRefreshUtc => Interlocked.Read(ref _lastRefreshTicks) is var ticks and not 0 ? new(ticks, TimeSpan.Zero) : null;

    /// <summary>
    /// Gets the exception of the last refresh, or <see langword="null"/> if it succeeded.
    /// </summary>
    public Exception? LastRefreshError => Volatile.Read(ref _lastRefreshError);

    /// <summary>
    /// Gets the number of requests refused because no networks were loaded, since the app started.
    /// </summary>
    public long RefusedRequests => Interlocked.Read(ref _refusedRequests);

    /// <summary>
    /// Puts the specified networks in use, of the specified date. Returns <see langword="false"/> without replacing them if they are the same as the
    /// networks in use, whose date is set all the same.
    /// </summary>
    public bool Set(IPNetwork[] networks, DateTimeOffset date)
    {
        Interlocked.Exchange(ref _rangesDateTicks, date.UtcTicks);

        if (Volatile.Read(ref _networks) is { } current && current.AsSpan().SequenceEqual(networks))
            return false;

        Volatile.Write(ref _networks, networks);
        return true;
    }

    /// <summary>
    /// Records the end of a refresh, with its exception if it failed.
    /// </summary>
    public void EndRefresh(DateTimeOffset utcNow, Exception? exception)
    {
        Volatile.Write(ref _lastRefreshError, exception);
        Interlocked.Exchange(ref _lastRefreshTicks, utcNow.UtcTicks);

        if (exception is not null)
            _refreshFailures?.Add(1);
    }

    /// <summary>
    /// Counts a request that was refused because no networks were loaded.
    /// </summary>
    public void AddRefusedRequest()
    {
        Interlocked.Increment(ref _refusedRequests);
        _refusedRequestsCounter?.Add(1);
    }

    public bool Contains(IPAddress address)
    {
        foreach (var network in Volatile.Read(ref _networks) ?? [])
        {
            if (network.Contains(address))
                return true;
        }

        return false;
    }
}
