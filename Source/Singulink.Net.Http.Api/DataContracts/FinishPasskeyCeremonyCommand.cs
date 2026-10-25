namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a request to finish a passkey registration or verification: the ceremony and the credential that the browser returned.
/// </summary>
public sealed class FinishPasskeyCeremonyCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FinishPasskeyCeremonyCommand"/> class.
    /// </summary>
    /// <param name="ceremonyId">The ID of the ceremony that the registration or verification began with.</param>
    /// <param name="credential">The credential that the browser returned, as WebAuthn JSON.</param>
    public FinishPasskeyCeremonyCommand(string ceremonyId, string credential)
    {
        CeremonyId = ceremonyId;
        Credential = credential;
    }

    /// <summary>
    /// Gets the ID of the ceremony that the registration or verification began with (see <see cref="PasskeyCeremony.Id"/>).
    /// </summary>
    public string CeremonyId { get; }

    /// <summary>
    /// Gets the credential that the browser returned, as WebAuthn JSON (the <c>PublicKeyCredential.toJSON()</c> form).
    /// </summary>
    public string Credential { get; }
}
