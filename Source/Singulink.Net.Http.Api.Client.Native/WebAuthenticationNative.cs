namespace Singulink.Net.Http.Api.Client;

// Compatibility rule: Singulink.Net.Http.Api.Client.WinUI calls these internal members across packages, and an app can get a newer version of this
// package than of the WinUI package. Members are only ever added, never changed or removed (see the project file).

/// <summary>
/// Runs web authentication sessions on iOS, in an <c>ASWebAuthenticationSession</c>, for <c>PasskeyAuthenticator</c> in
/// <c>Singulink.Net.Http.Api.Client.WinUI</c>: a session opens a page and ends when the page navigates to an address with the callback address's scheme,
/// which returns to the app with that address. Other platforms aren't supported (see <see cref="IsSupported"/>).
/// </summary>
internal static partial class WebAuthenticationNative
{
    /// <summary>
    /// Gets a value indicating whether this platform is supported, which is iOS.
    /// </summary>
    public static bool IsSupported => IsSupportedCore;

    /// <summary>
    /// Opens the specified page in a web authentication session and waits until it navigates to an address with the callback address's scheme, or until
    /// the person closes the session.
    /// </summary>
    /// <param name="requestUri">The address of the page to open.</param>
    /// <param name="callbackUri">The app's callback address, such as <c>myapp://callback</c>. The session ends at any address with its scheme, which the
    /// caller makes sure is a custom one.</param>
    /// <param name="prefersEphemeralWebBrowserSession">Whether the session shares no cookies or website data with Safari, which also skips the system's
    /// sign-in consent alert. Default is <see langword="true"/>.</param>
    /// <param name="cancellationToken">A token that cancels the session, which closes it.</param>
    /// <returns>The result. On success, its <see cref="WebAuthenticationNativeResult.CallbackUri"/> is the address the page navigated to, including its
    /// query.</returns>
    /// <exception cref="ArgumentException"><paramref name="requestUri"/> is not an absolute HTTP or HTTPS address, or <paramref name="callbackUri"/> is not
    /// absolute.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform is not supported.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task<WebAuthenticationNativeResult> AuthenticateAsync(
        Uri requestUri, Uri callbackUri, bool prefersEphemeralWebBrowserSession = true, CancellationToken cancellationToken = default)
    {
        if (!requestUri.IsAbsoluteUri || (requestUri.Scheme != Uri.UriSchemeHttps && requestUri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("The request address must be an absolute HTTP or HTTPS address.", nameof(requestUri));

        if (!callbackUri.IsAbsoluteUri)
            throw new ArgumentException("The callback address must be absolute.", nameof(callbackUri));

        if (!IsSupported)
            throw new PlatformNotSupportedException("Native web authentication sessions are only supported on iOS.");

        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<WebAuthenticationNativeResult>(cancellationToken);

        return AuthenticateCoreAsync(requestUri, callbackUri, prefersEphemeralWebBrowserSession, cancellationToken);
    }

#if !__IOS__
    private static bool IsSupportedCore => false;

    private static Task<WebAuthenticationNativeResult> AuthenticateCoreAsync(
        Uri requestUri, Uri callbackUri, bool prefersEphemeralWebBrowserSession, CancellationToken cancellationToken)
    {
        throw new PlatformNotSupportedException();
    }
#endif
}
