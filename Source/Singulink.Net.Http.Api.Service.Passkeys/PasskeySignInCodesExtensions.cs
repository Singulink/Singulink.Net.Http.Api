using Singulink.Net.Http.Api.Service.Passkeys;

namespace Singulink.Net.Http.Api.Service.SignIn;

/// <summary>
/// Extension methods for <see cref="SignInCodes"/> that continue a passkey sign-in or registration with a one-time sign-in code.
/// </summary>
public static class PasskeySignInCodesExtensions
{
    /// <summary>
    /// Creates a one-time sign-in code for the user of a passkey sign-in, bound to the PKCE code challenge the sign-in began with, so that the client redeems
    /// it with the same code verifier.
    /// </summary>
    /// <param name="signInCodes">The sign-in codes service.</param>
    /// <param name="passkey">The passkey that <see cref="PasskeyService.FinishSignInAsync"/> verified.</param>
    /// <param name="appData">An optional value of the app's that the code carries (see <see cref="SignInCodes.CreateAsync"/>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">The passkey was not verified by a sign-in that began with a code challenge, which can only happen when
    /// <see cref="PasskeyOptions.RequireCodeChallenge"/> is <see langword="false"/> or the passkey comes from a verification.</exception>
    public static Task<string> CreateAsync(
        this SignInCodes signInCodes, VerifiedPasskey passkey, string? appData = null, CancellationToken cancellationToken = default)
    {
        string codeChallenge = passkey.CodeChallenge ?? throw new InvalidOperationException(
            "The passkey sign-in did not begin with a code challenge, so it cannot continue with a sign-in code.");

        return signInCodes.CreateAsync(passkey.UserId, codeChallenge, appData, cancellationToken);
    }

    /// <summary>
    /// Creates a one-time sign-in code for the user of a passkey registration, bound to the PKCE code challenge the registration began with, so that the
    /// client redeems it with the matching code verifier.
    /// </summary>
    /// <param name="signInCodes">The sign-in codes service.</param>
    /// <param name="passkey">The passkey that <see cref="PasskeyService.FinishRegistrationAsync(string, string, string, string?, CancellationToken)"/>
    /// registered.</param>
    /// <param name="appData">An optional value of the app's that the code carries (see <see cref="SignInCodes.CreateAsync"/>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">The registration did not begin with a code challenge.</exception>
    public static Task<string> CreateAsync(
        this SignInCodes signInCodes, RegisteredPasskey passkey, string? appData = null, CancellationToken cancellationToken = default)
    {
        string codeChallenge = passkey.CodeChallenge ?? throw new InvalidOperationException(
            "The passkey registration did not begin with a code challenge, so it cannot continue with a sign-in code.");

        return signInCodes.CreateAsync(passkey.Passkey.UserId, codeChallenge, appData, cancellationToken);
    }
}
