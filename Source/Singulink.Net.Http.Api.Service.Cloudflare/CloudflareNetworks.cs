using System.Net;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The set of Cloudflare networks whose connections are trusted to forward the client address. Replaced as a whole on refresh, so requests always see a
/// complete set.
/// </summary>
internal sealed class CloudflareNetworks
{
    private IPNetwork[]? _networks;

    /// <summary>
    /// Gets a value indicating whether the networks are available. They never become unavailable again once they are.
    /// </summary>
    public bool IsAvailable => Volatile.Read(ref _networks) is not null;

    /// <summary>
    /// Puts the specified networks in use. Returns <see langword="false"/> without replacing them if they are the same as the networks in use.
    /// </summary>
    public bool Set(IPNetwork[] networks)
    {
        if (Volatile.Read(ref _networks) is { } current && current.AsSpan().SequenceEqual(networks))
            return false;

        Volatile.Write(ref _networks, networks);
        return true;
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
