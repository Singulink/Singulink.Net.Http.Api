using System.Net;
using System.Net.Sockets;

namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for trusting the client address and scheme that Cloudflare forwards.
/// </summary>
public sealed class CloudflareForwardingOptions
{
    /// <summary>
    /// The name of the <see cref="HttpClient"/> (see <see cref="IHttpClientFactory"/>) used to download Cloudflare's IP ranges, so the configuration of
    /// that named client applies to the downloads.
    /// </summary>
    public const string HttpClientName = "Singulink.Net.Http.Api.Service.Cloudflare";

    /// <summary>
    /// Gets or sets the path of the file that Cloudflare's IP ranges are saved to after each download, and read from at startup when they cannot be
    /// downloaded. When <see cref="DownloadRanges"/> is <see langword="false"/>, the ranges are only read from this file. Relative paths are resolved
    /// against the application's content root. Required: the application fails to start if this is not set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If the file cannot be written, downloaded ranges are still used.
    /// </para>
    /// <para>
    /// The file can be created in advance, for example for a new server, a container image or a read-only deployment. It contains one IP range per line
    /// in CIDR notation, like the lists that Cloudflare publishes, and must end with a line that contains only <c># end</c>, which shows that the file is
    /// complete. Blank lines and other lines that start with <c>#</c> are ignored. A file without the end line is not used, so a file that is read while
    /// it is still being written is never partly trusted.
    /// </para>
    /// </remarks>
    public string? SavedRangesPath {
        get;
        set {
            if (value is not null)
                ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether Cloudflare's IP ranges are downloaded. Default is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see langword="false"/>, the ranges are never downloaded or saved. Instead, they are read from <see cref="SavedRangesPath"/> when the
    /// application starts and again every <see cref="RefreshInterval"/>, so changes to the file are picked up without a restart. The file must be provided
    /// and kept up to date by other means, for example by another server that downloads the ranges and saves them to a shared location.
    /// </para>
    /// <para>
    /// If the file cannot be read or is not a complete list, the ranges in use are kept and it is read again after <see cref="RetryInterval"/>. Until
    /// it is first read, every request is refused with a <c>503 Service Unavailable</c> response and it is read again every <see
    /// cref="InitialRetryInterval"/>. <see cref="DownloadTimeout"/> and <see cref="StaleWarningAge"/> have no effect.
    /// </para>
    /// </remarks>
    public bool DownloadRanges { get; set; } = true;

    /// <summary>
    /// Gets the addresses and networks whose connections are trusted like connections from Cloudflare's IP ranges: their forwarded client address and
    /// scheme are applied and their Cloudflare headers are kept. Each entry is an IP address (for example <c>127.0.0.1</c>) or a network in CIDR notation
    /// (for example <c>10.0.0.0/8</c>). Empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any client that connects from one of these addresses can send requests with a client address and Cloudflare headers of its choosing.
    /// </para>
    /// <para>
    /// IPv4-mapped IPv6 addresses and networks, such as <c>::ffff:10.0.0.0/104</c>, are treated as the IPv4 addresses and networks they map, such as
    /// <c>10.0.0.0/8</c>. The application fails to start if an entry is not a valid address or network.
    /// </para>
    /// </remarks>
    public IList<string> TrustedProxyNetworks { get; } = [];

    /// <summary>
    /// Gets or sets how often Cloudflare's IP ranges are downloaded again after a successful download, or read again from <see cref="SavedRangesPath"/>
    /// when <see cref="DownloadRanges"/> is <see langword="false"/>. Default is 1 day.
    /// </summary>
    public TimeSpan RefreshInterval {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets how long to wait before trying again after a download fails, or after reading the saved copy fails when <see cref="DownloadRanges"/>
    /// is <see langword="false"/>. <see cref="RefreshInterval"/> is used instead if it is shorter. Default is 5 minutes.
    /// </summary>
    /// <remarks>
    /// <see cref="InitialRetryInterval"/> applies instead while no ranges are available.
    /// </remarks>
    public TimeSpan RetryInterval {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets how long to wait before trying again while no ranges are available, which is when they could not be loaded at startup. Default is 10
    /// seconds.
    /// </summary>
    /// <remarks>
    /// Until the ranges are available, every request is refused with a <c>503 Service Unavailable</c> response that asks the client to retry after this
    /// interval.
    /// </remarks>
    public TimeSpan InitialRetryInterval {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets how long a download of the ranges may take before it is abandoned. This also bounds how long startup waits for the ranges. Default is 10
    /// seconds.
    /// </summary>
    public TimeSpan DownloadTimeout {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets how old the ranges in use can get before each failed download also logs a warning that they are out of date. Does not apply when <see
    /// cref="DownloadRanges"/> is <see langword="false"/>. Default is 7 days.
    /// </summary>
    public TimeSpan StaleWarningAge {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromDays(7);

    /// <summary>
    /// Parses an entry of <see cref="TrustedProxyNetworks"/>, or returns <see langword="false"/> if it is not a valid address or network.
    /// </summary>
    internal static bool TryParseNetwork(string value, out IPNetwork network)
    {
        var span = value.AsSpan().Trim();

        if (IPAddress.TryParse(span, out var address))
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            network = new IPNetwork(address, address.AddressFamily is AddressFamily.InterNetworkV6 ? 128 : 32);
            return true;
        }

        if (!IPNetwork.TryParse(span, out network))
            return false;

        // Connection addresses are mapped to IPv4 before they are checked, so a network of IPv4-mapped addresses is too. Its prefix covers at least the
        // 96 bits before the IPv4 address, or its base address would not be a mapped one.
        if (network.BaseAddress.IsIPv4MappedToIPv6)
            network = new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96);

        return true;
    }
}
