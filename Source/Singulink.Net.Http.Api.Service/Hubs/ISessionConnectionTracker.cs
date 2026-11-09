using Microsoft.AspNetCore.SignalR;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Tracks the hub connections that have a session, for <see cref="SessionRevalidationHubFilter"/>. One is registered for each session token type.
/// </summary>
internal interface ISessionConnectionTracker
{
    /// <summary>
    /// Gets the name of the session token type, which the metrics and the health check of hub sessions report the tracker by.
    /// </summary>
    string TokenTypeName { get; }

    /// <summary>
    /// Gets the tracked connections, the connections that rechecks ended since the app started, and the last round of rechecks.
    /// </summary>
    HubSessionTrackerStatus GetStatus();

    /// <summary>
    /// Validates the session of a connection that is connecting if its cookie is due.
    /// </summary>
    ValueTask ValidateIfDueAsync(HubCallerContext connection);

    /// <summary>
    /// Starts tracking a connection of the specified hub type if it has a session.
    /// </summary>
    void Track(HubCallerContext connection, Type hubType);

    void Untrack(HubCallerContext connection);

    /// <summary>
    /// Gets the error, as a hub error, of a connection whose session ended and that is about to be closed, or <see langword="null"/> if its session didn't
    /// end.
    /// </summary>
    string? GetSessionEndedError(HubCallerContext connection);
}
