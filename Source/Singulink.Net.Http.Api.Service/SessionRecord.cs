using System.Net;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents the values of a session record in the session store that are managed by session handling.
/// </summary>
/// <remarks>
/// <para>
/// Session stores return an instance of this class when a session is loaded, and receive an instance with the updated values when a session is refreshed.
/// </para>
/// <para>
/// The creation time (<see cref="CreatedUtc"/>) is what the maximum session lifetimes configured in <see cref="SessionHandlingOptions"/> are measured
/// from, so session stores record it when a session is created and return it when the session is loaded. It never changes, so a refreshed record carries
/// the creation time of the record that was loaded and stores do not need to write it when a session is refreshed.
/// </para>
/// </remarks>
public class SessionRecord
{
    /// <summary>
    /// Gets the device that last refreshed the session.
    /// </summary>
    public required string Device { get; init; }

    /// <summary>
    /// Gets the IP address that last refreshed the session, or <see langword="null"/> if it is not known.
    /// </summary>
    public required IPAddress? IpAddress { get; init; }

    /// <summary>
    /// Gets the date and time when the session was last refreshed (in UTC).
    /// </summary>
    public required DateTime RefreshedUtc { get; init; }

    /// <summary>
    /// Gets the date and time when the session expires (in UTC), after which it can no longer be refreshed.
    /// </summary>
    /// <remarks>
    /// Session handling sets it to the configured expiry for the session's kind after <see cref="RefreshedUtc"/>, capped at the end of the session's
    /// maximum lifetime (see <see cref="SessionHandlingOptions.MaxTempSessionLifetime"/> and
    /// <see cref="SessionHandlingOptions.MaxPersistentSessionLifetime"/>). Validation ends a session that is past it, or past the end of a maximum lifetime
    /// that was shortened after the record was written.
    /// </remarks>
    public required DateTime ExpiresUtc { get; init; }

    /// <summary>
    /// Gets the generation of the session. This value is incremented each time the session is refreshed, and a session cookie from the previous generation
    /// is accepted for a short grace period to allow for concurrent refresh requests.
    /// </summary>
    public required int Generation { get; init; }

    /// <summary>
    /// Gets a value indicating whether the session is persistent: it survives browser or application restarts.
    /// </summary>
    public required bool IsPersistent { get; init; }

    /// <summary>
    /// Gets the date and time when the session was created, when the user signed in (in UTC). The maximum session lifetimes configured in
    /// <see cref="SessionHandlingOptions"/> are measured from it.
    /// </summary>
    public required DateTime CreatedUtc { get; init; }

    /// <summary>
    /// Gets the date and time when the session expires with the specified options (in UTC): <see cref="ExpiresUtc"/>, or the end of its maximum lifetime
    /// with the specified options if that comes first.
    /// </summary>
    internal DateTime GetExpiresUtc(SessionHandlingOptions options)
    {
        if (options.GetLifetimeEndUtc(IsPersistent, CreatedUtc) is { } lifetimeEndUtc && lifetimeEndUtc < ExpiresUtc)
            return lifetimeEndUtc;

        return ExpiresUtc;
    }

    /// <summary>
    /// Gets a value indicating whether the session has expired at the specified time with the specified options: whether the time is past
    /// <see cref="GetExpiresUtc(SessionHandlingOptions)"/>.
    /// </summary>
    internal bool IsExpired(SessionHandlingOptions options, DateTime utcNow) => utcNow > GetExpiresUtc(options);
}
