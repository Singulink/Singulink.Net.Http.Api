namespace Singulink.Net.Http.Api;

/// <summary>
/// Contains the error codes (<see cref="ApiException.ErrorCode"/>) of the errors that the <c>Singulink.Net.Http.Api.Service</c> and
/// <c>Singulink.Net.Http.Api.Service.Cloudflare</c> packages report for requests they refuse themselves, so that clients can tell them apart from each
/// other and from the app's own errors.
/// </summary>
public static class ApiErrorCodes
{
    /// <summary>
    /// The request needs a signed-in user and has no valid session. Thrown as an <see cref="UnauthorizedApiException"/> when a required session token is
    /// read. The toolkit's API clients end the session on the device when they receive it.
    /// </summary>
    public const string SignInRequired = "sign-in-required";

    /// <summary>
    /// The request accesses the session from an origin that is not allowed. Thrown as a <see cref="ForbiddenApiException"/>.
    /// </summary>
    public const string OriginBlocked = "origin-blocked";

    /// <summary>
    /// The request has no <c>User-Agent</c> header, or an empty one, where the session needs the device it describes, such as to sign in or to validate a
    /// session. Thrown as a <see cref="BadRequestApiException"/>.
    /// </summary>
    public const string UserAgentRequired = "user-agent-required";

    /// <summary>
    /// A rate limit rejected the request. Reported as a <see cref="TooManyRequestsApiException"/>, with <see cref="ApiException.RetryAfter"/> set when the
    /// limit reports how long to wait.
    /// </summary>
    public const string RateLimited = "rate-limited";

    /// <summary>
    /// The service's in-memory short-lived store has no room for the state the request needs, such as a sign-in code or a rate limit count. Thrown as a
    /// <see cref="ServiceUnavailableApiException"/>, with <see cref="ApiException.RetryAfter"/> set.
    /// </summary>
    public const string ShortLivedStoreFull = "short-lived-store-full";

    /// <summary>
    /// The service has not loaded Cloudflare's IP ranges yet, so it cannot tell requests through Cloudflare from direct connections and refuses every
    /// request. Reported as a <see cref="ServiceUnavailableApiException"/> by the Cloudflare forwarding middleware of the
    /// <c>Singulink.Net.Http.Api.Service.Cloudflare</c> package, with <see cref="ApiException.RetryAfter"/> set.
    /// </summary>
    public const string CloudflareRangesUnavailable = "cloudflare-ranges-unavailable";
}
