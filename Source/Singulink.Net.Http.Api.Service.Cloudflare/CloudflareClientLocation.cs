namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The visitor location that Cloudflare sends with a request, as Cloudflare provides it. Values that Cloudflare did not send are <see langword="null"/>.
/// </summary>
/// <remarks>
/// Cloudflare sends <see cref="CountryCode"/> when IP geolocation is turned on for the zone, and the other values when its "Add visitor location headers"
/// managed transform is turned on.
/// </remarks>
public sealed class CloudflareClientLocation
{
    internal CloudflareClientLocation()
    {
    }

    /// <summary>
    /// Gets the visitor's country as a two-letter ISO 3166-1 code, such as <c>CA</c>, from the <c>CF-IPCountry</c> header. Cloudflare sends <c>XX</c> when
    /// the country is unknown and <c>T1</c> for the Tor network.
    /// </summary>
    public string? CountryCode { get; internal init; }

    /// <summary>
    /// Gets the visitor's continent as a two-letter code, such as <c>NA</c>, from the <c>CF-IPContinent</c> header.
    /// </summary>
    public string? ContinentCode { get; internal init; }

    /// <summary>
    /// Gets the name of the visitor's region, such as <c>Ontario</c>, from the <c>CF-Region</c> header.
    /// </summary>
    public string? Region { get; internal init; }

    /// <summary>
    /// Gets the code of the visitor's region, such as <c>ON</c>, from the <c>CF-Region-Code</c> header.
    /// </summary>
    public string? RegionCode { get; internal init; }

    /// <summary>
    /// Gets the name of the visitor's city, such as <c>Toronto</c>, from the <c>CF-IPCity</c> header.
    /// </summary>
    public string? City { get; internal init; }

    /// <summary>
    /// Gets the visitor's postal code, from the <c>CF-Postal-Code</c> header.
    /// </summary>
    public string? PostalCode { get; internal init; }

    /// <summary>
    /// Gets the visitor's metro code (US only), from the <c>CF-Metro-Code</c> header.
    /// </summary>
    public string? MetroCode { get; internal init; }

    /// <summary>
    /// Gets the visitor's approximate latitude, as Cloudflare writes it, from the <c>CF-IPLatitude</c> header.
    /// </summary>
    public string? Latitude { get; internal init; }

    /// <summary>
    /// Gets the visitor's approximate longitude, as Cloudflare writes it, from the <c>CF-IPLongitude</c> header.
    /// </summary>
    public string? Longitude { get; internal init; }

    /// <summary>
    /// Gets the name of the visitor's time zone, such as <c>America/Toronto</c>, from the <c>CF-Timezone</c> header.
    /// </summary>
    public string? TimeZone { get; internal init; }

    /// <summary>
    /// Returns the visitor's city, region and country code, as far as Cloudflare sent them, separated by commas, such as <c>Toronto, Ontario, CA</c>. The
    /// country code <c>XX</c>, which Cloudflare sends for an unknown country, is left out. Returns an empty string if none of them is known.
    /// </summary>
    public override string ToString()
    {
        string? countryCode = CountryCode is "XX" ? null : CountryCode;
        return string.Join(", ", new[] { City, Region, countryCode }.Where(p => p is not null));
    }
}
