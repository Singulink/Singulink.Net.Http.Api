using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Validates <see cref="PasskeyOptions"/>, so that a misconfiguration fails at startup rather than when the first ceremony is verified. Settings with which
/// some ceremonies cannot run are logged as warnings.
/// </summary>
internal sealed class PasskeyOptionsValidator(ILoggerFactory? loggerFactory = null) : IValidateOptions<PasskeyOptions>
{
    internal const string AndroidOriginPrefix = "android:apk-key-hash:";

    private const string LocalhostRelyingPartyId = "localhost";

    public ValidateOptionsResult Validate(string? name, PasskeyOptions options)
    {
        List<string> failures = [];
        string rpId = options.RelyingPartyId;
        bool isValidRpId = false;

        if (string.IsNullOrWhiteSpace(rpId))
            failures.Add($"{nameof(PasskeyOptions.RelyingPartyId)} is required.");
        else if (Uri.CheckHostName(rpId) is not UriHostNameType.Dns)
            failures.Add($"{nameof(PasskeyOptions.RelyingPartyId)} '{rpId}' must be a domain name, such as 'example.com'.");
        else
            isValidRpId = true;

        if (string.IsNullOrWhiteSpace(options.RelyingPartyName))
            failures.Add($"{nameof(PasskeyOptions.RelyingPartyName)} is required.");

        if (options.AllowedOrigins.Count is 0 && !options.AllowLocalhostOrigins)
            failures.Add($"At least one origin is required in {nameof(PasskeyOptions.AllowedOrigins)}.");

        foreach (string origin in options.AllowedOrigins)
        {
            if (!IsValidOrigin(origin, out var uri))
            {
                failures.Add(
                    $"Allowed origin '{origin}' must be an origin such as 'https://app.example.com' (with no path) or an '{AndroidOriginPrefix}' origin.");
            }
            else if (isValidRpId && uri is not null && !IsOnRelyingPartyId(uri.Host, rpId))
            {
                failures.Add(
                    $"Allowed origin '{origin}' is not on the relying party ID '{rpId}' or a subdomain of it, so browsers would not run ceremonies on it.");
            }
        }

        if (options.AllowLocalhostOrigins && isValidRpId && !rpId.Equals(LocalhostRelyingPartyId, StringComparison.OrdinalIgnoreCase))
        {
            loggerFactory?.CreateLogger<PasskeyOptions>().LogWarning(
                "{Option} is true, but the relying party ID is '{RelyingPartyId}' rather than 'localhost', so browsers do not run ceremonies on localhost " +
                "pages: they only run them on the relying party ID and its subdomains.",
                nameof(PasskeyOptions.AllowLocalhostOrigins),
                rpId);
        }

        if (options.CeremonyLifetime < options.CeremonyTimeout)
            failures.Add($"{nameof(PasskeyOptions.CeremonyLifetime)} must be at least {nameof(PasskeyOptions.CeremonyTimeout)}.");

        return failures.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Determines whether a value is a valid allowed origin, and returns the origin as a URI unless it is an Android app's origin.
    /// </summary>
    private static bool IsValidOrigin(string origin, out Uri? uri)
    {
        if (origin.StartsWith(AndroidOriginPrefix, StringComparison.Ordinal))
        {
            uri = null;
            return origin.Length > AndroidOriginPrefix.Length;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out uri) &&
            uri.Scheme is "https" or "http" &&
            uri.HostNameType is not UriHostNameType.Unknown &&
            uri.UserInfo.Length is 0 &&
            uri.PathAndQuery == "/" && uri.Fragment.Length is 0;
    }

    private static bool IsOnRelyingPartyId(string host, string rpId)
    {
        return host.Equals(rpId, StringComparison.OrdinalIgnoreCase) ||
            (host.Length > rpId.Length && host.EndsWith(rpId, StringComparison.OrdinalIgnoreCase) && host[^(rpId.Length + 1)] is '.');
    }
}
