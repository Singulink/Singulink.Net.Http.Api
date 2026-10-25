namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The key prefixes of the state this library keeps in the <see cref="IShortLivedStore"/>. They all start with <c>httpapi:</c>.
/// </summary>
internal static class ShortLivedKeys
{
    public const string SignInCode = "httpapi:sign-in-code:";

    public const string DeviceLink = "httpapi:device-link:";

    public const string DeviceLinkCode = "httpapi:device-link-code:";

    public const string DeviceLinkApproval = "httpapi:device-link-approval:";

    public const string DeviceLinkSignInCode = "httpapi:device-link-sign-in-code:";

    public const string DeviceLinkWrongCodes = "httpapi:device-link-wrong-codes:";

    public const string RateLimit = "httpapi:rate-limit:";

    public const string GlobalRateLimit = "httpapi:rate-limit-global:";
}
