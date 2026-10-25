namespace Singulink.Net.Http.Api.Client.Passkeys;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for <see cref="PasskeyAuthenticator"/>.
/// </summary>
public sealed class PasskeyAuthenticatorOptions
{
    internal const string DefaultCompletedMessage = "You can close this tab and return to the app.";

    internal static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the address of the passkey page that apps on iOS, Android and desktop open in the system browser to run passkey operations. Required
    /// on those platforms; not used on WebAssembly. Default is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page must implement the toolkit's passkey page protocol, as the page served by <c>MapPasskeyPage</c> in
    /// <c>Singulink.Net.Http.Api.Service.Passkeys</c> does. Passkeys are bound to the page's origin, so it must be served from the relying party's
    /// domain or a subdomain of it.
    /// </para>
    /// <para>
    /// The address must be absolute and use HTTPS. HTTP is accepted for loopback hosts.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not an absolute HTTPS address or an HTTP loopback address.</exception>
    public Uri? PageUri {
        get;
        set {
            if (value is not null && !IsValidPageUri(value))
                throw new ArgumentException("The passkey page address must be an absolute HTTPS address, or an HTTP loopback address.", nameof(value));

            field = value;

            static bool IsValidPageUri(Uri value) =>
                value.IsAbsoluteUri && (value.Scheme == Uri.UriSchemeHttps || (value.Scheme == Uri.UriSchemeHttp && value.IsLoopback));
        }
    }

    /// <summary>
    /// Gets or sets the address the passkey page returns to on iOS and Android, such as <c>myapp://passkey</c>. Default is <see langword="null"/>, which
    /// uses the web authentication broker's current application callback URI. Not used on desktop, where a loopback address is chosen automatically, or
    /// on WebAssembly.
    /// </summary>
    /// <remarks>
    /// The scheme must be registered by the app (in <c>Info.plist</c> on iOS, and by a <c>WebAuthenticationBrokerActivityBase</c> activity on Android)
    /// and allowed by the passkey page.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not an absolute URI.</exception>
    public Uri? CallbackUri {
        get;
        set {
            if (value is not null && !value.IsAbsoluteUri)
                throw new ArgumentException("The callback URI must be absolute.", nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether iOS opens the passkey page in an ephemeral browser session, which shares no cookies or website data with
    /// Safari and skips the system's sign-in consent alert. Not used on other platforms. Default is <see langword="true"/>.
    /// </summary>
    public bool PrefersEphemeralWebBrowserSession { get; set; } = true;

    /// <summary>
    /// Gets or sets the message shown in the browser after the passkey page returns to the app on desktop (Uno Platform desktop and Windows App SDK). Not
    /// used on other platforms. Default is <c>"You can close this tab and return to the app."</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty.</exception>
    public string CompletedMessage {
        get;
        set {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
            field = value;
        }
    } = DefaultCompletedMessage;

    /// <summary>
    /// Gets or sets how long an operation on desktop (Uno Platform desktop and Windows App SDK) waits for the browser to return from the passkey page
    /// before it fails with <see cref="PasskeyErrorReason.TimedOut"/>. Not used on other platforms. Default is <see langword="null"/>, which uses Uno's
    /// <c>WinRTFeatureConfiguration.WebAuthenticationBroker.AuthenticationTimeout</c> in Uno Platform desktop apps, and 5 minutes in Windows App SDK apps.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan? Timeout {
        get;
        set {
            if (value is { } timeout)
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets how long an operation on desktop (Uno Platform desktop and Windows App SDK) waits for the browser to open the passkey page before it
    /// fails with <see cref="PasskeyErrorReason.PageNotOpened"/>. Not used on other platforms. Default is 30 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan StartTimeout {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = DefaultStartTimeout;
}
