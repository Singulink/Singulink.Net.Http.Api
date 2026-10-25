namespace Singulink.Net.Http.Api.Client.Passkeys;

/// <summary>
/// Runs passkey (WebAuthn) operations for an app: signing in, confirming the signed-in user and creating passkeys.
/// </summary>
/// <remarks>
/// Options and credentials are WebAuthn JSON: the app gets the options from its API, passes them in, and posts the returned credential's
/// <see cref="PasskeyCredential.Json"/> back to its API.
/// </remarks>
public interface IPasskeyAuthenticator
{
    /// <summary>
    /// Gets a value indicating whether passkeys can be used here.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Gets a value indicating whether operations are handed off to a passkey page in the system browser rather than run in the app's own page.
    /// </summary>
    bool UsesPasskeyPage { get; }

    /// <summary>
    /// Loads what operations need ahead of time, so that the first operation does not wait for it. Does nothing if there is nothing to load.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="PasskeyAuthenticatorException">Loading failed. The next operation tries again.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task PrepareAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a passkey credential (a WebAuthn assertion), to sign in or to confirm the signed-in user.
    /// </summary>
    /// <param name="requestOptionsJson">The request options from the app's API, as WebAuthn JSON (<c>PublicKeyCredentialRequestOptionsJSON</c>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The credential to post to the app's API, and whether the passkey was on this device.</returns>
    /// <exception cref="PasskeyAuthenticatorException">The operation failed or the person cancelled it. <see cref="PasskeyAuthenticatorException.Reason"/>
    /// says why.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<PasskeyCredential> GetCredentialAsync(string requestOptionsJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a passkey (a WebAuthn attestation).
    /// </summary>
    /// <param name="creationOptionsJson">The creation options from the app's API, as WebAuthn JSON (<c>PublicKeyCredentialCreationOptionsJSON</c>).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The new credential to post to the app's API, and whether the passkey was created on this device.</returns>
    /// <exception cref="PasskeyAuthenticatorException">The operation failed or the person cancelled it. <see cref="PasskeyAuthenticatorException.Reason"/>
    /// says why.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<PasskeyCredential> CreateCredentialAsync(string creationOptionsJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the browser, and through it the password manager, that a passkey is not registered with the relying party, so that it can stop offering
    /// the passkey. Does nothing if the browser can't be told, and ignores failures.
    /// </summary>
    /// <param name="credential">The credential of the passkey, as returned by <see cref="GetCredentialAsync"/>.</param>
    /// <param name="requestOptionsJson">The request options that the credential was returned for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <remarks>
    /// The service reports a passkey that is not registered with <see cref="PasskeyErrorCodes.NotRegistered"/>. A passkey that is registered to another
    /// user, which the service reports with <see cref="PasskeyErrorCodes.WrongAccount"/>, is not unknown to the relying party.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task SignalUnknownCredentialAsync(PasskeyCredential credential, string requestOptionsJson, CancellationToken cancellationToken = default);
}
