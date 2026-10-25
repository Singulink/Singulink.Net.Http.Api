using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.DataContracts;

/// <summary>
/// Specifies the state of a chunked upload. Serialized to JSON as the name of the value.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UploadState>))]
public enum UploadState
{
    /// <summary>
    /// The upload is receiving chunks.
    /// </summary>
    Receiving,

    /// <summary>
    /// The upload's handler is running.
    /// </summary>
    Processing,

    /// <summary>
    /// The upload's handler has finished, and its outcome, which is its last item or its error, is kept for completion requests that come back for it.
    /// </summary>
    Completed,
}
