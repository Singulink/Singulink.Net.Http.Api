namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Serves Cloudflare's IP range lists in place of the real endpoints. The lists and failure behavior can be changed while a host is running.
/// </summary>
public sealed class FakeCloudflareHandler : HttpMessageHandler
{
    public const string DefaultIpv4Ranges = """
        # Comments and blank lines are skipped

        173.245.48.0/20
        103.21.244.0/22
        """;

    public const string DefaultIpv6Ranges = "2400:cb00::/32\n2606:4700::/32\n";

    private int _requestCount;

    public string Ipv4Ranges { get; set; } = DefaultIpv4Ranges;

    public string Ipv6Ranges { get; set; } = DefaultIpv6Ranges;

    /// <summary>
    /// Gets or sets a value indicating whether requests fail with a 503 response.
    /// </summary>
    public bool Fail { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether requests never complete (until they are canceled).
    /// </summary>
    public bool Hang { get; set; }

    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);

        if (Hang)
            await Task.Delay(Timeout.Infinite, cancellationToken);

        if (Fail)
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("<html>Service unavailable</html>") };

        string? content = request.RequestUri?.AbsoluteUri switch {
            "https://www.cloudflare.com/ips-v4/" => Ipv4Ranges,
            "https://www.cloudflare.com/ips-v6/" => Ipv6Ranges,
            _ => null,
        };

        if (content is null)
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
    }
}
