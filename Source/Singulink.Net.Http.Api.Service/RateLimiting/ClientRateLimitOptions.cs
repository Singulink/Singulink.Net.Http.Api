using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.RateLimiting;

namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for a fixed window rate limit that applies to each client address separately.
/// </summary>
/// <remarks>
/// <para>
/// The client address is <see cref="ConnectionInfo.RemoteIpAddress"/>, so services behind a proxy must apply the forwarded client address before rate
/// limiting runs. A request from a private or loopback address with an <c>X-Forwarded-For</c>, <c>Forwarded</c>, <c>X-Real-IP</c> or
/// <c>CF-Connecting-IP</c> header is logged as a warning, once for each limit. IPv6 clients are limited per /64 network, since a single client usually
/// controls a whole /64. <see cref="GetClientKey"/> returns the key that a client address is counted under.
/// </para>
/// <para>
/// Clients in <see cref="ExemptNetworks"/> are never limited, and neither are private addresses and connections without a remote IP address when <see
/// cref="ExemptPrivateNetworks"/> is <see langword="true"/>.
/// </para>
/// <para>
/// Requests are counted in the registered <see cref="IShortLivedStore"/>, or in one <see cref="MemoryShortLivedStore"/> that all the limits of the app share
/// if none is registered, so with a store that all of a service's servers share, a client's limit holds across all of them. Windows start at fixed times
/// on the clock (a one-minute window starts at every full minute), so that all servers count the same window.
/// </para>
/// <para>
/// The options can be bound from configuration, for example a section with <c>"ExemptNetworks": ["203.0.113.7", "2001:db8:1234::/48"]</c>.
/// </para>
/// </remarks>
public sealed class ClientRateLimitOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether requests are limited. When <see langword="false"/>, no request is limited. Default is
    /// <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of requests a client can make in each window. Must be set to a positive value before the limit is added.
    /// </summary>
    public int PermitLimit {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the length of the window that <see cref="PermitLimit"/> applies to. Default is 1 minute.
    /// </summary>
    public TimeSpan Window {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the message of the error that requests rejected by this limit receive, or <see langword="null"/> for the message of the rejection
    /// response (see <see cref="RateLimiterOptionsExtensions.SetApiRejectionResponse(RateLimiterOptions, string)"/>). Default is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A rejection response that is set with a function (see
    /// <see cref="RateLimiterOptionsExtensions.SetApiRejectionResponse(RateLimiterOptions, Func{RateLimitRejectionContext, string})"/>) passes this value to
    /// the function, which returns the message.
    /// </remarks>
    public string? RejectionMessage { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of requests from a client that wait for the next window once the limit is reached, instead of being rejected.
    /// The queue is kept on each server, and waiting requests try again in no particular order when each window starts. Default is 0 (requests over the
    /// limit are rejected immediately).
    /// </summary>
    public int QueueLimit {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether clients with private, loopback, link-local, shared (<c>100.64.0.0/10</c>) or unique local IPv6 addresses,
    /// and connections without a remote IP address (such as Unix domain sockets), are exempt from the limit. IPv4-mapped IPv6 addresses are treated as the
    /// IPv4 address they map. Default is <see langword="false"/>.
    /// </summary>
    public bool ExemptPrivateNetworks { get; set; }

    /// <summary>
    /// Gets the additional addresses and networks whose clients are exempt from the limit, such as an office's public IP address. Each entry is an IP
    /// address (for example <c>203.0.113.7</c>) or a network in CIDR notation (for example <c>2001:db8:1234::/48</c>).
    /// </summary>
    /// <remarks>
    /// Invalid entries cause a <see cref="FormatException"/> when the limit is added.
    /// </remarks>
    public IList<string> ExemptNetworks { get; } = [];

    /// <summary>
    /// Gets the key that the per-client limits count requests from the specified client address under. Requests from addresses with the same key share
    /// a count.
    /// </summary>
    /// <param name="address">The client address (<see cref="ConnectionInfo.RemoteIpAddress"/>), or <see langword="null"/> for a connection without a
    /// remote IP address.</param>
    /// <returns>
    /// An IPv4 address as is, the IPv4 address that an IPv4-mapped IPv6 address maps, the network address of the /64 network that any other IPv6 address
    /// is in (such as <c>2001:db8:1:2::</c> for <c>2001:db8:1:2::1</c>), or <see cref="IPAddress.Any"/> (<c>0.0.0.0</c>) for <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// Exemptions are not applied: exempt addresses (see <see cref="ExemptPrivateNetworks"/> and <see cref="ExemptNetworks"/>) get a key like any other.
    /// </remarks>
    public static IPAddress GetClientKey(IPAddress? address)
    {
        if (address is null)
            return IPAddress.Any;

        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        if (address.AddressFamily is not AddressFamily.InterNetworkV6)
            return address;

        // A single IPv6 client usually controls a whole /64 network (and can pick any address in it), so clients are limited per /64.
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();

        return new IPAddress(bytes);
    }
}
