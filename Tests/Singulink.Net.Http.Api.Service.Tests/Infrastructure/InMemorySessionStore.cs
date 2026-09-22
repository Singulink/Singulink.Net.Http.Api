namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// In-memory session store that records every store call so tests can assert exactly what the session context did and when. Session records are projected
/// on the way out to mimic a no-tracking database query, so changes only take effect through <see cref="ISessionStoreContext{T}.TryRefreshSessionAsync"/>.
/// </summary>
public sealed class InMemorySessionStore : ISessionStoreContextFactory<TestSessionToken>
{
    public Dictionary<long, TestSessionData> Sessions { get; } = [];

    /// <summary>
    /// Gets the current security stamp for each user. A token is current when its stamp matches.
    /// </summary>
    public Dictionary<int, int> UserStamps { get; } = [];

    public List<string> Calls { get; } = [];

    public int ContextsCreated { get; private set; }

    public int OpenContexts { get; private set; }

    /// <summary>
    /// Gets or sets a hook that runs after a session refresh has been requested but before the conditional write is evaluated, allowing tests to interleave
    /// another request's work between a request's validation and its refresh.
    /// </summary>
    public Func<Task>? BeforeRefreshSession { get; set; }

    public ISessionStoreContext<TestSessionToken> Create()
    {
        ContextsCreated++;
        OpenContexts++;
        return new Context(this);
    }

    private sealed class Context(InMemorySessionStore store) : ISessionStoreContext<TestSessionToken>
    {
        private bool _disposed;

        public Task<SessionLookupResult?> GetSessionAsync(TestSessionToken sessionToken)
        {
            Record("GetSession");

            if (!store.Sessions.TryGetValue(sessionToken.SessionId, out var data))
                return Task.FromResult<SessionLookupResult?>(null);

            var session = new SessionRecord(data.Device, data.IpAddress, data.RefreshedUtc, data.ValidFor, data.Generation, data.IsPersistent);
            bool isTokenStale = store.UserStamps[sessionToken.UserId] != sessionToken.Stamp;

            return Task.FromResult<SessionLookupResult?>(new SessionLookupResult(session, isTokenStale));
        }

        public async Task<bool> TryRefreshSessionAsync(TestSessionToken sessionToken, SessionRecord refreshedSession, int expectedGeneration)
        {
            Record("RefreshSession");

            if (store.BeforeRefreshSession is { } hook)
                await hook();

            // Evaluate the condition at write time (like a conditional UPDATE would) rather than when the refresh was requested.

            if (!store.Sessions.TryGetValue(sessionToken.SessionId, out var data) || data.Generation != expectedGeneration)
                return false;

            data.Device = refreshedSession.Device;
            data.IpAddress = refreshedSession.IpAddress;
            data.RefreshedUtc = refreshedSession.RefreshedUtc;
            data.ValidFor = refreshedSession.ValidFor;
            data.Generation = refreshedSession.Generation;

            return true;
        }

        public Task InvalidateSessionAsync(TestSessionToken sessionToken)
        {
            Record("InvalidateSession");
            store.Sessions.Remove(sessionToken.SessionId);
            return Task.CompletedTask;
        }

        public ValueTask<TestSessionToken> CreateTokenAsync(TestSessionToken previousToken, SessionRecord session, bool isStale)
        {
            Record(isStale ? "CreateToken(stale)" : "CreateToken(current)");

            var token = previousToken with {
                RefreshedUtc = session.RefreshedUtc,
                ValidFor = session.ValidFor,
                Generation = session.Generation,
            };

            if (isStale)
                token = token with { Stamp = store.UserStamps[previousToken.UserId], BuildCount = previousToken.BuildCount + 1 };

            return ValueTask.FromResult(token);
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                store.OpenContexts--;
            }

            return ValueTask.CompletedTask;
        }

        private void Record(string call)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            store.Calls.Add(call);
        }
    }
}

/// <summary>
/// Session store factory that resolves the shared in-memory store from the container (so a test can seed it).
/// </summary>
public sealed class InMemorySessionStoreFactory(InMemorySessionStore store) : ISessionStoreContextFactory<TestSessionToken>
{
    public ISessionStoreContext<TestSessionToken> Create() => store.Create();
}
