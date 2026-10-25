namespace Singulink.Net.Http.Api;

/// <summary>
/// Contains the error codes (<see cref="ApiException.ErrorCode"/>) of the exceptions thrown by the sign-in building blocks of the
/// <c>Singulink.Net.Http.Api.Service</c> package, so that apps and their clients can tell the failures apart and word them in their own way.
/// </summary>
public static class SignInErrorCodes
{
    /// <summary>
    /// The sign-in code is unknown, expired or already used, or the code verifier does not match it, so the user signs in again. Thrown as an
    /// <see cref="UnauthorizedApiException"/> by <c>SignInCodes.SignInAsync</c>.
    /// </summary>
    public const string SignInCodeInvalid = "sign-in-code-invalid";

    /// <summary>
    /// The user entered too many wrong device link codes and must wait before trying again. Thrown as a <see cref="TooManyRequestsApiException"/> by
    /// <c>DeviceLinks.FindAsync</c> and <c>DeviceLinks.ApproveAsync</c>.
    /// </summary>
    public const string DeviceLinkTooManyWrongCodes = "device-link-too-many-wrong-codes";

    /// <summary>
    /// The device link is unknown or expired, or the poll secret is wrong, so the new device starts a new link. Thrown as an
    /// <see cref="UnauthorizedApiException"/> by <c>DeviceLinks.CheckAsync</c>. On the client, <c>DeviceLinkWaiter.WaitForApprovalAsync</c> ends its wait by
    /// throwing it.
    /// </summary>
    public const string DeviceLinkExpired = "device-link-expired";

    /// <summary>
    /// The PKCE code challenge is missing or malformed. Thrown as a <see cref="BadRequestApiException"/> by <c>PkceChallenge.EnsureValid</c>, and so by
    /// <c>SignInCodes.CreateAsync</c>, <c>DeviceLinks.StartAsync</c>, <c>PasskeyService.BeginSignInAsync</c> and <c>PasskeyService.BeginRegistrationAsync</c>.
    /// </summary>
    public const string CodeChallengeInvalid = "code-challenge-invalid";
}
