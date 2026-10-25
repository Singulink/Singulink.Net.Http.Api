namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a request to redeem a one-time sign-in code for a session.
/// </summary>
public sealed class RedeemSignInCodeCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RedeemSignInCodeCommand"/> class.
    /// </summary>
    /// <param name="code">The sign-in code.</param>
    /// <param name="codeVerifier">The PKCE code verifier that matches the code challenge the sign-in began with.</param>
    /// <param name="isPersistent">A value indicating whether the session is persistent.</param>
    public RedeemSignInCodeCommand(string code, string codeVerifier, bool isPersistent)
    {
        Code = code;
        CodeVerifier = codeVerifier;
        IsPersistent = isPersistent;
    }

    /// <summary>
    /// Gets the sign-in code (see <see cref="SignInCode.Code"/>).
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the PKCE code verifier that matches the code challenge the sign-in began with.
    /// </summary>
    public string CodeVerifier { get; }

    /// <summary>
    /// Gets a value indicating whether the session is persistent: it survives browser or application restarts.
    /// </summary>
    public bool IsPersistent { get; }
}
