using System.Collections.ObjectModel;

namespace Singulink.Net.Http.Api.Service.Passkeys;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for the passkey hand-off page mapped by <see cref="PasskeyPageEndpointRouteBuilderExtensions.MapPasskeyPage"/>.
/// </summary>
/// <remarks>
/// The passkey page checks the options when it takes them, after which they can't be changed, and their setters throw an
/// <see cref="InvalidOperationException"/>. <see cref="RedirectSchemes"/> is then read-only, and changing it throws a <see cref="NotSupportedException"/>.
/// </remarks>
public sealed class PasskeyPageOptions
{
    /// <summary>
    /// Gets or sets the app name shown on the page. Default is <see langword="null"/>, which uses <see cref="PasskeyOptions.RelyingPartyName"/> if passkey
    /// services are registered.
    /// </summary>
    public string? AppName {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    }

    /// <summary>
    /// Gets the custom URI schemes that apps' callback addresses can use, such as <c>myapp</c> for <c>myapp://passkey</c>. Standard schemes that browsers
    /// handle themselves, such as <c>http</c>, <c>https</c> and <c>javascript</c>, are not allowed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched exactly (ignoring case). A trailing colon is ignored.
    /// </para>
    /// <para>
    /// On mobile platforms another app can register the same custom scheme and receive the credential instead. A sign-in bound to a PKCE code challenge
    /// (see <see cref="PasskeyService.BeginSignInAsync"/>) cannot be finished by an app that only receives its credential. An app that registers the scheme
    /// can still open the page with a sign-in it began itself, which the page cannot tell apart from a sign-in of the app the scheme was meant for.
    /// </para>
    /// </remarks>
    public IList<string> RedirectSchemes { get; private set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether callback addresses can be loopback addresses (<c>http://127.0.0.1:{port}/...</c> or
    /// <c>http://[::1]:{port}/...</c> on any port), which desktop apps listen on to receive the result. Default is <see langword="true"/>.
    /// </summary>
    public bool AllowLoopbackRedirects {
        get;
        set {
            EnsureNotFrozen();
            field = value;
        }
    } = true;

    /// <summary>
    /// Gets a value indicating whether the options are in use: the passkey page checked them (see <see cref="Validate"/>) when it took them, and they can no
    /// longer be changed.
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Checks the options as the passkey page does when it takes them.
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
    /// Checks the options (see <see cref="Validate"/>) and keeps them from being changed from then on, when the passkey page takes them. Options that are
    /// frozen already stay as they are.
    /// </summary>
    /// <exception cref="InvalidOperationException">The options are not valid. The message lists every problem.</exception>
    internal void Freeze()
    {
        if (IsFrozen)
            return;

        Validate();
        RedirectSchemes = new ReadOnlyCollection<string>([.. RedirectSchemes]);
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
            throw new InvalidOperationException($"The {nameof(PasskeyPageOptions)} are in use, so they can no longer be changed.");
    }
}
