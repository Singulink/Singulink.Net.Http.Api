namespace Singulink.Net.Http.Api.Service.SignIn;

#pragma warning disable SA1513 // Closing brace should be followed by blank line

/// <summary>
/// Provides options for <see cref="DeviceLinks"/>.
/// </summary>
public sealed class DeviceLinkOptions
{
    /// <summary>
    /// Gets or sets how long a device link waits for approval, which the started link reports as <see cref="DataContracts.DeviceLinkStart.Lifetime"/>, and
    /// how long an approver's wrong codes are remembered after their last lookup. Must be positive. Default is 10 minutes.
    /// </summary>
    public TimeSpan Lifetime {
        get;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromMinutes(10);
}
