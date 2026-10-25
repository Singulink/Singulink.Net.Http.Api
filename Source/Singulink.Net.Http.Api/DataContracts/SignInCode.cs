namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a one-time sign-in code, which signs the client in when it is redeemed with the PKCE code verifier that matches the code challenge the
/// sign-in began with.
/// </summary>
public sealed class SignInCode
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SignInCode"/> class.
    /// </summary>
    /// <param name="code">The sign-in code.</param>
    public SignInCode(string code)
    {
        Code = code;
    }

    /// <summary>
    /// Gets the sign-in code.
    /// </summary>
    public string Code { get; }
}
