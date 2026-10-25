namespace Singulink.Net.Http.Api;

/// <summary>
/// Contains the error codes (<see cref="ApiException.ErrorCode"/>) of the exceptions thrown by <c>PasskeyService</c> in the
/// <c>Singulink.Net.Http.Api.Service.Passkeys</c> package, so that apps and their clients can tell the failures apart and word them in their own way.
/// </summary>
/// <remarks>
/// A sign-in that begins without a valid code challenge fails with <see cref="SignInErrorCodes.CodeChallengeInvalid"/>.
/// </remarks>
public static class PasskeyErrorCodes
{
    /// <summary>
    /// The ceremony is unknown, expired, already finished, or was started for something else (such as another user).
    /// Thrown as an <see cref="UnauthorizedApiException"/> for sign-ins, and a <see cref="ValidationApiException"/> for verifications and registrations.
    /// </summary>
    public const string CeremonyExpired = "passkey-ceremony-expired";

    /// <summary>
    /// The ceremony was bound to a code challenge and the code verifier is missing or does not match it, so the credential did not come back to the app
    /// that started the sign-in. Thrown as an <see cref="UnauthorizedApiException"/>.
    /// </summary>
    public const string CodeVerifierMismatch = "passkey-code-verifier-mismatch";

    /// <summary>
    /// The user has no passkeys to sign in or verify with. Thrown as an <see cref="UnauthorizedApiException"/> for sign-ins and a
    /// <see cref="ConflictApiException"/> for verifications.
    /// </summary>
    public const string NoPasskeys = "passkey-none";

    /// <summary>
    /// The passkey is not registered with the relying party, for example because it was removed. Thrown as an <see cref="UnauthorizedApiException"/> for
    /// sign-ins and a <see cref="ValidationApiException"/> for verifications. A passkey that is registered to another user is reported with
    /// <see cref="WrongAccount"/> instead.
    /// </summary>
    public const string NotRegistered = "passkey-not-registered";

    /// <summary>
    /// The passkey is registered, but to a different user than the one the sign-in or verification is for. Thrown as an
    /// <see cref="UnauthorizedApiException"/> for sign-ins and a <see cref="ValidationApiException"/> for verifications.
    /// </summary>
    public const string WrongAccount = "passkey-wrong-account";

    /// <summary>
    /// The credential failed verification. Thrown as an <see cref="UnauthorizedApiException"/> for sign-ins, and a <see cref="ValidationApiException"/>
    /// for verifications and registrations.
    /// </summary>
    public const string VerificationFailed = "passkey-verification-failed";

    /// <summary>
    /// The new passkey is already registered. Thrown as a <see cref="ConflictApiException"/>.
    /// </summary>
    public const string AlreadyRegistered = "passkey-already-registered";
}
