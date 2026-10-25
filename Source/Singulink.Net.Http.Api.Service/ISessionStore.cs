namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents the store that keeps the records of sessions. Session handling resolves the store from a new service scope for each operation, so the store
/// can take scoped services such as a database context through dependency injection.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
public interface ISessionStore<TSessionToken>
    where TSessionToken : class, ISessionToken
{
    /// <summary>
    /// Gets the session that the specified session token belongs to, or <see langword="null"/> to end the session.
    /// </summary>
    /// <param name="sessionToken">The session token from the session cookie.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// <para>
    /// This is called when the session cookie is due for a refresh (see <see cref="SessionHandlingOptions.RefreshAfter"/>), when a request forces
    /// validation (see <see cref="SessionAccessOptions.ForceValidate"/>), and when a SignalR connection's session is rechecked. A <see langword="null"/>
    /// result ends the session: the request continues without a session and the session cookie is cleared, except that a browser's request keeps a cookie
    /// that was due. <see cref="InvalidateSessionAsync"/> is not called for it.
    /// </para>
    /// <para>
    /// If the result has a <see cref="SessionLookupResult{TSessionToken}.ReplacementToken"/>, the session continues with that token: the request gets
    /// it, and the session cookie is reissued with it.
    /// </para>
    /// <para>
    /// An <see cref="UnauthorizedApiException"/> thrown by this method ends the session with a reason: <see cref="InvalidateSessionAsync"/> is called,
    /// the session cookie is cleared and the exception propagates to the request, which reports it to the client. In a SignalR connection or a streaming
    /// response that has started, the cookie can no longer be cleared, so only the exception reaches the client. A SignalR connection whose session is
    /// rechecked is closed with the exception (see <see cref="SessionHandlingOptions.RevalidateHubConnections"/>).
    /// </para>
    /// <para>
    /// It is also called at the start of a response that reissues the session cookie without a refresh, such as with a replacement token, to check that
    /// the session still exists. There, a <see langword="null"/> result or an exception only skips the reissue: a replacement token is ignored, and
    /// <see cref="InvalidateSessionAsync"/> is not called.
    /// </para>
    /// </remarks>
    Task<SessionLookupResult<TSessionToken>?> GetSessionAsync(TSessionToken sessionToken, CancellationToken cancellationToken);

    /// <summary>
    /// Updates the session that the specified session token belongs to with the values of <paramref name="refreshedSession"/>, if its stored generation is
    /// still <paramref name="expectedGeneration"/>. Returns <see langword="true"/> if the session was updated, or <see langword="false"/> if it no longer
    /// exists or its generation has changed.
    /// </summary>
    /// <param name="sessionToken">The token of the session to update.</param>
    /// <param name="refreshedSession">The values to store: <see cref="SessionRecord.Device"/>, <see cref="SessionRecord.IpAddress"/>,
    /// <see cref="SessionRecord.RefreshedUtc"/>, <see cref="SessionRecord.ExpiresUtc"/> and <see cref="SessionRecord.Generation"/>. Its
    /// <see cref="SessionRecord.CreatedUtc"/> and <see cref="SessionRecord.IsPersistent"/> are the stored values, which never change.</param>
    /// <param name="expectedGeneration">The generation the stored session must have for the update to be applied.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// The generation check and the update must be atomic, such as a single conditional update statement, so that only one of several concurrent updates
    /// of the same generation succeeds. The generation of <paramref name="refreshedSession"/> can be the same as <paramref name="expectedGeneration"/>.
    /// </remarks>
    Task<bool> TryRefreshSessionAsync(TSessionToken sessionToken, SessionRecord refreshedSession, int expectedGeneration, CancellationToken cancellationToken);

    /// <summary>
    /// Ends the session that the specified session token belongs to.
    /// </summary>
    /// <param name="sessionToken">The token of the session to end.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// <para>
    /// This is called when the user signs out, when validation ends a session that has expired, is past its maximum lifetime or was presented with a
    /// token from a generation that is no longer accepted, and when <see cref="GetSessionAsync"/> throws an <see cref="UnauthorizedApiException"/>. A
    /// request that has a session and signs in ends that previous session through it too, unless the sign-in returns the previous session's token.
    /// </para>
    /// <para>
    /// Signing out, ending the previous session at sign-in and ending a session after <see cref="GetSessionAsync"/> threw in a request are not cancelled
    /// with the request: their <paramref name="cancellationToken"/> cannot be cancelled. The rechecks of SignalR connections pass a token of their own,
    /// which is cancelled when the app shuts down.
    /// </para>
    /// </remarks>
    Task InvalidateSessionAsync(TSessionToken sessionToken, CancellationToken cancellationToken);
}
