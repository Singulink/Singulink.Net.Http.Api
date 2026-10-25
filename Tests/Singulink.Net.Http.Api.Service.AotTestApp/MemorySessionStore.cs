using System.Collections.Concurrent;

namespace Singulink.Net.Http.Api.Service.AotTestApp;

/// <summary>
/// The session store. It is created by dependency injection, through the constructor that takes <see cref="MemorySessions"/>.
/// </summary>
internal sealed class MemorySessionStore(MemorySessions sessions) : ISessionStore<AppSessionToken>
{
    public Task<SessionLookupResult<AppSessionToken>?> GetSessionAsync(AppSessionToken sessionToken, CancellationToken cancellationToken)
    {
        var result = sessions.Records.TryGetValue(sessionToken.SessionId, out var record) ? new SessionLookupResult<AppSessionToken>(record) : null;
        return Task.FromResult(result);
    }

    public Task<bool> TryRefreshSessionAsync(
        AppSessionToken sessionToken, SessionRecord refreshedSession, int expectedGeneration, CancellationToken cancellationToken)
    {
        bool refreshed = sessions.Records.TryGetValue(sessionToken.SessionId, out var record) && record.Generation == expectedGeneration &&
            sessions.Records.TryUpdate(sessionToken.SessionId, refreshedSession, record);

        return Task.FromResult(refreshed);
    }

    public Task InvalidateSessionAsync(AppSessionToken sessionToken, CancellationToken cancellationToken)
    {
        sessions.Records.TryRemove(sessionToken.SessionId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Keeps the session records, standing in for a database table.
/// </summary>
internal sealed class MemorySessions
{
    private long _lastId;

    public ConcurrentDictionary<long, SessionRecord> Records { get; } = [];

    public AppSessionToken Create(string userId, SignInInfo info)
    {
        long id = Interlocked.Increment(ref _lastId);

        Records[id] = info;
        return new AppSessionToken(id, userId);
    }
}
