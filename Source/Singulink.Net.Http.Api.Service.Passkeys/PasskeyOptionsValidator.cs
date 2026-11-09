using Microsoft.Extensions.Options;

namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Validates <see cref="PasskeyOptions"/>, so that a misconfiguration fails at startup rather than when the first ceremony is verified. Settings with which
/// some ceremonies cannot run are logged as warnings. It checks options of every name.
/// </summary>
internal sealed class PasskeyOptionsValidator(ILoggerFactory? loggerFactory = null) : IValidateOptions<PasskeyOptions>
{
    internal const string AndroidOriginPrefix = "android:apk-key-hash:";

    internal const string LocalhostRelyingPartyId = "localhost";

    public ValidateOptionsResult Validate(string? name, PasskeyOptions options)
    {
        var failures = options.GetProblems();
        string? rpId = options.RelyingPartyId;
        bool isValidRpId = !string.IsNullOrWhiteSpace(rpId) && Uri.CheckHostName(rpId) is UriHostNameType.Dns;

        if (options.AllowLocalhostOrigins && isValidRpId && !rpId!.Equals(LocalhostRelyingPartyId, StringComparison.OrdinalIgnoreCase))
        {
            loggerFactory?.CreateLogger<PasskeyOptions>().LogWarning(
                "{Option} is true, but the relying party ID is '{RelyingPartyId}' rather than 'localhost', so browsers do not run ceremonies on localhost " +
                "pages: they only run them on the relying party ID and its subdomains.",
                nameof(PasskeyOptions.AllowLocalhostOrigins),
                rpId);
        }

        return failures.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Determines whether a value is a valid allowed origin, and returns the origin as a URI unless it is an Android app's origin.
    /// </summary>
    internal static bool IsValidOrigin(string origin, out Uri? uri)
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

    internal static bool IsOnRelyingPartyId(string host, string rpId)
    {
        return host.Equals(rpId, StringComparison.OrdinalIgnoreCase) ||
            (host.Length > rpId.Length && host.EndsWith(rpId, StringComparison.OrdinalIgnoreCase) && host[^(rpId.Length + 1)] is '.');
    }
}
