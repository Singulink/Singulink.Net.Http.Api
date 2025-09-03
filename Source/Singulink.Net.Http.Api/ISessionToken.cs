namespace Singulink.Net.Http.Api;

/// <summary>
/// Represents a session token that contains user information and the last time it was refreshed.
/// </summary>
public interface ISessionToken
{
    /// <summary>
    /// Gets the user ID associated with the session (as a string).
    /// </summary>
    public string UserId { get; }

    // /// <summary>
    // /// Gets the ID of the session.
    // /// </summary>
    // long SessionId { get; }

    /// <summary>
    /// Gets the date and time when the session token was refreshed (in UTC).
    /// </summary>
    DateTime RefreshedUtc { get; }

    /// <summary>
    /// Gets amount of time after which the session token must be refreshed.
    /// </summary>
    TimeSpan RefreshAfter { get; }

    /// <summary>
    /// Gets the amount of time after which the session token expires and can no longer be refreshed.
    /// </summary>
    TimeSpan ValidFor { get; }

    /// <summary>
    /// Gets the generation of the session token. This value is incremented each time the token is refreshed, and the previous generation is allowed a refresh
    /// for a short grace period to allow for concurrent refresh requests.
    /// </summary>
    public int Generation { get; }

    /// <summary>
    /// Gets a value indicating whether the session token should be persistent (i.e., it should survive browser or application restarts).
    /// </summary>
    bool IsPersistent { get; }
}
