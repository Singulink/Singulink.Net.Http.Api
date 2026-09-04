namespace Singulink.Net.Http.Api;

/// <summary>
/// Represents information that should be refreshed on a session token.
/// </summary>
public interface ISessionTokenRefreshInfo
{
    /// <summary>
    /// Gets the date and time when the session token was last refreshed (in UTC).
    /// </summary>
    DateTime RefreshedUtc { get; }

    /// <summary>
    /// Gets the amount of time after which the session token expires and can no longer be refreshed.
    /// </summary>
    TimeSpan ValidFor { get; }

    /// <summary>
    /// Gets the generation of the session token. This value is incremented each time the token is refreshed, and the previous generation is allowed a refresh
    /// for a short grace period to allow for concurrent refresh requests.
    /// </summary>
    int Generation { get; }
}
