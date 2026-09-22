namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents a session data storage context used for managing session records and tokens.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
public interface ISessionStoreContext<TSessionToken> : IAsyncDisposable
    where TSessionToken : class, ISessionToken
{
    /// <summary>
    /// Gets the session record associated with the specified session token together with an indication of whether the token is stale, or
    /// <see langword="null"/> if the session does not exist.
    /// </summary>
    /// <remarks>
    /// This is called once per request when the token is due for a refresh or the request forces session validation. The staleness check should be cheap,
    /// typically comparing a security stamp or version captured in the token against the latest value in the data store, and can usually be folded into the
    /// same query that loads the session record.
    /// </remarks>
    Task<SessionLookupResult?> GetSessionAsync(TSessionToken sessionToken);

    /// <summary>
    /// Updates the session associated with the specified session token with the provided refreshed values if the stored session generation still matches
    /// <paramref name="expectedGeneration"/>. Returns <see langword="true"/> if the session was updated, or <see langword="false"/> if the session no longer
    /// exists or its generation has changed since it was loaded (i.e. it was refreshed or invalidated concurrently).
    /// </summary>
    /// <param name="sessionToken">The token identifying the session to refresh.</param>
    /// <param name="refreshedSession">The values to store for the refreshed session.</param>
    /// <param name="expectedGeneration">The generation the stored session must currently have for the update to be applied.</param>
    /// <remarks>
    /// The generation check and the update must be performed atomically, i.e. as a single conditional update statement or an equivalent compare-and-swap
    /// operation, so that two concurrent refreshes cannot both succeed.
    /// </remarks>
    Task<bool> TryRefreshSessionAsync(TSessionToken sessionToken, SessionRecord refreshedSession, int expectedGeneration);

    /// <summary>
    /// Signs out of the session associated with the specified session token.
    /// </summary>
    Task InvalidateSessionAsync(TSessionToken sessionToken);

    /// <summary>
    /// Creates a new session token for the session represented by the previous token with the refresh info from the specified session record applied.
    /// </summary>
    /// <param name="previousToken">The token the new token is based on.</param>
    /// <param name="session">The session record whose refresh info (<see cref="SessionRecord.RefreshedUtc"/>, <see cref="SessionRecord.ValidFor"/> and
    /// <see cref="SessionRecord.Generation"/>) must be applied to the new token.</param>
    /// <param name="isStale">Indicates whether the previous token's information was found to be out of date by <see cref="GetSessionAsync"/>. If
    /// <see langword="true"/>, the new token must be built from the latest information in the data store. If <see langword="false"/>, implementations may
    /// reuse the previous token's information and only apply the refresh info (typically synchronously). Reloading the information regardless of this
    /// value is always safe, just slower.</param>
    ValueTask<TSessionToken> CreateTokenAsync(TSessionToken previousToken, SessionRecord session, bool isStale);
}

/// <summary>
/// Represents a factory for creating <see cref="ISessionStoreContext{TSessionToken}"/> instances.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
public interface ISessionStoreContextFactory<TSessionToken>
    where TSessionToken : class, ISessionToken
{
    /// <summary>
    /// Creates a new data context for session data operations. Contexts are created per operation and disposed when the operation completes.
    /// </summary>
    ISessionStoreContext<TSessionToken> Create();
}
