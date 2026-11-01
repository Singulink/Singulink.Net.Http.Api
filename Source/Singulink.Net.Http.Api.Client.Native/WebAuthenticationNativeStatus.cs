namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Specifies how a web authentication session run by <see cref="WebAuthenticationNative"/> ended.
/// </summary>
internal enum WebAuthenticationNativeStatus
{
    /// <summary>
    /// The page navigated to the callback address.
    /// </summary>
    Success,

    /// <summary>
    /// The person closed the session before the page navigated to the callback address.
    /// </summary>
    UserCancel,

    /// <summary>
    /// The session could not be started or shown.
    /// </summary>
    NotOpened,

    /// <summary>
    /// The session failed otherwise.
    /// </summary>
    Error,
}
