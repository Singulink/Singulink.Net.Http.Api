namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Represents a request from a signed-in device to approve a device link by the code that the new device shows, or to look up the details of the link
/// before approving it.
/// </summary>
public sealed class ApproveDeviceLinkCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApproveDeviceLinkCommand"/> class.
    /// </summary>
    /// <param name="code">The code of the link, as the user typed it.</param>
    public ApproveDeviceLinkCommand(string code)
    {
        Code = code;
    }

    /// <summary>
    /// Gets the code of the link (see <see cref="DeviceLinkStart.Code"/>), as the user typed it.
    /// </summary>
    public string Code { get; }
}
