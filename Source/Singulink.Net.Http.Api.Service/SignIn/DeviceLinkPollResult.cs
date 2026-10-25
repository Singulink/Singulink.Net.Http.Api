using System.Diagnostics.CodeAnalysis;

namespace Singulink.Net.Http.Api.Service.SignIn;

/// <summary>
/// The status of a device link, returned to the new device by <see cref="DeviceLinks.PollAsync"/>.
/// </summary>
public sealed class DeviceLinkPollResult
{
    internal static readonly DeviceLinkPollResult Pending = new(null, null);

    internal DeviceLinkPollResult(string? signInCode, string? approverUserId)
    {
        SignInCode = signInCode;
        ApproverUserId = approverUserId;
    }

    /// <summary>
    /// Gets a value indicating whether the link was approved. If <see langword="false"/>, the link is still waiting for approval.
    /// </summary>
    [MemberNotNullWhen(true, nameof(SignInCode), nameof(ApproverUserId))]
    public bool IsApproved => SignInCode is not null;

    /// <summary>
    /// Gets the one-time sign-in code for the new device if the link was approved, otherwise <see langword="null"/>. The code is bound to the code challenge
    /// the link was started with, so only the new device can redeem it.
    /// </summary>
    public string? SignInCode { get; }

    /// <summary>
    /// Gets the ID of the user who approved the link and whom the sign-in code signs in, otherwise <see langword="null"/>.
    /// </summary>
    public string? ApproverUserId { get; }
}
