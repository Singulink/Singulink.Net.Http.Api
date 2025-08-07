namespace Singulink.Net.Http.Api;

/// <summary>
/// Represents a session token that contains user information and the last time it was refreshed.
/// </summary>
public interface ISessionToken<TUserId>
    where TUserId : notnull, IParsable<TUserId>, IEquatable<TUserId>
{
    /// <summary>
    /// Gets the user ID associated with the session token.
    /// </summary>
    TUserId UserId { get; }

    /// <summary>
    /// Gets the date and time when the session token was last refreshed, in UTC.
    /// </summary>
    DateTime RefreshedUtc { get; }

    /// <summary>
    /// Gets a value indicating whether the session token is persisted (i.e., it should persist across browser sessions or application restarts).
    /// </summary>
    bool Persisted { get; }
}
