namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for configuring <see cref="WebApplication"/> and its related interfaces to trust the client address and scheme forwarded by Cloudflare.
/// </summary>
public static class CloudflareWebApplicationExtensions
{
    /// <summary>
    /// Adds middleware that trusts the client address and scheme forwarded by Cloudflare. Requires the services registered by <see
    /// cref="CloudflareServiceCollectionExtensions.AddCloudflareForwarding(IServiceCollection, Action{CloudflareForwardingOptions}?)"/>.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <remarks>
    /// <para>
    /// Call this first in the pipeline, before any middleware that uses the client's address or the request scheme (such as HTTPS redirection, rate limiting
    /// or request logging).
    /// </para>
    /// <para>
    /// For requests whose connection comes from one of Cloudflare's IP ranges (or from one of the
    /// <see cref="CloudflareForwardingOptions.TrustedProxyNetworks"/>), <see cref="ConnectionInfo.RemoteIpAddress"/> is set to the address in the
    /// <c>CF-Connecting-IP</c> header and <see cref="HttpRequest.Scheme"/> to the value of the <c>X-Forwarded-Proto</c> header (<c>http</c> or
    /// <c>https</c>). Missing or invalid values leave the request unchanged.
    /// </para>
    /// <para>
    /// All other requests are direct connections: their address and scheme are left unchanged and every request header whose name starts with <c>CF-</c> is
    /// removed, so Cloudflare headers such as <c>CF-IPCountry</c> can be trusted whenever they are present. A direct connection with a
    /// <c>CF-Connecting-IP</c> header that has another address is logged as a warning, once an hour for each connection address.
    /// </para>
    /// <para>
    /// Until Cloudflare's IP ranges are available, requests through Cloudflare cannot be told apart from direct connections, so every request is refused
    /// with a <c>503 Service Unavailable</c> response in the toolkit's error format, with the error code
    /// <see cref="ApiErrorCodes.CloudflareRangesUnavailable"/> and a <c>Retry-After</c> header. This only happens when the ranges could not be downloaded at
    /// startup and there is no usable saved copy (see <see cref="CloudflareForwardingOptions.SavedRangesPath"/>). The response is written before the
    /// middleware that comes after this one runs, such as CORS, so it has no CORS headers and pages on other origins cannot read it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The Cloudflare forwarding services are not registered.</exception>
    public static IApplicationBuilder UseCloudflareForwarding(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetService<CloudflareNetworks>() is null)
        {
            throw new InvalidOperationException(
                $"Cloudflare forwarding services are not registered. Call {nameof(CloudflareServiceCollectionExtensions.AddCloudflareForwarding)} when " +
                "configuring services.");
        }

        return app.UseMiddleware<CloudflareForwardingMiddleware>();
    }
}
