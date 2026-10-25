namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Specifies the state of the claim of an upload's handler run in an <see cref="IUploadStore"/>.
/// </summary>
public enum UploadRunState
{
    /// <summary>
    /// The run is not claimed.
    /// </summary>
    None,

    /// <summary>
    /// The run is claimed and the claim's lease is current.
    /// </summary>
    Running,

    /// <summary>
    /// The run is claimed and the claim's lease has lapsed, because the server that claimed it stopped renewing it.
    /// </summary>
    Lapsed,
}
