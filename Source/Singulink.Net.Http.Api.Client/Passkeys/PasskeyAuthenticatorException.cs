namespace Singulink.Net.Http.Api.Client.Passkeys;

#pragma warning disable RCS1194 // Implement exception constructors

/// <summary>
/// The exception that is thrown when a passkey operation fails or is cancelled by the person.
/// </summary>
/// <remarks>
/// The message comes from the browser where it supplied one. It isn't localized or written for the person using the app; <see cref="Reason"/> tells the
/// failures apart.
/// </remarks>
public sealed class PasskeyAuthenticatorException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyAuthenticatorException"/> class with the default message for the reason.
    /// </summary>
    /// <param name="reason">The reason the operation failed.</param>
    public PasskeyAuthenticatorException(PasskeyErrorReason reason) : this(reason, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyAuthenticatorException"/> class.
    /// </summary>
    /// <param name="reason">The reason the operation failed.</param>
    /// <param name="message">The error message, or <see langword="null"/> to use a default message for the reason.</param>
    public PasskeyAuthenticatorException(PasskeyErrorReason reason, string? message) : this(reason, message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PasskeyAuthenticatorException"/> class.
    /// </summary>
    /// <param name="reason">The reason the operation failed.</param>
    /// <param name="message">The error message, or <see langword="null"/> to use a default message for the reason.</param>
    /// <param name="innerException">The exception that caused this exception, if any.</param>
    public PasskeyAuthenticatorException(PasskeyErrorReason reason, string? message, Exception? innerException)
        : base(message ?? GetDefaultMessage(reason), innerException)
    {
        Reason = reason;
    }

    /// <summary>
    /// Gets the reason the operation failed.
    /// </summary>
    public PasskeyErrorReason Reason { get; }

    /// <summary>
    /// Gets a value indicating whether the person may have cancelled the operation: <see langword="true"/> if <see cref="Reason"/> is
    /// <see cref="PasskeyErrorReason.Cancelled"/> or <see cref="PasskeyErrorReason.NotAllowed"/>. Browsers report a dismissed or timed out passkey prompt
    /// as <see cref="PasskeyErrorReason.NotAllowed"/>, the same as an operation they refuse.
    /// </summary>
    public bool IsCancellation => Reason is PasskeyErrorReason.Cancelled or PasskeyErrorReason.NotAllowed;

    private static string GetDefaultMessage(PasskeyErrorReason reason) => reason switch {
        PasskeyErrorReason.Cancelled => "The passkey operation was cancelled.",
        PasskeyErrorReason.NotAllowed => "The passkey operation was not allowed or timed out.",
        PasskeyErrorReason.AlreadyOnAuthenticator => "The authenticator already has a passkey for this account.",
        PasskeyErrorReason.NotSupported => "Passkeys are not supported here.",
        PasskeyErrorReason.RequirementsNotMet => "The authenticator can't meet the requirements of the passkey options.",
        PasskeyErrorReason.Security => "The passkey operation was refused for security reasons.",
        PasskeyErrorReason.PageNotOpened => "The passkey page could not be opened.",
        PasskeyErrorReason.TimedOut => "The passkey page did not return in time.",
        _ => "The passkey operation failed.",
    };
}
