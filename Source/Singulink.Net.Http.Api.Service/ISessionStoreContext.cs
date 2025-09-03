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
    /// Refreshes the specified session token and produces a new token with updated information from the data store and provided refresh info.
    /// </summary>
    Task<TSessionToken> RefreshTokenAsync(TSessionToken previousToken, ISessionTokenRefreshInfo refreshInfo);
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
