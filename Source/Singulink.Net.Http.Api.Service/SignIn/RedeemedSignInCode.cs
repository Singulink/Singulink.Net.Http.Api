namespace Singulink.Net.Http.Api.Service.SignIn;

/// <summary>
/// A sign-in code that <see cref="SignInCodes.RedeemAsync"/> redeemed: the user it signs in, and the app's value it was created with.
/// </summary>
public sealed class RedeemedSignInCode
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RedeemedSignInCode"/> class.
    /// </summary>
    /// <param name="userId">The ID of the user that the code signs in.</param>
    /// <param name="appData">The app's value that the code was created with, or <see langword="null"/> if it was created without one.</param>
    public RedeemedSignInCode(string userId, string? appData = null)
    {
        UserId = userId;
        AppData = appData;
    }

    /// <summary>
    /// Gets the ID of the user that the code signs in.
    /// </summary>
    public string UserId { get; }

    /// <summary>
    /// Gets the app's value that the code was created with, or <see langword="null"/> if it was created without one (see
    /// <see cref="SignInCodes.CreateAsync"/> and <see cref="DeviceLinks.ApproveAsync(string, string, string?, CancellationToken)"/>).
    /// </summary>
    public string? AppData { get; }
}
