namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Represents the result of looking up a session in the session store: the session record and whether the token used to look it up is stale.
/// </summary>
public class SessionLookupResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SessionLookupResult"/> class.
    /// </summary>
    /// <param name="session">The session record that was found.</param>
    /// <param name="isTokenStale">A value indicating whether the information in the token used to look up the session is out of date.</param>
    public SessionLookupResult(SessionRecord session, bool isTokenStale)
    {
        ArgumentNullException.ThrowIfNull(session);

        Session = session;
        IsTokenStale = isTokenStale;
    }

    /// <summary>
    /// Gets the session record that was found.
    /// </summary>
    public SessionRecord Session { get; }

    /// <summary>
    /// Gets a value indicating whether the information contained in the token used to look up the session is stale, i.e. the data it was created from has
    /// changed since it was created. Implementations typically determine this by comparing a security stamp or version captured in the token against the
    /// latest value in the data store.
    /// </summary>
    public bool IsTokenStale { get; }
}
