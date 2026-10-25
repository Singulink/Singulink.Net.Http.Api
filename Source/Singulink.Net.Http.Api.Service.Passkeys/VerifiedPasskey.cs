using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.SignIn;

namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Represents a passkey that was verified in a sign-in or verification ceremony.
/// </summary>
public sealed class VerifiedPasskey
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VerifiedPasskey"/> class.
    /// </summary>
    /// <param name="passkey">The passkey, with the values it reported in the ceremony.</param>
    /// <param name="attachment">Where the passkey came from, as reported by the browser.</param>
    /// <param name="codeChallenge">The PKCE code challenge that the sign-in was bound to when it began, or <see langword="null"/> for none.</param>
    public VerifiedPasskey(PasskeyRecord passkey, PasskeyAttachment attachment, string? codeChallenge = null)
    {
        Passkey = passkey;
        Attachment = attachment;
        CodeChallenge = codeChallenge;
    }

    /// <summary>
    /// Gets the ID of the user the passkey belongs to.
    /// </summary>
    public string UserId => Passkey.UserId;

    /// <summary>
    /// Gets the passkey, with the values it reported in this ceremony. They have already been saved through <see cref="IPasskeyStore.UpdateAsync"/>.
    /// </summary>
    public PasskeyRecord Passkey { get; }

    /// <summary>
    /// Gets where the passkey came from, as reported by the browser.
    /// </summary>
    public PasskeyAttachment Attachment { get; }

    /// <summary>
    /// Gets the PKCE code challenge that the sign-in was bound to when it began, or <see langword="null"/> for verifications and for sign-ins that were
    /// begun without one.
    /// </summary>
    /// <remarks>
    /// The client presented the matching code verifier to finish the sign-in.
    /// <see cref="PasskeySignInCodesExtensions.CreateAsync(SignInCodes, VerifiedPasskey, string?, CancellationToken)"/> creates a one-time sign-in code
    /// bound to this challenge, which the client redeems with the same verifier.
    /// </remarks>
    public string? CodeChallenge { get; }
}
