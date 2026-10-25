namespace Singulink.Net.Http.Api.Service.SignIn;

/// <summary>
/// Specifies the outcome of <see cref="DeviceLinks.ApproveAsync(string, string, string?, CancellationToken)"/>.
/// </summary>
public enum DeviceLinkApprovalOutcome
{
    /// <summary>
    /// The link was approved. The new device receives a sign-in code the next time it checks the link's status.
    /// </summary>
    Approved,

    /// <summary>
    /// No device is waiting with the code: it was mistyped, expired or the device was already signed in.
    /// </summary>
    NotFound,

    /// <summary>
    /// The link was already approved, by this or another user.
    /// </summary>
    AlreadyApproved,
}
