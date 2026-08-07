using Singulink.Enums;

namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for configuring session context behavior.
/// </summary>
public sealed class SessionHandlingOptions
{
    /// <summary>
    /// Gets or sets the name of the cookie that holds the encrypted session token. Default is <c>"session-token"</c>.
    /// </summary>
    public string SessionCookieName {
        get;
        set {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
            field = value;
        }
    } = "session-token";

    /// <summary>
    /// Gets or sets the domain that the session cookie applies to. Leave <see langword="null"/> (the default) to scope the cookie to the host that issued
    /// it. Set to a parent domain (e.g. <c>".example.com"</c>) to share the session across subdomains — in that case the same value must be configured on
    /// every application that participates in the shared session, and they must all share the same data-protection key ring so the encrypted cookie can be
    /// read across hosts.
    /// </summary>
    public string? CookieDomain { get; set; }

    /// <summary>
    /// Gets or sets the path that the session cookie applies to. Default is <c>"/"</c>.
    /// </summary>
    public string CookiePath {
        get;
        set {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
            field = value;
        }
    } = "/";

    /// <summary>
    /// Gets or sets the query parameter name for the user ID precondition. Default is <c>"if-userId"</c>.
    /// </summary>
    public string UserIdPreconditionQueryName {
        get;
        set {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
            field = value;
        }
    } = "if-userId";

    /// <summary>
    /// Gets or sets the forced session access options that are applied to all session token retrievals. Default is <see cref="SessionAccessOptions.None"/>.
    /// </summary>
    public SessionAccessOptions ForcedAccessOptions
    {
        get;
        set {
            value.ThrowIfFlagsAreNotDefined(nameof(value));
            field = value;
        }
    } = SessionAccessOptions.None;

    /// <summary>
    /// Gets or sets the <see cref="ApiException.ErrorCode" /> applied to the <see cref="UnauthorizedApiException" /> that is thrown when a required session
    /// token is missing, expired or otherwise invalid. Leave <see langword="null" /> (the default) to omit the error code. Set this to let clients tell an
    /// expired session apart from other unauthorized responses, e.g. so they can prompt the user to sign in again and retry the request.
    /// </summary>
    /// <remarks>
    /// Must only consist of valid ASCII letters, digits, hyphens, and underscores - see <see cref="ApiException.ErrorCode" /> for more info.
    /// </remarks>
    public string? NotSignedInErrorCode
    {
        get;
        set
        {
            ApiException.ThrowIfInvalidErrorCode(value);
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the grace period during which a session token from the previous generation is still accepted. This allows concurrent requests that have
    /// not yet received the updated token cookie to continue without invalidating the session. The grace period timer starts when the response carrying the
    /// refreshed token is sent to the client (i.e. when the session store is updated with the new generation). Default is 15 seconds.
    /// </summary>
    public TimeSpan MultipleRefreshGracePeriod { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the expiry duration for temporary (non-persistent) sessions. Default is 1 day.
    /// </summary>
    public TimeSpan TempSessionExpiry { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets the expiry duration for persistent sessions. Default is 30 days.
    /// </summary>
    public TimeSpan PersistentSessionExpiry { get; set; } = TimeSpan.FromDays(30);
}
