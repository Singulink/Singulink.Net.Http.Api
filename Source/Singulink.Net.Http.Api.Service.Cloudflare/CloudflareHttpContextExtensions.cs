namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Extension methods for <see cref="HttpContext"/> that read what Cloudflare sends with a request.
/// </summary>
public static class CloudflareHttpContextExtensions
{
    /// <summary>
    /// Gets the visitor location that Cloudflare sent with the request, or <see langword="null"/> if the request did not come through Cloudflare or Cloudflare
    /// sent no location.
    /// </summary>
    /// <param name="httpContext">The context of the request.</param>
    /// <remarks>
    /// A request came through Cloudflare when the middleware added by <see cref="CloudflareWebApplicationExtensions.UseCloudflareForwarding"/> verified that
    /// its connection came from Cloudflare (or from a trusted proxy, see <see cref="CloudflareForwardingOptions.TrustedProxyNetworks"/>).
    /// Without the middleware, every request returns <see langword="null"/>.
    /// </remarks>
    public static CloudflareClientLocation? GetClientLocation(this HttpContext httpContext)
    {
        if (httpContext.Features.Get<CloudflareRequestFeature>() is null)
            return null;

        var headers = httpContext.Request.Headers;
        bool hasValue = false;

        var location = new CloudflareClientLocation {
            CountryCode = Get("CF-IPCountry"),
            ContinentCode = Get("CF-IPContinent"),
            Region = Get("CF-Region"),
            RegionCode = Get("CF-Region-Code"),
            City = Get("CF-IPCity"),
            PostalCode = Get("CF-Postal-Code"),
            MetroCode = Get("CF-Metro-Code"),
            Latitude = Get("CF-IPLatitude"),
            Longitude = Get("CF-IPLongitude"),
            TimeZone = Get("CF-Timezone"),
        };

        return hasValue ? location : null;

        string? Get(string name)
        {
            string value = headers[name].ToString();

            if (value.Length is 0)
                return null;

            hasValue = true;
            return value;
        }
    }
}
