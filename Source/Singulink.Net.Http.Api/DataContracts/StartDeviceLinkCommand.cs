namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a request from a new device to start a device link.
/// </summary>
public sealed class StartDeviceLinkCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StartDeviceLinkCommand"/> class.
    /// </summary>
    /// <param name="codeChallenge">The new device's PKCE code challenge.</param>
    public StartDeviceLinkCommand(string codeChallenge)
    {
        CodeChallenge = codeChallenge;
    }

    /// <summary>
    /// Gets the new device's PKCE code challenge (<c>S256</c>, base64url), which the sign-in code of an approved link is bound to.
    /// </summary>
    public string CodeChallenge { get; }
}
