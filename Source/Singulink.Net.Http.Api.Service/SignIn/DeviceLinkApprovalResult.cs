using System.Diagnostics.CodeAnalysis;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Service.SignIn;

/// <summary>
/// The result of <see cref="DeviceLinks.ApproveAsync(string, string, string?, CancellationToken)"/>: its outcome, and the details of the device that was
/// approved.
/// </summary>
public sealed class DeviceLinkApprovalResult
{
    internal DeviceLinkApprovalResult(DeviceLinkApprovalOutcome outcome, PendingDeviceLink? link)
    {
        Outcome = outcome;
        Link = link;
    }

    /// <summary>
    /// Gets the outcome of the approval.
    /// </summary>
    public DeviceLinkApprovalOutcome Outcome { get; }

    /// <summary>
    /// Gets a value indicating whether the link was approved.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Link))]
    public bool IsApproved => Outcome is DeviceLinkApprovalOutcome.Approved;

    /// <summary>
    /// Gets the details of the device that was approved, as they were when the link was looked up for the approval, or <see langword="null"/> if the link
    /// was not approved.
    /// </summary>
    public PendingDeviceLink? Link { get; }
}
