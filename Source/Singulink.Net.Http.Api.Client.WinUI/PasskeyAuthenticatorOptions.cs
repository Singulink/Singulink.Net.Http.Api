namespace Singulink.Net.Http.Api.Client.Passkeys;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for <see cref="PasskeyAuthenticator"/>.
/// </summary>
/// <remarks>
/// The authenticator that uses the options checks them when it takes them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>.
/// </remarks>
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
            EnsureNotFrozen();
            if (value is not null && !IsValidPageUri(value))
                throw new ArgumentException("The passkey page address must be an absolute HTTPS address, or an HTTP loopback address.", nameof(value));

            field = value;

            static bool IsValidPageUri(Uri value) =>
                value.IsAbsoluteUri && (value.Scheme == Uri.UriSchemeHttps || (value.Scheme == Uri.UriSchemeHttp && value.IsLoopback));
        }
    }

    /// <summary>
    /// Gets or sets the address the passkey page returns to on iOS and Android, such as <c>myapp://passkey</c>, which must have a custom scheme. Not used
    /// on desktop, where a loopback address is chosen automatically, or on WebAssembly. Default is <see langword="null"/>, which uses the web
    /// authentication broker's current application callback URI: Uno Platform's <c>WinRTFeatureConfiguration.WebAuthenticationBroker.DefaultReturnUri</c>
    /// if it is set, otherwise the first custom scheme of the app followed by Uno Platform's default callback path, <c>:/authentication-callback</c>, with
    /// no host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Android apps whose activity's intent filter has a host must set this property or <c>DefaultReturnUri</c>. On iOS the default's scheme is the first
    /// URL scheme in the app's <c>Info.plist</c>, and on Android the first scheme of an activity that derives from Uno Platform's
    /// <c>WebAuthenticationBrokerActivityBase</c>.
    /// </para>
    /// <para>
    /// The scheme must be allowed by the passkey page. On Android, an activity that derives from <c>WebAuthenticationBrokerActivityBase</c> must have an
    /// intent filter that matches the address's scheme and host. On iOS, a scheme set here doesn't need to be registered in <c>Info.plist</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not an absolute URI with a custom scheme: its scheme is one that browsers handle themselves, such as
    /// <c>https</c>.</exception>
    public Uri? CallbackUri {
        get;
        set {
            EnsureNotFrozen();
            if (value is not null && !BrowserSchemes.HasCustomScheme(value))
                throw new ArgumentException("The callback URI must be an absolute URI with a custom scheme, such as myapp://passkey.", nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether iOS opens the passkey page in an ephemeral browser session, which shares no cookies or website data with
    /// Safari and skips the system's sign-in consent alert. Not used on other platforms, or when the app registered a web authentication broker provider of
    /// its own. Default is <see langword="true"/>.
    /// </summary>
    public bool PrefersEphemeralWebBrowserSession {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    } = true;

    /// <summary>
    /// Gets or sets the message shown in the browser after the passkey page returns to the app on desktop (Uno Platform desktop and Windows App SDK). Not
    /// used on other platforms. Default is <c>"You can close this tab and return to the app."</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty.</exception>
    public string CompletedMessage {
        get;
        set {
            EnsureNotFrozen();
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(value));
            field = value;
        }
    } = DefaultCompletedMessage;

    /// <summary>
    /// Gets or sets how long an operation on desktop (Uno Platform desktop and Windows App SDK) waits for the browser to return from the passkey page
    /// before it fails with <see cref="PasskeyErrorReason.TimedOut"/>. Not used on other platforms. Must be greater than zero. Default is
    /// <see langword="null"/>, which uses Uno's <c>WinRTFeatureConfiguration.WebAuthenticationBroker.AuthenticationTimeout</c> in Uno Platform desktop
    /// apps, and 5 minutes in Windows App SDK apps.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan? Timeout {
        get;
        set {
            EnsureNotFrozen();
            if (value is { } timeout)
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets how long an operation on desktop (Uno Platform desktop and Windows App SDK) waits for the browser to open the passkey page before it
    /// fails with <see cref="PasskeyErrorReason.PageNotOpened"/>. Not used on other platforms. Must be greater than zero. Default is 30 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan StartTimeout {
        get;
        set {
            EnsureNotFrozen();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = DefaultStartTimeout;

    /// <summary>
    /// Gets a value indicating whether the options are in use: the authenticator that uses them checked them (see <see cref="Validate"/>) when it took them,
    /// and they can no longer be changed.
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as the authenticator that uses them does when it takes them.
    /// </summary>
    /// <remarks>
    /// Each value is checked when it is set, and no rule involves more than one of them, so this does not throw.
    /// </remarks>
    public void Validate()
    {
        var problems = GetProblems();

        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
    }

    /// <summary>
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when the authenticator that uses them takes them.
    /// Options that are frozen already stay as they are.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    internal void Freeze()
    {
        if (IsFrozen)
            return;

        Validate();
        IsFrozen = true;
    }

    /// <summary>
    /// Gets the problems of the options, which <see cref="Validate"/> reports.
    /// </summary>
    internal List<string> GetProblems()
    {
        return [];
    }

    private void EnsureNotFrozen()
    {
        if (IsFrozen)
            throw new InvalidOperationException($"The {nameof(PasskeyAuthenticatorOptions)} are in use, so they can no longer be changed.");
    }
}
