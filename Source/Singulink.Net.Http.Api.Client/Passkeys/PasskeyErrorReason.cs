namespace Singulink.Net.Http.Api.Client.Passkeys;

/// <summary>
/// Specifies why a passkey operation failed.
/// </summary>
public enum PasskeyErrorReason
{
    /// <summary>
    /// The operation failed for a reason not covered by the other values.
    /// </summary>
    Unknown,

    /// <summary>
    /// The person cancelled the operation, for example by closing the passkey page.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The browser or authenticator didn't allow the operation. Browsers also report a dismissed or timed out passkey prompt this way, so
    /// <see cref="PasskeyAuthenticatorException.IsCancellation"/> is <see langword="true"/> for it, as for <see cref="Cancelled"/>.
    /// </summary>
    NotAllowed,

    /// <summary>
    /// The authenticator already holds a passkey from the creation options' exclude list: the account already has a passkey on it. Browsers report it as
    /// <c>InvalidStateError</c>.
    /// </summary>
    AlreadyOnAuthenticator,

    /// <summary>
    /// The browser or device doesn't support passkeys.
    /// </summary>
    NotSupported,

    /// <summary>
    /// The authenticator can't meet the requirements of the options, such as user verification or a passkey stored on the authenticator.
    /// </summary>
    RequirementsNotMet,

    /// <summary>
    /// The browser refused the operation for security reasons, for example because the relying party ID doesn't match the origin of the page.
    /// </summary>
    Security,

    /// <summary>
    /// The passkey page didn't open, for example because the browser couldn't be started.
    /// </summary>
    PageNotOpened,

    /// <summary>
    /// The passkey page didn't return to the app in time.
    /// </summary>
    TimedOut,
}
