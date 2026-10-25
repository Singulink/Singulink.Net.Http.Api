using System.Collections.Concurrent;

namespace Singulink.Net.Http.Api.Service;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// In-memory session data that records every store call so tests can assert exactly what session handling did and when. Session handling uses it through
/// <see cref="ScopedInMemorySessionStore"/>, which it resolves in a new scope for each operation. Session records are projected on the way out to mimic a
/// no-tracking database query, so changes only take effect through <see cref="ISessionStore{T}.TryRefreshSessionAsync"/>.
/// </summary>
public sealed class InMemorySessionStore
{
    private readonly List<string> _calls = [];
    private readonly List<SessionRecord> _refreshedSessions = [];
    private int _storesCreated;
    private int _openStores;

    public ConcurrentDictionary<long, TestSessionData> Sessions { get; } = [];

    /// <summary>
    /// Gets the current security stamp for each user. A token is current when its stamp matches; otherwise the store replaces it.
    /// </summary>
    public ConcurrentDictionary<int, int> UserStamps { get; } = [];

    /// <summary>
    /// Gets the store calls made so far, in call order: <c>GetSession</c> (or <c>GetSession(replaced)</c> if the token was replaced),
    /// <c>RefreshSession</c> and <c>InvalidateSession</c>. A <c>RefreshSession</c> call is recorded once its conditional write is done.
    /// </summary>
    public IReadOnlyList<string> Calls
    {
        get {
            lock (_calls)
                return [.. _calls];
        }
    }

    /// <summary>
    /// Gets the session records passed to <see cref="ISessionStore{T}.TryRefreshSessionAsync"/>, in call order.
    /// </summary>
    public IReadOnlyList<SessionRecord> RefreshedSessions
    {
        get {
            lock (_refreshedSessions)
                return [.. _refreshedSessions];
        }
    }

    public int StoresCreated => Volatile.Read(ref _storesCreated);

    public int OpenStores => Volatile.Read(ref _openStores);

    /// <summary>
    /// Gets or sets a hook that runs after a session refresh has been requested but before the conditional write is evaluated, allowing tests to interleave
    /// another request's work between a request's validation and its refresh.
    /// </summary>
    public Func<Task>? BeforeRefreshSession { get; set; }

    /// <summary>
    /// Gets or sets an exception that <see cref="ISessionStore{T}.GetSessionAsync"/> throws instead of looking the session up, after recording a
    /// <c>GetSession(threw)</c> call.
    /// </summary>
    public Exception? GetSessionException { get; set; }

    /// <summary>
    /// Gets or sets a callback that runs right before <see cref="GetSessionException"/> is thrown.
    /// </summary>
    public Action? GetSessionThrowing { get; set; }

    /// <summary>
    /// Gets or sets a hook that <see cref="ISessionStore{T}.GetSessionAsync"/> awaits before it looks the session up.
    /// </summary>
    public Func<Task>? BeforeGetSession { get; set; }

    /// <summary>
    /// Gets or sets an exception that <see cref="ISessionStore{T}.InvalidateSessionAsync"/> throws instead of removing the session, after recording an
    /// <c>InvalidateSession(threw)</c> call.
    /// </summary>
    public Exception? InvalidateSessionException { get; set; }

    public void ClearCalls()
    {
        lock (_calls)
            _calls.Clear();
    }

    internal void Record(string call)
    {
        lock (_calls)
            _calls.Add(call);
    }

    internal void RecordRefresh(SessionRecord refreshedSession)
    {
        lock (_refreshedSessions)
            _refreshedSessions.Add(refreshedSession);
    }

    internal void StoreCreated()
    {
        Interlocked.Increment(ref _storesCreated);
        Interlocked.Increment(ref _openStores);
    }

    internal void StoreDisposed() => Interlocked.Decrement(ref _openStores);
}

/// <summary>
/// The session store that session handling resolves for each operation, over the shared <see cref="InMemorySessionStore"/> data. Like a database, it throws
/// if an operation is cancelled.
/// </summary>
public sealed class ScopedInMemorySessionStore : ISessionStore<TestSessionToken>, IAsyncDisposable
{
    private readonly InMemorySessionStore _store;
    private bool _disposed;

    public ScopedInMemorySessionStore(InMemorySessionStore store)
    {
        _store = store;
        store.StoreCreated();
    }

    public Task<SessionLookupResult<TestSessionToken>?> GetSessionAsync(TestSessionToken sessionToken, CancellationToken cancellationToken)
    {
        return _store.BeforeGetSession is { } before ? GetSessionAfterAsync(before) : GetSession(sessionToken, cancellationToken);

        async Task<SessionLookupResult<TestSessionToken>?> GetSessionAfterAsync(Func<Task> before)
        {
            await before();
            return await GetSession(sessionToken, cancellationToken);
        }
    }

    private Task<SessionLookupResult<TestSessionToken>?> GetSession(TestSessionToken sessionToken, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_store.GetSessionException is { } exception)
        {
            _store.Record("GetSession(threw)");
            _store.GetSessionThrowing?.Invoke();
            throw exception;
        }

        if (!_store.Sessions.TryGetValue(sessionToken.SessionId, out var data))
        {
            _store.Record("GetSession");
            return Task.FromResult<SessionLookupResult<TestSessionToken>?>(null);
        }

        var session = new SessionRecord {
            Device = data.Device,
            IpAddress = data.IpAddress,
            RefreshedUtc = data.RefreshedUtc,
            ExpiresUtc = data.ExpiresUtc,
            Generation = data.Generation,
            IsPersistent = data.IsPersistent,
            CreatedUtc = data.CreatedUtc,
        };

        int stamp = _store.UserStamps[sessionToken.UserId];
        TestSessionToken? replacement = null;

        if (stamp != sessionToken.Stamp)
            replacement = sessionToken with { Stamp = stamp, BuildCount = sessionToken.BuildCount + 1 };

        _store.Record(replacement is null ? "GetSession" : "GetSession(replaced)");
        return Task.FromResult<SessionLookupResult<TestSessionToken>?>(new SessionLookupResult<TestSessionToken>(session, replacement));
    }

    public async Task<bool> TryRefreshSessionAsync(
        TestSessionToken sessionToken, SessionRecord refreshedSession, int expectedGeneration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // The call is recorded once the write is done, so that a test that sees the call also sees the stored values.
        try
        {
            if (_store.BeforeRefreshSession is { } hook)
                await hook();

            // Evaluate the condition at write time (like a conditional UPDATE would) rather than when the refresh was requested. The creation time never
            // changes, so it is not written.

            if (!_store.Sessions.TryGetValue(sessionToken.SessionId, out var data))
                return false;

            lock (data)
            {
                if (data.Generation != expectedGeneration)
                    return false;

                data.Device = refreshedSession.Device;
                data.IpAddress = refreshedSession.IpAddress;
                data.RefreshedUtc = refreshedSession.RefreshedUtc;
                data.ExpiresUtc = refreshedSession.ExpiresUtc;
                data.Generation = refreshedSession.Generation;
            }

            return true;
        }
        finally
        {
            _store.RecordRefresh(refreshedSession);
            _store.Record("RefreshSession");
        }
    }

    public Task InvalidateSessionAsync(TestSessionToken sessionToken, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_store.InvalidateSessionException is { } exception)
        {
            _store.Record("InvalidateSession(threw)");
            throw exception;
        }

        _store.Record("InvalidateSession");
        _store.Sessions.TryRemove(sessionToken.SessionId, out _);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _store.StoreDisposed();
        }

        return ValueTask.CompletedTask;
    }
}
