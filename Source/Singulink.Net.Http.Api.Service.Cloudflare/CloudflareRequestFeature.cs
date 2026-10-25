namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Marks a request that the forwarding middleware verified came through Cloudflare, so that Cloudflare's headers on it can be trusted.
/// </summary>
internal sealed class CloudflareRequestFeature
{
    public static readonly CloudflareRequestFeature Instance = new();

    private CloudflareRequestFeature()
    {
    }
}
