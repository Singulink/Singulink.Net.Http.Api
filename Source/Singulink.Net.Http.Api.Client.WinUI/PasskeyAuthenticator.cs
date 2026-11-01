#if !__WASM__ && !WINDOWS
using Uno.AuthenticationBroker;
using Uno.Foundation.Extensibility;
using Windows.Security.Authentication.Web;
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

#if !__WASM__
    /// <summary>
    /// Gets or sets the factory for the loopback flow used on desktop, unless an Uno Platform app registered a web authentication broker provider of its
    /// own. The flow is configured with the hand-off settings of <see cref="Options"/>. Replaced by tests.
    /// </summary>
    internal Func<LoopbackBrowserFlow> CreateLoopbackFlow { get; set; } = () => new LoopbackBrowserFlow();
#endif

#if !__WASM__ && !WINDOWS
    /// <summary>
    /// Gets or sets a value indicating whether the app registered a web authentication broker provider of its own, other than the toolkit's loopback
    /// provider, which then runs operations on every platform. Replaced by tests.
    /// </summary>
    internal Func<bool> IsBrokerProviderRegistered { get; set; } =
        () => ApiExtensibility.IsRegistered<IWebAuthenticationBrokerProvider>() && !LoopbackWebAuthenticationBrokerProvider.IsRegistered;

    /// <summary>
    /// Gets or sets a value indicating whether the app runs on Android, where Uno Platform's web authentication broker runs operations: it stays native in
    /// apps with the Skia renderer, which load the plain .NET build of this library. Replaced by tests.
    /// </summary>
    internal bool IsAndroid { get; set; } = OperatingSystem.IsAndroid();

    /// <summary>
    /// Gets or sets the web authentication session used on iOS, unless the app registered a web authentication broker provider of its own, or
    /// <see langword="null"/> on other platforms. Decided at run time in every build, since apps with Uno Platform's Skia renderer load the plain .NET
    /// build on iOS. Replaced by tests.
    /// </summary>
    internal NativeWebAuthenticator? NativeAuthenticator { get; set; } = WebAuthenticationNative.IsSupported ? WebAuthenticationNative.AuthenticateAsync : null;

    /// <summary>
    /// Gets or sets the call of the web authentication broker, used on Android and with a provider that the app registered. Replaced by tests.
    /// </summary>
    internal BrokerAuthenticator AuthenticateWithBroker { get; set; } = (requestUri, callbackUri, cancellationToken) =>
        WebAuthenticationBroker.AuthenticateAsync(WebAuthenticationOptions.None, requestUri, callbackUri).AsTask(cancellationToken);

    /// <summary>
    /// Gets or sets the web authentication broker's current application callback URI, used on iOS and Android if
    /// <see cref="PasskeyAuthenticatorOptions.CallbackUri"/> is not set, and on desktop with a provider that the app registered. Replaced by tests.
    /// </summary>
    internal Func<Uri> GetDefaultCallbackUri { get; set; } = WebAuthenticationBroker.GetCurrentApplicationCallbackUri;
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
    /// <exception cref="ArgumentException"><paramref name="requestOptionsJson"/> is empty.</exception>
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
    /// <exception cref="ArgumentException"><paramref name="creationOptionsJson"/> is empty.</exception>
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

#if !WINDOWS
        // A web authentication broker provider that the app registered runs the operation, and on Android, Uno Platform's own broker does.
        if (IsBrokerProviderRegistered() || IsAndroid)
        {
            // The callback address of the options is only used on iOS and Android.
            Uri brokerCallbackUri = (IsAndroid || NativeAuthenticator is not null) ? GetMobileCallbackUri() : GetDefaultCallbackUri();
            var brokerRequestUri = PasskeyPageProtocol.BuildRequestUri(pageUri, operation, optionsJson, brokerCallbackUri, state);

            return await RunWithBrokerAsync(brokerRequestUri, brokerCallbackUri, state, cancellationToken).ConfigureAwait(false);
        }

        // iOS.
        if (NativeAuthenticator is { } nativeAuthenticator)
        {
            Uri nativeCallbackUri = GetMobileCallbackUri();
            var nativeRequestUri = PasskeyPageProtocol.BuildRequestUri(pageUri, operation, optionsJson, nativeCallbackUri, state);

            return await RunWithNativeAsync(nativeAuthenticator, nativeRequestUri, nativeCallbackUri, state, cancellationToken).ConfigureAwait(false);
        }
#endif

        // Neither Uno desktop apps nor WinUI 3 desktop apps have a working web authentication broker, so the loopback flow runs here, with the options'
        // settings, which apply on every desktop target.
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
    }
#endif

#if !__WASM__ && !WINDOWS
    private async Task<string> RunWithNativeAsync(
        NativeWebAuthenticator nativeAuthenticator, Uri requestUri, Uri callbackUri, string state, CancellationToken cancellationToken)
    {
        var result = await nativeAuthenticator(requestUri, callbackUri, Options.PrefersEphemeralWebBrowserSession, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return result.Status switch {
            WebAuthenticationNativeStatus.Success => PasskeyPageProtocol.ParseResponse(result.CallbackUri, state),
            WebAuthenticationNativeStatus.UserCancel => throw new PasskeyAuthenticatorException(PasskeyErrorReason.Cancelled, null),
            WebAuthenticationNativeStatus.NotOpened => throw new PasskeyAuthenticatorException(
                PasskeyErrorReason.PageNotOpened, $"The passkey page could not be opened ({result.ErrorMessage ?? "no details"})."),
            _ => throw new PasskeyAuthenticatorException(
                PasskeyErrorReason.Unknown, $"The passkey page could not be completed ({result.ErrorMessage ?? "no details"})."),
        };
    }

    /// <summary>
    /// Gets the callback address on iOS and Android: the one in the options, whose setter makes sure it has a custom scheme, or the web authentication
    /// broker's, which must have one too.
    /// </summary>
    private Uri GetMobileCallbackUri()
    {
        if (Options.CallbackUri is { } callbackUri)
            return callbackUri;

        Uri defaultCallbackUri;

        try
        {
            defaultCallbackUri = GetDefaultCallbackUri();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"{nameof(PasskeyAuthenticatorOptions)}.{nameof(PasskeyAuthenticatorOptions.CallbackUri)} must be set: the app's callback address " +
                $"could not be determined. {ex.Message}", ex);
        }

        // The broker's default address can be set by the app (Uno Platform's WinRTFeatureConfiguration.WebAuthenticationBroker.DefaultReturnUri).
        if (!BrowserSchemes.HasCustomScheme(defaultCallbackUri))
        {
            throw new InvalidOperationException(
                $"The web authentication broker's callback address must have a custom scheme to be used on iOS and Android: '{defaultCallbackUri.Scheme}' " +
                $"is a scheme that browsers handle themselves. Set {nameof(PasskeyAuthenticatorOptions)}." +
                $"{nameof(PasskeyAuthenticatorOptions.CallbackUri)}.");
        }

        return defaultCallbackUri;
    }

    private async Task<string> RunWithBrokerAsync(Uri requestUri, Uri callbackUri, string state, CancellationToken cancellationToken)
    {
        WebAuthenticationResult result;

        try
        {
            result = await AuthenticateWithBroker(requestUri, callbackUri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The Android broker reports a browser tab closed without a result as a cancellation.
            throw new PasskeyAuthenticatorException(PasskeyErrorReason.Cancelled, null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return result.ResponseStatus switch {
            WebAuthenticationStatus.Success => PasskeyPageProtocol.ParseResponse(result.ResponseData, state),
            WebAuthenticationStatus.UserCancel => throw new PasskeyAuthenticatorException(PasskeyErrorReason.Cancelled, null),
            WebAuthenticationStatus.ErrorHttp => throw new PasskeyAuthenticatorException(
                PasskeyErrorReason.PageNotOpened, $"The passkey page could not be opened ({GetHttpErrorDetails(result)})."),
            _ => throw new PasskeyAuthenticatorException(
                PasskeyErrorReason.Unknown, $"The passkey page could not be completed ({result.ResponseData ?? result.ResponseStatus.ToString()})."),
        };
    }

    // The HTTP status, if the broker reported one, and its description.
    private static string GetHttpErrorDetails(WebAuthenticationResult result) =>
        result.ResponseErrorDetail is 0 ? result.ResponseData ?? "no details" : $"HTTP {result.ResponseErrorDetail}, {result.ResponseData ?? "no details"}";
#endif
}
