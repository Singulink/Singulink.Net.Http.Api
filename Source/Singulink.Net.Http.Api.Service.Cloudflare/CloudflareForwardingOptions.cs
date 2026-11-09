using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Singulink.IO;

namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for trusting the client address and scheme that Cloudflare forwards.
/// </summary>
/// <remarks>
/// Cloudflare forwarding checks the options when it takes them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>. <see cref="TrustedProxyNetworks"/> is then read-only, and changing it throws a
/// <see cref="NotSupportedException"/>.
/// </remarks>
public sealed class CloudflareForwardingOptions
{
    /// <summary>
    /// The name of the <see cref="HttpClient"/> (see <see cref="IHttpClientFactory"/>) used to download Cloudflare's IP ranges, so the configuration of
    /// that named client applies to the downloads.
    /// </summary>
    public const string HttpClientName = "Singulink.Net.Http.Api.Service.Cloudflare";

    private const string SavedRangesFileNotSet =
        $"{nameof(CloudflareForwardingOptions)}.{nameof(SavedRangesFile)} is not set. It is required, and has no default: Cloudflare's IP ranges are saved " +
        "to this file and read from it when they can't be downloaded at startup, or when downloads are turned off. Use a location that survives restarts " +
        "and redeployments, such as a file in the app's data directory.";

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudflareForwardingOptions"/> class.
    /// </summary>
    public CloudflareForwardingOptions()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudflareForwardingOptions"/> class with the file of the saved ranges.
    /// </summary>
    /// <param name="savedRangesFile">The file of the saved ranges (see <see cref="SavedRangesFile"/>).</param>
    /// <exception cref="ArgumentException"><paramref name="savedRangesFile"/> is not a path with friendly names.</exception>
    [SetsRequiredMembers]
    public CloudflareForwardingOptions(IAbsoluteFilePath savedRangesFile)
    {
        CheckSavedRangesFile(savedRangesFile, nameof(savedRangesFile));
        SavedRangesFile = savedRangesFile;
    }

    /// <summary>
    /// Gets or sets the file that Cloudflare's IP ranges are saved to after each download, and read from at startup when they cannot be downloaded. It is
    /// required, has no default, and is <see langword="null"/> while it is not set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The application fails to start if it is still not set then. When <see cref="DownloadRanges"/> is <see langword="false"/>, the ranges are only read
    /// from this file. If the file cannot be written, downloaded ranges are still used.
    /// </para>
    /// <para>
    /// The file can be created in advance, for example for a new server, a container image or a read-only deployment. It contains one IP range per line
    /// in CIDR notation, like the lists that Cloudflare publishes, and must end with a line that contains only <c># end</c>, which shows that the file is
    /// complete. Blank lines and other lines that start with <c>#</c> are ignored. A file without the end line is not used, so a file that is read while
    /// it is still being written is never partly trusted.
    /// </para>
    /// <para>
    /// The file must be a path with friendly names, one that is valid for <see cref="PathOptions.NoUnfriendlyNames"/>, on every platform. A path bound from
    /// a configuration is read with <see cref="PathOptions.None"/>, so a typo such as a trailing space or dot would otherwise create a file or directory
    /// with that name, which many tools handle badly.
    /// </para>
    /// <para>
    /// The reflection-based configuration binder binds it from a string through the type converter of Singulink.IO.FileSystem, which reads it as a path of
    /// the platform the app runs on, with <see cref="PathOptions.None"/>, and the file then must have friendly names. The configuration binding source
    /// generator doesn't use type converters and leaves it unset, so an app that uses it sets the file in code, from a path that it parses, such as with
    /// <see cref="FilePath.ParseAbsolute(ReadOnlySpan{char}, PathOptions)"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not a path with friendly names. The message names the path and its problem.</exception>
    [MaybeNull]
    public required IAbsoluteFilePath SavedRangesFile {
        get => field ?? (IsFrozen ? throw new InvalidOperationException(SavedRangesFileNotSet) : null);
        set {
            EnsureNotFrozen();
            CheckSavedRangesFile(value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether Cloudflare's IP ranges are downloaded. Default is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see langword="false"/>, the ranges are never downloaded or saved. Instead, they are read from <see cref="SavedRangesFile"/> when the
    /// application starts and again every <see cref="RefreshInterval"/>, so changes to the file are picked up without a restart. The file must be provided
    /// and kept up to date by other means, for example by another server that downloads the ranges and saves them to a shared location.
    /// </para>
    /// <para>
    /// If the file cannot be read or is not a complete list, the ranges in use are kept and it is read again after <see cref="RetryInterval"/>. Until
    /// it is first read, every request is refused with a <c>503 Service Unavailable</c> response and it is read again every
    /// <see cref="InitialRetryInterval"/>. <see cref="DownloadTimeout"/> and <see cref="StaleWarningAge"/> have no effect.
    /// </para>
    /// </remarks>
    public bool DownloadRanges {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    } = true;

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
    public IList<string> TrustedProxyNetworks { get; private set; } = [];

    /// <summary>
    /// Gets or sets how often Cloudflare's IP ranges are downloaded again after a successful download, or read again from <see cref="SavedRangesFile"/>
    /// when <see cref="DownloadRanges"/> is <see langword="false"/>. Must be greater than zero. Default is 1 day.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan RefreshInterval {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets how long to wait before trying again after a download fails, or after reading the saved copy fails when <see cref="DownloadRanges"/>
    /// is <see langword="false"/>. Must be greater than zero. Default is 5 minutes.
    /// </summary>
    /// <remarks>
    /// <see cref="RefreshInterval"/> is used instead if it is shorter. <see cref="InitialRetryInterval"/> applies instead while no ranges are available.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan RetryInterval {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets how long to wait before trying again while no ranges are available, which is when they could not be loaded at startup. Must be
    /// greater than zero. Default is 10 seconds.
    /// </summary>
    /// <remarks>
    /// Until the ranges are available, every request is refused with a <c>503 Service Unavailable</c> response that asks the client to retry after this
    /// interval.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan InitialRetryInterval {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets how long a download of the ranges may take before it is abandoned. Must be greater than zero. Default is 10 seconds.
    /// </summary>
    /// <remarks>
    /// This also bounds how long startup waits for the ranges.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan DownloadTimeout {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets how old the ranges in use can get before each failed download also logs a warning that they are out of date. Does not apply when
    /// <see cref="DownloadRanges"/> is <see langword="false"/>. Must be greater than zero. Default is 7 days.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan StaleWarningAge {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets a value indicating whether the options are in use: Cloudflare forwarding checked them (see <see cref="Validate"/>) when it took them, and they can
    /// no longer be changed.
    /// </summary>
    [MemberNotNullWhen(true, nameof(SavedRangesFile))]
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as Cloudflare forwarding does when it takes them. The required <see cref="SavedRangesFile"/> is set, and every entry of
    /// <see cref="TrustedProxyNetworks"/> is an IP address or a network in CIDR notation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    [MemberNotNull(nameof(SavedRangesFile))]
    public void Validate()
    {
        var problems = GetProblems();

        if (problems.Count > 0 || SavedRangesFile is null)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

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

    /// <summary>
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when Cloudflare forwarding takes them. Options that are
    /// frozen already stay as they are.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    [MemberNotNull(nameof(SavedRangesFile))]
    internal void Freeze()
    {
        if (IsFrozen)
            return;

        Validate();
        TrustedProxyNetworks = new ReadOnlyCollection<string>([.. TrustedProxyNetworks]);
        IsFrozen = true;
    }

    /// <summary>
    /// Gets the problems of the options, which <see cref="Validate"/> reports.
    /// </summary>
    internal List<string> GetProblems()
    {
        List<string> problems = [];

        foreach (string entry in TrustedProxyNetworks)
        {
            if (!TryParseNetwork(entry, out _))
            {
                problems.Add(
                    $"'{entry}' in {nameof(CloudflareForwardingOptions)}.{nameof(TrustedProxyNetworks)} is not a valid IP address or network in CIDR " +
                    "notation (for example 127.0.0.1 or 10.0.0.0/8).");
            }
        }

        if (SavedRangesFile is null)
            problems.Add(SavedRangesFileNotSet);

        return problems;
    }

    private static void CheckSavedRangesFile(IAbsoluteFilePath value, string paramName)
    {
        if (!value.IsValidFor(PathOptions.NoUnfriendlyNames, out string? errorMessage))
        {
            throw new ArgumentException(
                $"{nameof(CloudflareForwardingOptions)}.{nameof(SavedRangesFile)} '{value.PathDisplay}' must be a path with friendly names: {errorMessage}",
                paramName);
        }
    }

    private void EnsureNotFrozen()
    {
        if (IsFrozen)
            throw new InvalidOperationException($"The {nameof(CloudflareForwardingOptions)} are in use, so they can no longer be changed.");
    }
}
