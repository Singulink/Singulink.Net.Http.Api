namespace Singulink.Net.Http.Api.Service.Passkeys;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides the relying party settings and ceremony behavior for <see cref="PasskeyService"/>. The options can be bound from configuration.
/// </summary>
public sealed class PasskeyOptions
{
    // WebAuthn options carry the timeout in milliseconds as an unsigned 32-bit number.
    private static readonly TimeSpan MaxCeremonyTimeout = TimeSpan.FromMilliseconds(uint.MaxValue);

    /// <summary>
    /// Gets or sets the relying party ID, a domain such as <c>example.com</c>. Passkeys are bound to it, so it must not change once passkeys exist.
    /// </summary>
    /// <remarks>
    /// Ceremonies can only run on pages whose host is the relying party ID or a subdomain of it, so a relying party ID of <c>example.com</c> covers
    /// <c>example.com</c>, <c>app.example.com</c> and <c>api.example.com</c>.
    /// </remarks>
    public string RelyingPartyId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the relying party name that browsers and authenticators may show when a passkey is created.
    /// </summary>
    public string RelyingPartyName { get; set; } = string.Empty;

    /// <summary>
    /// Gets the exact origins of the pages that run ceremonies, such as <c>https://app.example.com</c> for a web app and <c>https://api.example.com</c> for
    /// a passkey page served by the API. Native Android apps that use the platform passkey APIs report an <c>android:apk-key-hash:...</c> origin, which can
    /// be added as is.
    /// </summary>
    /// <remarks>
    /// The host of each origin, other than an <c>android:apk-key-hash:</c> one, must be the relying party ID or a subdomain of it, which is checked when the
    /// app starts.
    /// </remarks>
    public IList<string> AllowedOrigins { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether pages served from <c>http://localhost</c> or <c>https://localhost</c> on any port can run ceremonies.
    /// Default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Browsers only run ceremonies on pages whose host is the relying party ID or a subdomain of it, so pages on <c>localhost</c> can only run them with
    /// the relying party ID <c>localhost</c>. A different relying party ID with this set to <see langword="true"/> is logged as a warning when the app
    /// starts.
    /// </remarks>
    public bool AllowLocalhostOrigins { get; set; }

    /// <summary>
    /// Gets or sets how long the browser waits for the user to complete a ceremony. Default is 3 minutes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative, or longer than <see cref="uint.MaxValue"/> milliseconds.</exception>
    public TimeSpan CeremonyTimeout {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxCeremonyTimeout, nameof(value));
            field = value;
        }
    } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Gets or sets how long a started ceremony can be finished, which the ceremony reports as
    /// <see cref="DataContracts.PasskeyCeremony.Lifetime"/>. Must be at least <see cref="CeremonyTimeout"/>, which is checked when the app starts. Default is
    /// 5 minutes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan CeremonyLifetime {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            field = value;
        }
    } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets a value indicating whether the authenticator must verify the user (with a biometric, PIN or screen lock) in every ceremony. When
    /// <see langword="false"/>, verification is preferred but not required. Default is <see langword="true"/>.
    /// </summary>
    public bool RequireUserVerification { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether every sign-in must be bound to a PKCE code challenge when it begins (see
    /// <see cref="PasskeyService.BeginSignInAsync"/>). Default is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// A credential that passes through another app, such as the passkey hand-off page returning it to a native app's callback address, could be intercepted
    /// on its way. A sign-in bound to the code challenge of the app that began it cannot be finished with an intercepted credential (see
    /// <see cref="PasskeyPageOptions.RedirectSchemes"/> for what it does not prevent). When this is <see langword="false"/>, sign-ins can begin without a
    /// challenge.
    /// </remarks>
    public bool RequireCodeChallenge { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether new passkeys must be discoverable credentials, which let users sign in without entering a user name first.
    /// When <see langword="false"/>, discoverable credentials are preferred but not required. Default is <see langword="true"/>.
    /// </summary>
    public bool RequireDiscoverableCredentials { get; set; } = true;
}
