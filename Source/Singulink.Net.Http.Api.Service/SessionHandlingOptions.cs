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
    public string SessionCookie {
        get;
        set {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
            field = value;
        }
    } = "session-token";

    /// <summary>
    /// Gets or sets the key for the user ID precondition header. Default is <c>"If-User-Id"</c>.
    /// </summary>
    public string UserIdPreconditionHeader {
        get;
        set {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
            field = value;
        }
    } = "If-User-Id";

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
    /// Gets or sets the grace period where multiple refresh requests for the same session token generation are allowed. This is to allow for concurrent requests that
    /// may all try to refresh the token at the same time. Default is 10 seconds.
    /// </summary>
    public TimeSpan MultipleRefreshGracePeriod { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the expiry duration for temporary (non-persistent) sessions. Default is 1 day.
    /// </summary>
    public TimeSpan TempSessionExpiry { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets the expiry duration for persistent sessions. Default is 30 days.
    /// </summary>
    public TimeSpan PersistentSessionExpiry { get; set; } = TimeSpan.FromDays(30);
}
