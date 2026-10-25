#if !__WASM__ && !WINDOWS
using Windows.Security.Authentication.Web;
#endif

#if !__WASM__ && !__IOS__ && !__ANDROID__ && !WINDOWS
using Uno.AuthenticationBroker;
using Uno.Foundation.Extensibility;
#endif

namespace Singulink.Net.Http.Api.Client.Passkeys;

/// <summary>
/// Runs passkey (WebAuthn) operations for an app: signing in, confirming the signed-in user and creating passkeys. WebAssembly apps run them in their
/// own page. Apps on iOS, Android and desktop (Uno Platform desktop and Windows App SDK) hand them off to the passkey page
/// (<see cref="PasskeyAuthenticatorOptions.PageUri"/>) in the system browser, which returns the result to the app.
/// </summary>
/// <remarks>
/// <para>
/// Options and credentials are WebAuthn JSON: the app gets the options from its API, passes them in, and posts the returned credential's
/// <see cref="PasskeyCredential.Json"/> back to its API.
/// </para>
/// <para>
/// When the passkey page is used, the credential travels through the app's callback address, which another app could intercept on mobile platforms.
/// Sign-in ceremonies are therefore bound to a PKCE code challenge when they begin, which services require by default.
/// </para>
/// </remarks>
public sealed class PasskeyAuthenticator : IPasskeyAuthenticator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyAuthenticator"/> class with default options. Without a passkey page address
    /// (<see cref="PasskeyAuthenticatorOptions.PageUri"/>), it only runs passkey operations on WebAssembly.
    /// </summary>
    public PasskeyAuthenticator() : this(new PasskeyAuthenticatorOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyAuthenticator"/> class with the specified options.
    /// </summary>
    /// <param name="options">The options. Later changes to them apply to subsequent operations.</param>
    public PasskeyAuthenticator(PasskeyAuthenticatorOptions options)
    {
        Options = options;
    }

    /// <summary>
    /// Gets the options.
    /// </summary>
    public PasskeyAuthenticatorOptions Options { get; }

#if !__WASM__ && !__IOS__ && !__ANDROID__
    /// <summary>
    /// Gets or sets the factory for the loopback flow used on desktop, unless an Uno Platform app registered a web authentication broker provider of its
    /// own. The flow is configured with the hand-off settings of <see cref="Options"/>. Replaced by tests.
    /// </summary>
    internal Func<LoopbackBrowserFlow> CreateLoopbackFlow { get; set; } = () => new LoopbackBrowserFlow();
#endif

    /// <summary>
    /// Gets a value indicating whether passkeys can be used on this platform. On WebAssembly, this depends on the browser.
    /// </summary>
    public bool IsSupported =>
#if __WASM__
        PasskeyInterop.IsSupported;
#else
        true;
#endif

    /// <summary>
    /// Gets a value indicating whether operations are handed off to the passkey page in the system browser (iOS, Android and desktop) rather than run in
    /// the app's own page (WebAssembly).
    /// </summary>
    public bool UsesPasskeyPage =>
#if __WASM__
        false;
#else
        true;
#endif

    /// <summary>
    /// Loads what operations need ahead of time, so that the first operation does not wait for it: the passkeys script on WebAssembly. Does nothing on
    /// other platforms.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation. On WebAssembly, cancellation stops waiting but doesn't stop the script from
    /// loading.</param>
    /// <exception cref="PasskeyAuthenticatorException">The passkeys script could not be loaded. The next operation tries again.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task PrepareAsync(CancellationToken cancellationToken = default)
    {
#if __WASM__
        return PasskeyInterop.PrepareAsync(cancellationToken);
#else
        return cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;
#endif
    }

    /// <summary>
    /// Gets a passkey credential (a WebAuthn assertion), to sign in or to confirm the signed-in user.
    /// </summary>
    /// <param name="requestOptionsJson">The request options from the app's API, as WebAuthn JSON (<c>PublicKeyCredentialRequestOptionsJSON</c>).</param>
    /// <param name="cancellationToken">A token to cancel the operation. On WebAssembly, cancellation stops waiting but can't close a prompt the browser is
    /// already showing.</param>
    /// <returns>The credential to post to the app's API, and whether the passkey was on this device.</returns>
    /// <exception cref="PasskeyAuthenticatorException">The operation failed or the person cancelled it. <see cref="PasskeyAuthenticatorException.Reason"/>
    /// says why.</exception>
    /// <exception cref="InvalidOperationException">This platform uses the passkey page and <see cref="PasskeyAuthenticatorOptions.PageUri"/> is not set,
    /// or the app's callback isn't set up.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<PasskeyCredential> GetCredentialAsync(string requestOptionsJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestOptionsJson);

#if __WASM__
        return ToCredentialAsync(PasskeyInterop.GetCredentialAsync(requestOptionsJson, cancellationToken));
#else
        return ToCredentialAsync(RunWithPageAsync(PasskeyPageProtocol.GetOperation, requestOptionsJson, cancellationToken));
#endif
    }

    /// <summary>
    /// Creates a passkey (a WebAuthn attestation).
    /// </summary>
    /// <param name="creationOptionsJson">The creation options from the app's API, as WebAuthn JSON (<c>PublicKeyCredentialCreationOptionsJSON</c>).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation. On WebAssembly, cancellation stops waiting but can't close a prompt the browser is
    /// already showing.</param>
    /// <returns>The new credential to post to the app's API, and whether the passkey was created on this device.</returns>
    /// <exception cref="PasskeyAuthenticatorException">The operation failed or the person cancelled it. <see cref="PasskeyAuthenticatorException.Reason"/>
    /// says why.</exception>
    /// <exception cref="InvalidOperationException">This platform uses the passkey page and <see cref="PasskeyAuthenticatorOptions.PageUri"/> is not set,
    /// or the app's callback isn't set up.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<PasskeyCredential> CreateCredentialAsync(string creationOptionsJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(creationOptionsJson);

#if __WASM__
        return ToCredentialAsync(PasskeyInterop.CreateCredentialAsync(creationOptionsJson, cancellationToken));
#else
        return ToCredentialAsync(RunWithPageAsync(PasskeyPageProtocol.CreateOperation, creationOptionsJson, cancellationToken));
#endif
    }

    /// <summary>
    /// Tells the browser, and through it the password manager, that a passkey is not registered with the relying party, so that it can stop offering
    /// the passkey. On WebAssembly, this uses the WebAuthn Signal API in browsers that have it. Does nothing on other platforms or in other browsers,
    /// and ignores failures.
    /// </summary>
    /// <param name="credential">The credential of the passkey, as returned by <see cref="GetCredentialAsync"/>.</param>
    /// <param name="requestOptionsJson">The request options that the credential was returned for, which name the relying party.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// The service reports a passkey that is not registered with <see cref="PasskeyErrorCodes.NotRegistered"/>. A passkey that is registered to another
    /// user, which the service reports with <see cref="PasskeyErrorCodes.WrongAccount"/>, is not unknown to the relying party.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="requestOptionsJson"/> is empty.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task SignalUnknownCredentialAsync(PasskeyCredential credential, string requestOptionsJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestOptionsJson);

#if __WASM__
        return PasskeyInterop.SignalUnknownCredentialAsync(credential.Json, requestOptionsJson, cancellationToken);
#else
        return cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;
#endif
    }

    private static async Task<PasskeyCredential> ToCredentialAsync(Task<string> credentialJsonTask)
    {
        return PasskeyCredential.FromJson(await credentialJsonTask.ConfigureAwait(false));
    }

#if !__WASM__
    private async Task<string> RunWithPageAsync(string operation, string optionsJson, CancellationToken cancellationToken)
    {
        var pageUri = Options.PageUri ?? throw new InvalidOperationException(
            $"{nameof(PasskeyAuthenticatorOptions)}.{nameof(PasskeyAuthenticatorOptions.PageUri)} must be set to use passkeys on this platform.");

        cancellationToken.ThrowIfCancellationRequested();
        string state = PasskeyPageProtocol.CreateState();

#if __IOS__ || __ANDROID__
        Uri callbackUri = Options.CallbackUri ?? WebAuthenticationBroker.GetCurrentApplicationCallbackUri();
        var requestUri = PasskeyPageProtocol.BuildRequestUri(pageUri, operation, optionsJson, callbackUri, state);

        return await RunWithBrokerAsync(requestUri, callbackUri, state, cancellationToken).ConfigureAwait(false);
#else
        // Neither Uno desktop apps nor WinUI 3 desktop apps have a working web authentication broker, so the loopback flow is used, unless an Uno app
        // registered a provider of its own. The toolkit's loopback provider would run the same flow with its own settings, so the flow runs here with the
        // options' settings instead, which apply on every desktop target.
#if !WINDOWS
        if (ApiExtensibility.IsRegistered<IWebAuthenticationBrokerProvider>() && !LoopbackWebAuthenticationBrokerProvider.IsRegistered)
        {
            Uri brokerCallbackUri = WebAuthenticationBroker.GetCurrentApplicationCallbackUri();
            var brokerRequestUri = PasskeyPageProtocol.BuildRequestUri(pageUri, operation, optionsJson, brokerCallbackUri, state);

            return await RunWithBrokerAsync(brokerRequestUri, brokerCallbackUri, state, cancellationToken).ConfigureAwait(false);
        }
#endif

        var flow = CreateLoopbackFlow();
        flow.CompletedMessage = Options.CompletedMessage;
        flow.Timeout = Options.Timeout;
        flow.StartTimeout = Options.StartTimeout;

        Uri callbackUri = flow.CreateCallbackUri();
        var requestUri = PasskeyPageProtocol.BuildRequestUri(pageUri, operation, optionsJson, callbackUri, state);

        var result = await flow.RunAsync(requestUri, callbackUri, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return result.Status switch {
            LoopbackBrowserStatus.Success => PasskeyPageProtocol.ParseResponse(result.CallbackUri, state),
            LoopbackBrowserStatus.LaunchFailed => throw new PasskeyAuthenticatorException(PasskeyErrorReason.PageNotOpened, "The browser could not be opened."),
            LoopbackBrowserStatus.NotOpened => throw new PasskeyAuthenticatorException(
                PasskeyErrorReason.PageNotOpened, "The browser did not open the passkey page."),

            // The browser didn't return in time. A flow that the caller cancelled has thrown above.
            _ => throw new PasskeyAuthenticatorException(PasskeyErrorReason.TimedOut, "The browser did not return from the passkey page in time."),
        };
#endif
    }
#endif

#if !__WASM__ && !WINDOWS
    private async Task<string> RunWithBrokerAsync(Uri requestUri, Uri callbackUri, string state, CancellationToken cancellationToken)
    {
        WebAuthenticationResult result;

#if __IOS__
        bool previousPrefersEphemeral = Uno.WinRTFeatureConfiguration.WebAuthenticationBroker.PrefersEphemeralWebBrowserSession;
        Uno.WinRTFeatureConfiguration.WebAuthenticationBroker.PrefersEphemeralWebBrowserSession = Options.PrefersEphemeralWebBrowserSession;
#endif

        try
        {
            result = await WebAuthenticationBroker.AuthenticateAsync(WebAuthenticationOptions.None, requestUri, callbackUri)
                .AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The Android broker reports a browser tab closed without a result as a cancellation.
            throw new PasskeyAuthenticatorException(PasskeyErrorReason.Cancelled, null);
        }
#if __IOS__
        finally
        {
            Uno.WinRTFeatureConfiguration.WebAuthenticationBroker.PrefersEphemeralWebBrowserSession = previousPrefersEphemeral;
        }
#endif

        cancellationToken.ThrowIfCancellationRequested();

        return result.ResponseStatus switch {
            WebAuthenticationStatus.Success => PasskeyPageProtocol.ParseResponse(result.ResponseData, state),
            WebAuthenticationStatus.UserCancel => throw new PasskeyAuthenticatorException(PasskeyErrorReason.Cancelled, null),
            WebAuthenticationStatus.ErrorHttp => throw new PasskeyAuthenticatorException(
                PasskeyErrorReason.PageNotOpened, $"The passkey page could not be opened ({result.ResponseData ?? "no details"})."),
            _ => throw new PasskeyAuthenticatorException(
                PasskeyErrorReason.Unknown, $"The passkey page could not be completed ({result.ResponseData ?? result.ResponseStatus.ToString()})."),
        };
    }
#endif
}
