namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents a session found in the session store: its record, and optionally a token that replaces the one it was found with.
/// </summary>
/// <typeparam name="TSessionToken">The session token type.</typeparam>
public sealed class SessionLookupResult<TSessionToken>
    where TSessionToken : class, ISessionToken
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SessionLookupResult{TSessionToken}"/> class.
    /// </summary>
    /// <param name="session">The session record that was found.</param>
    /// <param name="replacementToken">The token that the session continues with instead of the one it was found with, or <see langword="null"/> to keep
    /// that token.</param>
    public SessionLookupResult(SessionRecord session, TSessionToken? replacementToken = null)
    {
        Session = session;
        ReplacementToken = replacementToken;
    }

    /// <summary>
    /// Gets the session record that was found.
    /// </summary>
    public SessionRecord Session { get; }

    /// <summary>
    /// Gets the token that the session continues with instead of the one it was found with, or <see langword="null"/> to keep that token. The request that
    /// found the session gets the replacement token, and the session cookie is reissued with it.
    /// </summary>
    public TSessionToken? ReplacementToken { get; }
}
