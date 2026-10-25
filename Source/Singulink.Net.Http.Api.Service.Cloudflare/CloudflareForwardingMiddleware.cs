using System.Net;
using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Applies the client address and scheme forwarded by Cloudflare to requests that come from Cloudflare's networks (or from trusted proxy networks), and
/// removes Cloudflare's headers from all other requests. Refuses all requests until Cloudflare's networks are available.
/// </summary>
internal sealed partial class CloudflareForwardingMiddleware
{
    private const string ConnectingIpHeader = "CF-Connecting-IP";
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";
    private const string CloudflareHeaderPrefix = "CF-";

    private const string UnavailableMessage = "The service is starting. Please try again shortly.";

    // The untrusted forwarding warning is logged once a window for each connection address, for up to this many addresses a window.
    private const int MaxWarnedAddresses = 100;

    private static readonly TimeSpan WarningWindow = TimeSpan.FromHours(1);

    private readonly RequestDelegate _next;
    private readonly CloudflareNetworks _networks;
    private readonly TimeSpan _retryAfter;
    private readonly IPNetwork[] _trustedProxyNetworks;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Lock _warningLock = new();
    private readonly HashSet<IPAddress> _warnedAddresses = [];

    private DateTimeOffset _warningWindowEnd;

    public CloudflareForwardingMiddleware(
        RequestDelegate next,
        CloudflareNetworks networks,
        IOptions<CloudflareForwardingOptions> options,
        TimeProvider timeProvider,
        ILogger<CloudflareForwardingMiddleware> logger)
    {
        _next = next;
        _networks = networks;
        _retryAfter = options.Value.InitialRetryInterval;
        _timeProvider = timeProvider;
        _logger = logger;

        // The options are validated at startup, so every entry can be parsed.
        _trustedProxyNetworks = options.Value.TrustedProxyNetworks
            .Select(entry => CloudflareForwardingOptions.TryParseNetwork(entry, out var network) ? network : throw new FormatException(entry))
            .ToArray();
    }

    public Task InvokeAsync(HttpContext context)
    {
        // Without the networks, requests through Cloudflare cannot be told apart from direct connections. Treating them as direct connections would make
        // every client appear to come from a few Cloudflare addresses (sharing per-client rate limits, for example), so nothing is served until then.
        if (!_networks.IsAvailable)
            return WriteUnavailableResponseAsync(context);

        var connection = context.Connection;
        var request = context.Request;
        var remoteAddress = connection.RemoteIpAddress;

        if (remoteAddress is { IsIPv4MappedToIPv6: true })
            remoteAddress = remoteAddress.MapToIPv4();

        if (remoteAddress is not null && IsTrusted(remoteAddress))
        {
            context.Features.Set(CloudflareRequestFeature.Instance);

            // Multiple header values are joined with commas and fail to parse, so only a single valid address is applied.
            if (IPAddress.TryParse(request.Headers[ConnectingIpHeader].ToString(), out var clientAddress))
                connection.RemoteIpAddress = clientAddress.IsIPv4MappedToIPv6 ? clientAddress.MapToIPv4() : clientAddress;

            string forwardedProto = request.Headers[ForwardedProtoHeader].ToString();

            if (forwardedProto.Equals("https", StringComparison.OrdinalIgnoreCase))
                request.Scheme = "https";
            else if (forwardedProto.Equals("http", StringComparison.OrdinalIgnoreCase))
                request.Scheme = "http";
        }
        else
        {
            if (remoteAddress is not null)
                WarnIfForwarded(request, remoteAddress);

            // Anyone reaching the service directly could send their own Cloudflare headers (such as the visitor's country), so they are only kept on
            // requests that came through Cloudflare.
            RemoveCloudflareHeaders(request.Headers);
        }

        return _next(context);
    }

    private bool IsTrusted(IPAddress address)
    {
        if (_networks.Contains(address))
            return true;

        foreach (var network in _trustedProxyNetworks)
        {
            if (network.Contains(address))
                return true;
        }

        return false;
    }

    // A direct connection that forwards another client's address most likely came through a proxy (such as Cloudflare Tunnel's cloudflared) whose
    // address is not trusted, so every request through it appears to come from the proxy.
    private void WarnIfForwarded(HttpRequest request, IPAddress remoteAddress)
    {
        if (!IPAddress.TryParse(request.Headers[ConnectingIpHeader].ToString(), out var clientAddress))
            return;

        if (clientAddress.IsIPv4MappedToIPv6)
            clientAddress = clientAddress.MapToIPv4();

        if (clientAddress.Equals(remoteAddress))
            return;

        var utcNow = _timeProvider.GetUtcNow();

        lock (_warningLock)
        {
            // Each window starts without warned addresses, so a proxy that starts forwarding later is reported too.
            if (utcNow >= _warningWindowEnd)
            {
                _warnedAddresses.Clear();
                _warningWindowEnd = utcNow + WarningWindow;
            }

            if (_warnedAddresses.Count >= MaxWarnedAddresses || !_warnedAddresses.Add(remoteAddress))
                return;
        }

        LogUntrustedForwarding(_logger, remoteAddress, clientAddress, MaxWarnedAddresses);
    }

    private Task WriteUnavailableResponseAsync(HttpContext context)
    {
        // Keeps Cloudflare (or any other cache) from serving the error after the service is ready.
        context.Response.Headers.CacheControl = "no-store";

        var exception = new ServiceUnavailableApiException(UnavailableMessage) {
            ErrorCode = ApiErrorCodes.CloudflareRangesUnavailable,
            RetryAfter = _retryAfter,
        };

        return context.Response.WriteApiErrorAsync(exception, context.RequestAborted);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A request from {RemoteAddress} has a CF-Connecting-IP header with another address ({ForwardedAddress}), but its connection is not " +
            "from Cloudflare's IP ranges or a trusted proxy network, so the forwarded address was not applied and the request's Cloudflare headers were " +
            "removed. Any client that connects directly can send this header. Requests that reach the service through a proxy of its own, such as " +
            "Cloudflare Tunnel's cloudflared, are only trusted when the proxy's address is in CloudflareForwardingOptions.TrustedProxyNetworks. This " +
            "warning is logged once an hour for each connection address, for up to {MaxAddresses} addresses an hour.")]
    private static partial void LogUntrustedForwarding(ILogger logger, IPAddress remoteAddress, IPAddress forwardedAddress, int maxAddresses);

    private static void RemoveCloudflareHeaders(IHeaderDictionary headers)
    {
        List<string>? names = null;

        foreach (var header in headers)
        {
            if (header.Key.StartsWith(CloudflareHeaderPrefix, StringComparison.OrdinalIgnoreCase))
                (names ??= []).Add(header.Key);
        }

        if (names is null)
            return;

        foreach (string name in names)
            headers.Remove(name);
    }
}
