#if !__WASM__ && !__IOS__ && !__ANDROID__ && !WINDOWS

using Singulink.Net.Http.Api.Client.Passkeys;
using Uno.AuthenticationBroker;
using Uno.Foundation.Extensibility;
using Windows.Security.Authentication.Web;

namespace Singulink.Net.Http.Api.Client;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// A web authentication broker for Uno Platform desktop apps, which Uno doesn't provide one for. It opens the request in the default browser and
/// receives the result on a loopback address (<c>http://127.0.0.1:{port}/callback</c>), the standard way for native apps to get a result back from the
/// browser (RFC 8252).
/// </summary>
/// <remarks>
/// <para>
/// The browser is opened at a short address on the same port (<c>http://127.0.0.1:{port}/start</c>) that redirects to the request. If the browser
/// doesn't open it within <see cref="StartTimeout"/>, <see cref="AuthenticateAsync"/> reports <see cref="WebAuthenticationStatus.ErrorHttp"/> instead of
/// waiting for the full <see cref="Timeout"/>.
/// </para>
/// <para>
/// <see cref="PasskeyAuthenticator"/> runs the same flow on desktop itself, with the hand-off settings of its <see cref="PasskeyAuthenticatorOptions"/>
/// (<see cref="PasskeyAuthenticatorOptions.CompletedMessage"/>, <see cref="PasskeyAuthenticatorOptions.Timeout"/> and
/// <see cref="PasskeyAuthenticatorOptions.StartTimeout"/>), whether or not this provider is registered, so this provider's settings don't apply to
/// passkey operations. <see cref="Register(Action{LoopbackWebAuthenticationBrokerProvider}?)"/> makes <c>WebAuthenticationBroker</c> use it.
/// </para>
/// </remarks>
public sealed class LoopbackWebAuthenticationBrokerProvider : IWebAuthenticationBrokerProvider
{
    private const string BrowserNotOpenedMessage = "The browser could not be opened.";
    private const string RequestNotOpenedMessage = "The browser did not open the request.";

    private readonly LoopbackBrowserFlow _flow;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoopbackWebAuthenticationBrokerProvider"/> class.
    /// </summary>
    public LoopbackWebAuthenticationBrokerProvider()
    {
        _flow = new LoopbackBrowserFlow();
    }

    internal LoopbackWebAuthenticationBrokerProvider(Func<Uri, Task<bool>> launchUriAsync)
    {
        _flow = new LoopbackBrowserFlow(launchUriAsync);
    }

    /// <summary>
    /// Gets or sets the message shown in the browser after it returns to the app. Default is <c>"You can close this tab and return to the app."</c>.
    /// </summary>
    public string CompletedMessage {
        get => _flow.CompletedMessage;
        set {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
            _flow.CompletedMessage = value;
        }
    }

    /// <summary>
    /// Gets or sets how long <see cref="AuthenticateAsync"/> waits for the browser to return before it reports <see
    /// cref="WebAuthenticationStatus.UserCancel"/>. Default is <see langword="null"/>, which uses Uno's
    /// <c>WinRTFeatureConfiguration.WebAuthenticationBroker.AuthenticationTimeout</c>.
    /// </summary>
    public TimeSpan? Timeout {
        get => _flow.Timeout;
        set {
            if (value is { } timeout)
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(value));

            _flow.Timeout = value;
        }
    }

    /// <summary>
    /// Gets or sets how long <see cref="AuthenticateAsync"/> waits for the browser to open before it reports <see cref="WebAuthenticationStatus.ErrorHttp"/>.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan StartTimeout {
        get => _flow.StartTimeout;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            _flow.StartTimeout = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="Register(Action{LoopbackWebAuthenticationBrokerProvider}?)"/> registered this provider as the app's web
    /// authentication broker provider.
    /// </summary>
    internal static bool IsRegistered { get; private set; }

    /// <summary>
    /// Registers this provider as the app's web authentication broker provider. Call it at startup (e.g. in the desktop <c>Program.Main</c>), before
    /// <c>WebAuthenticationBroker</c> is first used.
    /// </summary>
    /// <param name="configure">An optional action that configures the provider instance.</param>
    /// <exception cref="InvalidOperationException">A web authentication broker provider is already registered.</exception>
    public static void Register(Action<LoopbackWebAuthenticationBrokerProvider>? configure = null)
    {
        if (ApiExtensibility.IsRegistered<IWebAuthenticationBrokerProvider>())
            throw new InvalidOperationException("A web authentication broker provider is already registered.");

        // Set first, so that a passkey operation running meanwhile never sees this provider registered without it.
        IsRegistered = true;

        ApiExtensibility.Register(typeof(IWebAuthenticationBrokerProvider), _ => {
            var provider = new LoopbackWebAuthenticationBrokerProvider();
            configure?.Invoke(provider);
            return provider;
        });
    }

    /// <summary>
    /// Starts listening on a free loopback port and returns the callback address on it, for use by the next <see cref="AuthenticateAsync"/> call.
    /// </summary>
    public Uri GetCurrentApplicationCallbackUri() => _flow.CreateCallbackUri();

    /// <summary>
    /// Opens <paramref name="requestUri"/> in the default browser and waits for the browser to return to <paramref name="callbackUri"/>.
    /// </summary>
    /// <param name="options">The options. Not used.</param>
    /// <param name="requestUri">The address to open.</param>
    /// <param name="callbackUri">The loopback HTTP address to wait on, normally the one returned by <see cref="GetCurrentApplicationCallbackUri"/>.
    /// </param>
    /// <param name="ct">A token that cancels the wait, which then reports <see cref="WebAuthenticationStatus.UserCancel"/>.</param>
    /// <returns>The result. On success, its response data is the full callback address the browser returned to, including the query. If the browser
    /// could not be opened or didn't open the request within <see cref="StartTimeout"/>, the status is <see cref="WebAuthenticationStatus.ErrorHttp"/>
    /// and the response data describes the error.</returns>
    /// <exception cref="ArgumentException"><paramref name="callbackUri"/> is not a loopback HTTP address, or its path is <c>/start</c>.</exception>
    public async Task<WebAuthenticationResult> AuthenticateAsync(WebAuthenticationOptions options, Uri requestUri, Uri callbackUri, CancellationToken ct)
    {
        var result = await _flow.RunAsync(requestUri, callbackUri, ct).ConfigureAwait(false);

        return result.Status switch {
            LoopbackBrowserStatus.Success => new WebAuthenticationResult(result.CallbackUri, 0, WebAuthenticationStatus.Success),
            LoopbackBrowserStatus.LaunchFailed => new WebAuthenticationResult(BrowserNotOpenedMessage, 0, WebAuthenticationStatus.ErrorHttp),
            LoopbackBrowserStatus.NotOpened => new WebAuthenticationResult(RequestNotOpenedMessage, 0, WebAuthenticationStatus.ErrorHttp),
            _ => new WebAuthenticationResult(null, 0, WebAuthenticationStatus.UserCancel),
        };
    }
}

#endif
