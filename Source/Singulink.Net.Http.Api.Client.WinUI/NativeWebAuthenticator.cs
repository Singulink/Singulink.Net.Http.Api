#if !__WASM__ && !WINDOWS
using Windows.Security.Authentication.Web;

namespace Singulink.Net.Http.Api.Client.Passkeys;

/// <summary>
/// Runs a web authentication session on iOS, as <see cref="WebAuthenticationNative.AuthenticateAsync"/> does. Replaced by tests.
/// </summary>
internal delegate Task<WebAuthenticationNativeResult> NativeWebAuthenticator(
    Uri requestUri, Uri callbackUri, bool prefersEphemeralWebBrowserSession, CancellationToken cancellationToken);

/// <summary>
/// Runs a web authentication broker operation, as <see cref="WebAuthenticationBroker.AuthenticateAsync(WebAuthenticationOptions, Uri, Uri)"/> does.
/// Replaced by tests.
/// </summary>
internal delegate Task<WebAuthenticationResult> BrokerAuthenticator(Uri requestUri, Uri callbackUri, CancellationToken cancellationToken);

#endif
