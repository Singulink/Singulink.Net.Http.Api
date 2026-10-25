namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Provides options for the passkey hand-off page mapped by <see cref="PasskeyPageEndpointRouteBuilderExtensions.MapPasskeyPage"/>.
/// </summary>
public sealed class PasskeyPageOptions
{
    /// <summary>
    /// Gets or sets the app name shown on the page. Defaults to <see cref="PasskeyOptions.RelyingPartyName"/> if passkey services are registered.
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    /// Gets the custom URI schemes that apps' callback addresses can use, such as <c>myapp</c> for <c>myapp://passkey</c>. Matched exactly
    /// (ignoring case). A trailing colon is ignored. Standard schemes that browsers handle themselves, such as <c>http</c>, <c>https</c> and
    /// <c>javascript</c>, are not allowed.
    /// </summary>
    /// <remarks>
    /// On mobile platforms another app can register the same custom scheme and receive the credential instead. A sign-in bound to a PKCE code challenge
    /// (see <see cref="PasskeyService.BeginSignInAsync"/>) cannot be finished by an app that only receives its credential. An app that registers the scheme
    /// can still open the page with a sign-in it began itself, which the page cannot tell apart from a sign-in of the app the scheme was meant for.
    /// </remarks>
    public IList<string> RedirectSchemes { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether callback addresses can be loopback addresses (<c>http://127.0.0.1:{port}/...</c> or
    /// <c>http://[::1]:{port}/...</c> on any port), which desktop apps listen on to receive the result. Default is <see langword="true"/>.
    /// </summary>
    public bool AllowLoopbackRedirects { get; set; } = true;
}
