namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Specifies how strongly the relying party asks authenticators for a feature in passkey ceremonies, with the values of WebAuthn's requirements (see
/// <see cref="PasskeyOptions.UserVerification"/> and <see cref="PasskeyOptions.DiscoverableCredentials"/>).
/// </summary>
public enum PasskeyRequirement
{
    /// <summary>
    /// The feature is required: a ceremony without it fails.
    /// </summary>
    Required = 1,

    /// <summary>
    /// The feature is preferred, and a ceremony without it succeeds.
    /// </summary>
    Preferred = 2,

    /// <summary>
    /// The feature is discouraged, and authenticators use it only when they must, such as one that always verifies its user.
    /// </summary>
    Discouraged = 3,
}
