using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
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
/// The rate limit checks the options when it takes them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>. <see cref="ExemptNetworks"/> is then read-only, and changing it throws a <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// The client address is <see cref="ConnectionInfo.RemoteIpAddress"/>, so services behind a proxy must apply the forwarded client address before rate
/// limiting runs. A request from a private or loopback address with an <c>X-Forwarded-For</c>, <c>Forwarded</c>, <c>X-Real-IP</c> or
/// <c>CF-Connecting-IP</c> header is logged as a warning, once for each limit. IPv6 clients are limited per /64 network, since a single client usually
/// controls a whole /64. <see cref="GetClientKey"/> returns the key that a client address is counted under.
/// </para>
/// <para>
/// Clients in <see cref="ExemptNetworks"/> are never limited, and neither are private addresses and connections without a remote IP address when
/// <see cref="ExemptPrivateNetworks"/> is <see langword="true"/>.
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
    private const string PermitLimitNotSet = $"{nameof(ClientRateLimitOptions)}.{nameof(PermitLimit)} is not set. It is required, and has no default.";

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientRateLimitOptions"/> class.
    /// </summary>
    public ClientRateLimitOptions()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientRateLimitOptions"/> class for a limit: configures it with the shared configuration of the limits
    /// and the limit's own, and freezes it, which checks that the required values are set.
    /// </summary>
    [SetsRequiredMembers]
    internal ClientRateLimitOptions(ClientRateLimits limits, Action<ClientRateLimitOptions> configure)
    {
        limits.ConfigureShared(this);
        configure(this);
        Freeze();
    }

    /// <summary>
    /// Gets or sets a value indicating whether requests are limited. When <see langword="false"/>, no request is limited. Default is
    /// <see langword="true"/>.
    /// </summary>
    public bool Enabled {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    } = true;

    /// <summary>
    /// Gets or sets the maximum number of requests a client can make in each window. Must be at least 1. It is required and has no default.
    /// </summary>
    /// <remarks>
    /// It reads as 0 while it is not set. Setting 0 while it is not set leaves it not set, which validation reports.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, or zero other than while it is not set.</exception>
    public required int PermitLimit {
        get => field is 0 && IsFrozen ? throw new InvalidOperationException(PermitLimitNotSet) : field;
        set {
            EnsureNotFrozen();

            // The binder of a configuration that lacks the value writes back what it read, which leaves the value as it was.
            if (value == field)
                return;

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the length of the window that <see cref="PermitLimit"/> applies to. Must be greater than zero. Default is 1 minute.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan Window {
        get;
        set {
            EnsureNotFrozen();
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
    public string? RejectionMessage {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of requests from a client that wait for the next window once the limit is reached, instead of being rejected.
    /// Must be at least zero. Default is 0 (requests over the limit are rejected immediately).
    /// </summary>
    /// <remarks>
    /// The queue is kept on each server, and waiting requests try again in no particular order when each window starts.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int QueueLimit {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether clients with private, loopback, link-local, shared (<c>100.64.0.0/10</c>) or unique local IPv6 addresses,
    /// and connections without a remote IP address (such as Unix domain sockets), are exempt from the limit. Default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// IPv4-mapped IPv6 addresses are treated as the IPv4 address they map.
    /// </remarks>
    public bool ExemptPrivateNetworks {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    }

    /// <summary>
    /// Gets the additional addresses and networks whose clients are exempt from the limit, such as an office's public IP address. Each entry is an IP
    /// address (for example <c>203.0.113.7</c>) or a network in CIDR notation (for example <c>2001:db8:1234::/48</c>).
    /// </summary>
    /// <remarks>
    /// Invalid entries cause a <see cref="FormatException"/> when the limit is added.
    /// </remarks>
    public IList<string> ExemptNetworks { get; private set; } = [];

    /// <summary>
    /// Gets a value indicating whether the options are in use: the rate limit checked them (see <see cref="Validate"/>) when it took them, and they can no
    /// longer be changed.
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Gets the key that the per-client limits count requests from the specified client address under.
    /// </summary>
    /// <param name="address">The client address (<see cref="ConnectionInfo.RemoteIpAddress"/>), or <see langword="null"/> for a connection without a
    /// remote IP address.</param>
    /// <returns>
    /// An IPv4 address as is, the IPv4 address that an IPv4-mapped IPv6 address maps, the network address of the /64 network that any other IPv6 address
    /// is in (such as <c>2001:db8:1:2::</c> for <c>2001:db8:1:2::1</c>), or <see cref="IPAddress.Any"/> (<c>0.0.0.0</c>) for <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// Requests from addresses with the same key share a count. Exemptions are not applied: exempt addresses (see <see cref="ExemptPrivateNetworks"/> and
    /// <see cref="ExemptNetworks"/>) get a key like any other.
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

    /// <summary>
    /// Checks the options as the rate limit does when it takes them. The required <see cref="PermitLimit"/> is set.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    public void Validate()
    {
        var problems = GetProblems();

        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    /// <summary>
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when the rate limit takes them. Options that are frozen
    /// already stay as they are.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    internal void Freeze()
    {
        if (IsFrozen)
            return;

        Validate();
        ExemptNetworks = new ReadOnlyCollection<string>([.. ExemptNetworks]);
        IsFrozen = true;
    }

    /// <summary>
    /// Gets the problems of the options, which <see cref="Validate"/> reports.
    /// </summary>
    internal List<string> GetProblems()
    {
        return PermitLimit is 0 ? [PermitLimitNotSet] : [];
    }

    private void EnsureNotFrozen()
    {
        if (IsFrozen)
            throw new InvalidOperationException($"The {nameof(ClientRateLimitOptions)} are in use, so they can no longer be changed.");
    }
}
