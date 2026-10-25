namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a request from a new device to check the status of its device link.
/// </summary>
public sealed class CheckDeviceLinkCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CheckDeviceLinkCommand"/> class.
    /// </summary>
    /// <param name="id">The ID of the link.</param>
    /// <param name="pollSecret">The secret that the new device checks the link's status with.</param>
    public CheckDeviceLinkCommand(string id, string pollSecret)
    {
        Id = id;
        PollSecret = pollSecret;
    }

    /// <summary>
    /// Gets the ID of the link (see <see cref="DeviceLinkStart.Id"/>).
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the secret that the new device checks the link's status with (see <see cref="DeviceLinkStart.PollSecret"/>).
    /// </summary>
    public string PollSecret { get; }
}
