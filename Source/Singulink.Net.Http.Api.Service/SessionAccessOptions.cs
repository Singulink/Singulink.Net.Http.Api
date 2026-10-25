namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Options for configuring session access behavior.
/// </summary>
[Flags]
public enum SessionAccessOptions
{
    /// <summary>
    /// Indicates that no special session options are set.
    /// </summary>
    None = 0,

    /// <summary>
    /// Indicates that the session must be validated against the session store and the session token information checked for changes before the request is
    /// processed, even if the token is not due for a refresh yet. As with a periodic refresh, if the token information is out of date a new token is
    /// created from the latest data and returned to the caller (and reissued to the client) so that the request operates on current information. Every
    /// retrieval with this option validates the session, including later ones in the same request or hub connection, unless the token was set in the
    /// request.
    /// </summary>
    ForceValidate = 1,

    /// <summary>
    /// Indicates that the user ID precondition is optional: it is only checked against the session token's user ID if the request has one.
    /// </summary>
    OptionalUserIdPrecondition = 2,

    /// <summary>
    /// Indicates that session token access is allowed for requests from all origins, which leaves the request open to cross-site request forgery.
    /// </summary>
    AllowAllOrigins = 4,
}
