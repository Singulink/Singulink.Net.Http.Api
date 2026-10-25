namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Represents the values of a stored passkey that <see cref="PasskeyService"/> needs to run ceremonies with it.
/// </summary>
/// <remarks>
/// <para>
/// Passkey stores return instances of this class when passkeys are looked up, and
/// <see cref="PasskeyService.FinishRegistrationAsync(string, string, string, string?, CancellationToken)"/> returns one for a new passkey, for the app to
/// store. Apps usually keep more about each passkey than this, such as a name the user knows it by, the device it was added from and when it was created
/// and last used.
/// </para>
/// <para>
/// The credential ID identifies a passkey across all users. A registration of a credential ID that the store already has is rejected when it finishes,
/// and a unique constraint in the store also rejects one of two registrations of the same credential ID that finish at the same moment.
/// </para>
/// </remarks>
public sealed class PasskeyRecord
{
    /// <summary>
    /// The longest credential ID allowed by the WebAuthn specification, in bytes.
    /// </summary>
    public const int MaxCredentialIdLength = 1023;

    /// <summary>
    /// Gets the ID of the user the passkey belongs to, which is also the passkey's WebAuthn user handle: authenticators store it with the passkey and return
    /// it when signing in.
    /// </summary>
    /// <remarks>
    /// The ID must not contain personal information such as an email address or user name (the WebAuthn specification forbids it), must never change or be
    /// reused for another user, and must be 1 to <see cref="PasskeyUser.MaxIdLength"/> bytes in UTF-8. IDs are compared exactly, as strings and as UTF-8
    /// bytes, so a non-string ID formatted another way, such as with another culture, is another user ID.
    /// </remarks>
    public required string UserId { get; init; }

    /// <summary>
    /// Gets the credential ID, which identifies the passkey. It is at most <see cref="MaxCredentialIdLength"/> bytes long.
    /// </summary>
    public required byte[] CredentialId { get; init; }

    /// <summary>
    /// Gets the passkey's public key (in COSE format).
    /// </summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>
    /// Gets the signature counter the authenticator last reported. Many passkeys always report 0. Authenticators that count are checked to have
    /// counted up since the last use, which detects some cloned authenticators.
    /// </summary>
    public uint SignCount { get; init; }

    /// <summary>
    /// Gets the transports the authenticator reported when the passkey was created (such as <c>internal</c>, <c>hybrid</c> or <c>usb</c>). They are passed
    /// back to browsers to help them find the passkey. Transports this library does not know are kept as reported, but not passed back.
    /// </summary>
    public IReadOnlyList<string> Transports { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the passkey can be backed up or synced to other devices (by a password manager such as iCloud Keychain or Google
    /// Password Manager). This never changes for a passkey.
    /// </summary>
    public bool IsBackupEligible { get; init; }

    /// <summary>
    /// Gets a value indicating whether the passkey was backed up or synced when it was created or last used.
    /// </summary>
    public bool IsBackedUp { get; init; }

    /// <summary>
    /// Gets the AAGUID of the authenticator model or passkey provider, or <see cref="Guid.Empty"/> if it was not disclosed. See
    /// <see cref="PasskeyNames.GetProviderName"/>.
    /// </summary>
    public Guid AaGuid { get; init; }
}
