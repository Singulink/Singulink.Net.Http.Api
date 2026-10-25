namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents the details of a device waiting to be linked, which the user who typed its code checks before approving it.
/// </summary>
public sealed class PendingDeviceLink
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PendingDeviceLink"/> class.
    /// </summary>
    /// <param name="device">The device that started the link.</param>
    /// <param name="ipAddress">The IP address of the device that started the link, or <see langword="null"/> if it is not known.</param>
    /// <param name="location">The approximate location of the device that started the link, or <see langword="null"/> if it is not known.</param>
    /// <param name="createdUtc">The time (in UTC) at which the link was started.</param>
    public PendingDeviceLink(string device, string? ipAddress, string? location, DateTime createdUtc)
    {
        Device = device;
        IpAddress = ipAddress;
        Location = location;
        CreatedUtc = createdUtc;
    }

    /// <summary>
    /// Gets the device that started the link.
    /// </summary>
    public string Device { get; }

    /// <summary>
    /// Gets the IP address of the device that started the link, or <see langword="null"/> if it is not known.
    /// </summary>
    public string? IpAddress { get; }

    /// <summary>
    /// Gets the approximate location of the device that started the link, as supplied by the application, or <see langword="null"/> if it is not known.
    /// </summary>
    public string? Location { get; }

    /// <summary>
    /// Gets the time (in UTC) at which the link was started.
    /// </summary>
    public DateTime CreatedUtc { get; }
}
