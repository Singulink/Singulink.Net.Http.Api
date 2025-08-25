namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Options for configuring session behavior.
/// </summary>
[Flags]
public enum SessionOptions
{
    /// <summary>
    /// Indicates that session token information should be refreshed from the session store, even if it is not due for a refresh yet.
    /// </summary>
    ForceRefresh = 1,

    /// <summary>
    /// Indicates that the user ID precondition header should be optional (i.e. it is only checked if it is provided in the request).
    /// </summary>
    OptionalUserIdPrecondition = 2,

    /// <summary>
    /// Indicates that the session token should be accessible even if the request is from an untrusted origin. This option should only be used for requests that
    /// are not sensitive to CSRF attacks.
    /// </summary>
    AllowUntrustedOrigin = 4,
}
