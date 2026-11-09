namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The state of the tracker of the hub connections of one session token type (see <see cref="ISessionConnectionTracker"/>).
/// </summary>
/// <param name="TrackedConnections">The hub connections with a session that this server tracks.</param>
/// <param name="ConnectionsEnded">The connections that rechecks ended because their session ended, since the app started.</param>
/// <param name="LastCheckUtc">When the last round of rechecks ended, or <see langword="null"/> before the first one.</param>
/// <param name="LastCheckDue">The connections that the last round rechecked.</param>
/// <param name="LastCheckFailures">The rechecks of the last round that failed, such as when the session store couldn't be reached.</param>
internal readonly record struct HubSessionTrackerStatus(
    int TrackedConnections, long ConnectionsEnded, DateTimeOffset? LastCheckUtc, int LastCheckDue, int LastCheckFailures);
