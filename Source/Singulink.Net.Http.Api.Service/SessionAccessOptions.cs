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
    /// Indicates that session token information should be refreshed from the session store, even if it is not due for a refresh yet.
    /// </summary>
    ForceRefresh = 1,

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
