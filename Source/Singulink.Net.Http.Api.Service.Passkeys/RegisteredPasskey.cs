using Singulink.Net.Http.Api.DataContracts;
using Singulink.Net.Http.Api.Service.SignIn;

namespace Singulink.Net.Http.Api.Service.Passkeys;

/// <summary>
/// Represents a passkey that was verified in a registration ceremony, for the app to store.
/// </summary>
public sealed class RegisteredPasskey
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RegisteredPasskey"/> class.
    /// </summary>
    /// <param name="passkey">The new passkey's data.</param>
    /// <param name="suggestedName">A name the passkey can start with.</param>
    /// <param name="attachment">Where the passkey was created, as reported by the browser.</param>
    /// <param name="codeChallenge">The PKCE code challenge that the registration was bound to when it began, or <see langword="null"/> for none.</param>
    public RegisteredPasskey(PasskeyRecord passkey, string suggestedName, PasskeyAttachment attachment, string? codeChallenge = null)
    {
        Passkey = passkey;
        SuggestedName = suggestedName;
        Attachment = attachment;
        CodeChallenge = codeChallenge;
    }

    /// <summary>
    /// Gets the new passkey's data. It has not been stored: the app stores it, usually with values of its own such as a name and the time it was created.
    /// </summary>
    public PasskeyRecord Passkey { get; }

    /// <summary>
    /// Gets a name the passkey can start with until the user renames it, such as "iCloud Keychain (iOS)". See <see cref="PasskeyNames.GetDefaultName"/>.
    /// </summary>
    public string SuggestedName { get; }

    /// <summary>
    /// Gets where the passkey was created, as reported by the browser.
    /// </summary>
    public PasskeyAttachment Attachment { get; }

    /// <summary>
    /// Gets the PKCE code challenge that the registration was bound to when it began, or <see langword="null"/> if it began without one.
    /// </summary>
    /// <remarks>
    /// <see cref="PasskeySignInCodesExtensions.CreateAsync(SignInCodes, RegisteredPasskey, string?, CancellationToken)"/> creates a one-time sign-in code
    /// bound to this challenge, which the client redeems with its code verifier.
    /// </remarks>
    public string? CodeChallenge { get; }
}
