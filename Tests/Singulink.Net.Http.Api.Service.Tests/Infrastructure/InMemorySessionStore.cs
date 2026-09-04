namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// In-memory session store that records every store call so tests can assert exactly what the session context did and when. Session data is copied on
/// the way in and out to mimic a no-tracking database context, so changes only take effect through <see cref="ISessionStoreContext{T, D}.UpdateSessionAsync"/>.
/// </summary>
public sealed class InMemorySessionStore : ISessionStoreContextFactory<TestSessionToken, TestSessionData>
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
    /// Gets or sets a hook that runs after a session update has been requested but before it is written, allowing tests to interleave another request's
    /// work between a refresh's read and its write.
    /// </summary>
    public Func<Task>? BeforeUpdateSession { get; set; }

    public ISessionStoreContext<TestSessionToken, TestSessionData> Create()
    {
        ContextsCreated++;
        OpenContexts++;
        return new Context(this);
    }

    private sealed class Context(InMemorySessionStore store) : ISessionStoreContext<TestSessionToken, TestSessionData>
    {
        private bool _disposed;

        public Task<TestSessionData?> GetSessionDataAsync(TestSessionToken sessionToken)
        {
            Record("GetSessionData");
            return Task.FromResult(store.Sessions.TryGetValue(sessionToken.SessionId, out var data) ? data.Clone() : null);
        }

        public async Task UpdateSessionAsync(TestSessionData sessionData)
        {
            Record("UpdateSession");

            if (store.BeforeUpdateSession is { } hook)
                await hook();

            store.Sessions[sessionData.Id] = sessionData.Clone();
        }

        public Task InvalidateSessionAsync(TestSessionToken sessionToken)
        {
            Record("InvalidateSession");
            store.Sessions.Remove(sessionToken.SessionId);
            return Task.CompletedTask;
        }

        public Task<bool> IsTokenStaleAsync(TestSessionToken sessionToken)
        {
            Record("IsTokenStale");
            return Task.FromResult(store.UserStamps[sessionToken.UserId] != sessionToken.Stamp);
        }

        public ValueTask<TestSessionToken> CreateTokenAsync(TestSessionToken previousToken, ISessionTokenRefreshInfo refreshInfo, bool isStale)
        {
            Record(isStale ? "CreateToken(stale)" : "CreateToken(current)");

            var token = previousToken with {
                RefreshedUtc = refreshInfo.RefreshedUtc,
                ValidFor = refreshInfo.ValidFor,
                Generation = refreshInfo.Generation,
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
public sealed class InMemorySessionStoreFactory(InMemorySessionStore store) : ISessionStoreContextFactory<TestSessionToken, TestSessionData>
{
    public ISessionStoreContext<TestSessionToken, TestSessionData> Create() => store.Create();
}
