namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents the status of a device link for the new device: whether the link was approved, and if so, the one-time sign-in code for the new device.
/// </summary>
public sealed class DeviceLinkStatus
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceLinkStatus"/> class.
    /// </summary>
    /// <param name="signInCode">The one-time sign-in code for the new device if the link was approved, otherwise <see langword="null"/>.</param>
    public DeviceLinkStatus(string? signInCode)
    {
        SignInCode = signInCode;
    }

    /// <summary>
    /// Gets the one-time sign-in code for the new device if the link was approved, or <see langword="null"/> if the link is still waiting for approval.
    /// The code is bound to the code challenge that the link was started with, so only the new device can redeem it. Every check after the approval gets
    /// the same code.
    /// </summary>
    public string? SignInCode { get; }
}
