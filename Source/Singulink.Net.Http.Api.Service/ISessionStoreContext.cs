namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents a session data storage context used for managing session tokens and associated data.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
/// <typeparam name="TSessionData">The session storage entry type.</typeparam>
public interface ISessionStoreContext<TSessionToken, TSessionData> : IAsyncDisposable
    where TSessionToken : class, ISessionToken
    where TSessionData : class, ISessionData
{
    /// <summary>
    /// Gets the session data associated with the specified session token.
    /// </summary>
    Task<TSessionData?> GetSessionDataAsync(TSessionToken sessionToken);

    /// <summary>
    /// Persists updated session data to the data store.
    /// </summary>
    Task UpdateSessionAsync(TSessionData sessionData);

    /// <summary>
    /// Signs out of the session associated with the specified session token.
    /// </summary>
    Task InvalidateSessionAsync(TSessionToken sessionToken);

    /// <summary>
    /// Determines whether the information contained in the specified session token is still current, i.e. the data it was created from has not changed
    /// since it was created. Implementations typically compare a security stamp or version captured in the token against the latest value in the data
    /// store. This is called once per request when the token is due for a refresh or the request forces session validation.
    /// </summary>
    Task<bool> IsTokenCurrentAsync(TSessionToken sessionToken);

    /// <summary>
    /// Creates a new session token for the session represented by the previous token with the provided refresh info applied.
    /// </summary>
    /// <param name="previousToken">The token the new token is based on.</param>
    /// <param name="refreshInfo">The refresh info to apply to the new token.</param>
    /// <param name="isStale">Indicates whether the previous token's information was found to be out of date by <see cref="IsTokenCurrentAsync"/>. If
    /// <see langword="true"/>, the new token must be built from the latest information in the data store. If <see langword="false"/>, implementations may
    /// reuse the previous token's information and only apply the refresh info (typically synchronously). Reloading the information regardless of this
    /// value is always safe, just slower.</param>
    ValueTask<TSessionToken> CreateTokenAsync(TSessionToken previousToken, ISessionTokenRefreshInfo refreshInfo, bool isStale);
}

/// <summary>
/// Represents a factory for creating <see cref="ISessionStoreContext{TSessionToken, TSessionData}"/> instances.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
/// <typeparam name="TSessionData">The session storage entry type.</typeparam>
public interface ISessionStoreContextFactory<TSessionToken, TSessionData>
    where TSessionToken : class, ISessionToken
    where TSessionData : class, ISessionData
{
    /// <summary>
    /// Creates a new data context for session data operations.
    /// </summary>
    ISessionStoreContext<TSessionToken, TSessionData> Create();
}
