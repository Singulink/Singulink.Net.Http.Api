namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a request to begin a passkey sign-in: the PKCE code challenge that the sign-in is bound to, and optionally the name of the user to sign in.
/// </summary>
public sealed class BeginPasskeySignInCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BeginPasskeySignInCommand"/> class.
    /// </summary>
    /// <param name="codeChallenge">The client's PKCE code challenge.</param>
    /// <param name="userName">The name the user signs in with, such as an email address, or <see langword="null"/> for none.</param>
    public BeginPasskeySignInCommand(string codeChallenge, string? userName = null)
    {
        CodeChallenge = codeChallenge;
        UserName = userName;
    }

    /// <summary>
    /// Gets the client's PKCE code challenge (<c>S256</c>, base64url), which the sign-in is bound to, so that it can only be finished with the matching
    /// code verifier.
    /// </summary>
    public string CodeChallenge { get; }

    /// <summary>
    /// Gets the name the user signs in with, such as an email address, or <see langword="null"/> if the sign-in is not for a particular user.
    /// </summary>
    public string? UserName { get; }
}
