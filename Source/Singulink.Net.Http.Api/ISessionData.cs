using System.Net;

namespace Singulink.Net.Http.Api;

/// <summary>
/// Represents session data in a session store.
/// </summary>
public interface ISessionData : ISessionTokenRefreshInfo
{
    /// <summary>
    /// Gets or sets the device associated with the session.
    /// </summary>
    string Device { get; set; }

    /// <summary>
    /// Gets or sets the last IP address associated with the session.
    /// </summary>
    IPAddress? IpAddress { get; set; }

    /// <summary>
    /// Gets or sets the date and time when the session was last refreshed (in UTC).
    /// </summary>
    new DateTime RefreshedUtc { get; set; }

    /// <summary>
    /// Gets or sets the amount of time after which the session expires and can no longer be refreshed.
    /// </summary>
    new TimeSpan ValidFor { get; set; }

    /// <summary>
    /// Gets or sets the generation of the session. This value is incremented each time the session is refreshed, and the previous generation is allowed a
    /// refresh for a short grace period to allow for concurrent refresh requests.
    /// </summary>
    new int Generation { get; set; }

    /// <summary>
    /// Gets a value indicating whether the session should be persistent (i.e., it should survive browser or application restarts).
    /// </summary>
    bool IsPersistent { get; }

    /// <inheritdoc/>
    DateTime ISessionTokenRefreshInfo.RefreshedUtc => RefreshedUtc;

    /// <inheritdoc/>
    TimeSpan ISessionTokenRefreshInfo.ValidFor => ValidFor;

    /// <inheritdoc/>
    int ISessionTokenRefreshInfo.Generation => Generation;
}
