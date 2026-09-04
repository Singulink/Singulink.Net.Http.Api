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
    /// processed, even if the token is not due for a refresh yet. If the token information is out of date, a new token is created from the latest data and
    /// returned to the caller (and re-issued to the client) so that the request operates on current information. Intended for security-sensitive
    /// operations (e.g. permanent deletions) where acting on stale session information is not acceptable.
    /// </summary>
    ForceValidate = 1,

    /// <summary>
    /// Indicates that the user ID precondition should be optional (i.e. it is only checked to see if it matches the session token user ID if it is
    /// provided in the request).
    /// </summary>
    OptionalUserIdPrecondition = 2,

    /// <summary>
    /// Indicates that session token access should be allowed for all origins. This option should only be used for requests that are not sensitive to CSRF
    /// attacks.
    /// </summary>
    AllowAllOrigins = 4,
}
