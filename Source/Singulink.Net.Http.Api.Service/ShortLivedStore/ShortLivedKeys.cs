namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The key prefixes of the state this library keeps in the <see cref="IShortLivedStore"/>. They all start with <c>httpapi:</c>.
/// </summary>
internal static class ShortLivedKeys
{
    public const string Prefix = "httpapi:";

    public const string SignInCode = "httpapi:sign-in-code:";

    public const string DeviceLink = "httpapi:device-link:";

    public const string DeviceLinkCode = "httpapi:device-link-code:";

    public const string DeviceLinkApproval = "httpapi:device-link-approval:";

    public const string DeviceLinkSignInCode = "httpapi:device-link-sign-in-code:";

    public const string DeviceLinkWrongCodes = "httpapi:device-link-wrong-codes:";

    public const string RateLimit = "httpapi:rate-limit:";

    public const string GlobalRateLimit = "httpapi:rate-limit-global:";

    // The counters of open uploads: {name}:{userId} for a user's and {name} for all users, or {name}@{machineName}:{userId} and {name}@{machineName}
    // when they are kept for each machine (see OpenUploadCounters).
    public const string UploadCount = "httpapi:upload-count";

    public const string UploadSize = "httpapi:upload-size";

    public const string UploadTotalSize = "httpapi:upload-total-size";

    // The cancellation of an upload: {name}{uploadId}:{scope}:{userId}, where scope is a hash of the endpoint and route values. Upload IDs and hashes have no
    // colon.
    public const string UploadCancel = "httpapi:upload-cancel:";
}
