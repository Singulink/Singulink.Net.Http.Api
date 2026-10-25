namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a request to finish a passkey sign-in: the ceremony, the credential that the browser returned and the PKCE code verifier.
/// </summary>
public sealed class FinishPasskeySignInCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FinishPasskeySignInCommand"/> class.
    /// </summary>
    /// <param name="ceremonyId">The ID of the ceremony that the sign-in began with.</param>
    /// <param name="credential">The credential that the browser returned, as WebAuthn JSON.</param>
    /// <param name="codeVerifier">The PKCE code verifier that matches the code challenge the sign-in began with.</param>
    public FinishPasskeySignInCommand(string ceremonyId, string credential, string codeVerifier)
    {
        CeremonyId = ceremonyId;
        Credential = credential;
        CodeVerifier = codeVerifier;
    }

    /// <summary>
    /// Gets the ID of the ceremony that the sign-in began with (see <see cref="PasskeyCeremony.Id"/>).
    /// </summary>
    public string CeremonyId { get; }

    /// <summary>
    /// Gets the credential that the browser returned, as WebAuthn JSON (the <c>PublicKeyCredential.toJSON()</c> form).
    /// </summary>
    public string Credential { get; }

    /// <summary>
    /// Gets the PKCE code verifier that matches the code challenge the sign-in began with.
    /// </summary>
    public string CodeVerifier { get; }
}
