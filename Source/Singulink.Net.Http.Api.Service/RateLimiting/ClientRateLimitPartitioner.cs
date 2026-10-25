using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Partitions requests by client address for a fixed window rate limit, with exempt clients not limited. Requests are counted in the registered
/// <see cref="IShortLivedStore"/>, or in a memory store that the limits of the same <see cref="ClientRateLimits"/> share if none is registered.
/// </summary>
internal sealed partial class ClientRateLimitPartitioner
{
    // Shared by all exempt requests so that they do not each create a partition. Never a real client address, so never a client key.
    private static readonly IPAddress ExemptKey = IPAddress.None;

    // Headers that proxies add with the client's address.
    private static readonly string[] ForwardingHeaders = ["X-Forwarded-For", "Forwarded", "X-Real-IP", "CF-Connecting-IP"];

    private readonly string _keyPrefix;
    private readonly bool _isGlobal;
    private readonly bool _enabled;
    private readonly bool _exemptPrivateNetworks;
    private readonly IPNetwork[] _exemptNetworks;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly int _queueLimit;
    private readonly string? _rejectionMessage;
    private readonly ClientRateLimits _limits;
    private readonly Func<IPAddress, RateLimiter> _createLimiter;

    private int _forwardingWarningLogged;

    private ClientRateLimitPartitioner(ClientRateLimitOptions options, string keyPrefix, bool isGlobal, ClientRateLimits limits)
    {
        if (options.PermitLimit is 0)
        {
            throw new InvalidOperationException(
                $"{nameof(ClientRateLimitOptions)}.{nameof(ClientRateLimitOptions.PermitLimit)} must be set to a positive value.");
        }

        _keyPrefix = keyPrefix;
        _isGlobal = isGlobal;
        _enabled = options.Enabled;
        _exemptPrivateNetworks = options.ExemptPrivateNetworks;
        _exemptNetworks = options.ExemptNetworks.Select(ParseNetwork).ToArray();
        _permitLimit = options.PermitLimit;
        _window = options.Window;
        _queueLimit = options.QueueLimit;
        _rejectionMessage = options.RejectionMessage;
        _limits = limits;
        _createLimiter = CreateLimiter;
    }

    /// <summary>
    /// Creates a partitioner for a limit of the specified rate limiter options, whose counts are kept under the specified key prefix, which must be unique to
    /// the limit. The options of the limit are configured with the shared configuration of the rate limiter options first.
    /// </summary>
    public static ClientRateLimitPartitioner Create(
        RateLimiterOptions rateLimiterOptions, Action<ClientRateLimitOptions> configure, string keyPrefix, bool isGlobal)
    {
        var limits = ClientRateLimits.Get(rateLimiterOptions);
        var options = new ClientRateLimitOptions();

        limits.ConfigureShared(options);
        configure(options);

        return new ClientRateLimitPartitioner(options, keyPrefix, isGlobal, limits);
    }

    public RateLimitPartition<IPAddress> GetPartition(HttpContext context)
    {
        if (!_enabled)
            return RateLimitPartition.GetNoLimiter(ExemptKey);

        var address = context.Connection.RemoteIpAddress;

        if (address is { IsIPv4MappedToIPv6: true })
            address = address.MapToIPv4();

        bool isPrivate = address is not null && IsPrivate(address);

        if (isPrivate && Volatile.Read(ref _forwardingWarningLogged) is 0)
            WarnIfForwarded(context, address!);

        if (IsExempt(address, isPrivate))
            return RateLimitPartition.GetNoLimiter(ExemptKey);

        // The store and clock are singletons, so the first request's services serve every later request.
        _limits.EnsureServices(context.RequestServices);

        return RateLimitPartition.Get(ClientRateLimitOptions.GetClientKey(address), _createLimiter);
    }

    private RateLimiter CreateLimiter(IPAddress clientKey)
    {
        var services = _limits.Services;
        string keyPrefix = $"{_keyPrefix}{clientKey}:";

        return new ClientFixedWindowRateLimiter(
            services.Store, services.TimeProvider, keyPrefix, _permitLimit, _window, _queueLimit, _rejectionMessage, countSynchronously: !_isGlobal);
    }

    private bool IsExempt(IPAddress? address, bool isPrivate)
    {
        if (address is null)
            return _exemptPrivateNetworks;

        if (_exemptPrivateNetworks && isPrivate)
            return true;

        foreach (var network in _exemptNetworks)
        {
            if (network.Contains(address))
                return true;
        }

        return false;
    }

    // A request from a private address with a forwarding header came through a proxy whose forwarded address was not applied, so the limit applies to
    // the proxy rather than to each client behind it.
    private void WarnIfForwarded(HttpContext context, IPAddress address)
    {
        var headers = context.Request.Headers;

        if (Array.Find(ForwardingHeaders, headers.ContainsKey) is not { } header || Interlocked.Exchange(ref _forwardingWarningLogged, 1) is not 0)
            return;

        if (context.RequestServices.GetService<ILoggerFactory>() is { } loggerFactory)
            LogForwardingNotApplied(loggerFactory.CreateLogger<ClientRateLimitPartitioner>(), address, header);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily is AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;

        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);

        return bytes switch {
            [10, ..] => true,                                // 10.0.0.0/8 (private)
            [172, >= 16 and <= 31, ..] => true,              // 172.16.0.0/12 (private)
            [192, 168, ..] => true,                          // 192.168.0.0/16 (private)
            [169, 254, ..] => true,                          // 169.254.0.0/16 (link-local)
            [100, >= 64 and <= 127, ..] => true,             // 100.64.0.0/10 (shared address space)
            _ => false,
        };
    }

    private static IPNetwork ParseNetwork(string value)
    {
        var span = value.AsSpan().Trim();

        if (IPNetwork.TryParse(span, out var network))
            return network;

        if (IPAddress.TryParse(span, out var address))
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            return new IPNetwork(address, address.AddressFamily is AddressFamily.InterNetworkV6 ? 128 : 32);
        }

        throw new FormatException(
            $"'{value}' in {nameof(ClientRateLimitOptions)}.{nameof(ClientRateLimitOptions.ExemptNetworks)} is not a valid IP address or network in CIDR " +
            "notation (for example 203.0.113.7 or 203.0.113.0/24).");
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A request from {RemoteAddress} has a {Header} header, so it most likely came through a proxy whose forwarded client address was not " +
            "applied. Rate limits then apply to the proxy's address rather than to each client. Apply the forwarded address before rate limiting runs, " +
            "for example with UseForwardedHeaders or UseCloudflareForwarding. This warning is logged once for each limit.")]
    private static partial void LogForwardingNotApplied(ILogger logger, IPAddress remoteAddress, string header);
}
